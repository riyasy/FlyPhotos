using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.ImageRendering;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Cmd = FlyPhotos.Display.Canvas.CanvasCommand;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The W2D-thread side of the main canvas: applies the commands <see cref="CanvasController"/> posts, ticks the view
/// and draws. It owns the renderer on screen, the lease on the item that renderer draws, and the checkerboard. Every
/// field is touched on the W2D thread only, unless marked, so the render path takes no lock.
/// <para>
/// It is also where every display item is finally freed (<see cref="DisplayItem.FreeSink"/>): a last release on any
/// thread queues the item here, so freeing never contends with a draw for the device.
/// </para>
/// <para>
/// The render loop pauses whenever nothing moves (<see cref="W2dQueue{T}.PauseIfIdle"/>); a posted command, a
/// renderer's invalidate or a <see cref="DelayedWake"/> wakes it.
/// </para>
/// </summary>
internal sealed class CanvasEngine
{
    /// <summary><see cref="Constants.ContinuousZoomSettleMs"/> in Stopwatch ticks.</summary>
    private static readonly long ContinuousZoomTicks = Stopwatch.Frequency * Constants.ContinuousZoomSettleMs / 1000;

    /// <summary>The Win2D control; the engine runs inside its Update and Draw.</summary>
    private readonly CanvasAnimatedControl _canvas;

    /// <summary>The thumbnail strip, redrawn when another photo is installed.</summary>
    private readonly IThumbnailController _thumbnails;

    /// <summary>The commands posted from other threads, applied at the top of each Update.</summary>
    private readonly W2dQueue<Cmd> _queue;

    /// <summary>What draws the photo on screen; null before the first install.</summary>
    private IRenderer _renderer;

    /// <summary>The lease on what <see cref="_renderer"/> draws; null for an animator (it has its own frames).</summary>
    private DisplayItem _item;

    /// <summary>The photo can be transparent: draw the checkerboard under it when that setting is on.</summary>
    private bool _checkered;

    /// <summary>Made on first use, and again after a device loss.</summary>
    private CanvasImageBrush _checkeredBrush;

    /// <summary>The page the user last asked for. Page steps count from it, so quick presses all count before the loads land.</summary>
    private int _requestedPageIndex;

    /// <summary>Stopwatch timestamp until which the last continuous zoom draws at animation quality.</summary>
    private long _continuousZoomUntil;

    /// <summary>Wakes the paused loop once a continuous zoom ends, for its full-quality frame.</summary>
    private readonly DelayedWake _continuousZoomEnd;

    /// <summary>This tick's frame: taken once in Update, so Draw and the hit test see the same one.</summary>
    private ViewFrame _frame;

    /// <summary>Callers of <see cref="Cmd.WaitForAnimation"/>, completed when the next animation arrives.</summary>
    private readonly List<TaskCompletionSource> _animationWaiters = [];

    /// <summary>Any thread: items whose last lease ended, freed together in one queued job.</summary>
    private readonly ConcurrentQueue<DisplayItem> _toFree = new();

    /// <summary>Any thread: 1 while a <see cref="Cmd.FreeReleasedItems"/> job is queued.</summary>
    private int _freeQueued;

    /// <summary>Guards <see cref="_hitTestFrame"/>, a struct too large to write atomically.</summary>
    private readonly Lock _hitTestLock = new();

    /// <summary>A copy of <see cref="_frame"/> for the UI thread's hit tests, so Draw itself takes no lock.</summary>
    private ViewFrame _hitTestFrame;

    /// <summary>Written each Update, read on the UI thread: see <see cref="IsAnimating"/>.</summary>
    private volatile bool _publishedAnimating;

    /// <summary>The view and its rules. Its events fire on the W2D thread.</summary>
    public ViewController ViewController { get; } = new();

    /// <summary>A page load landed: the page now on screen of the photo at its path. W2D thread.</summary>
    public event Action<MultiPagePosition> PageShown;

    /// <summary>Takes over freeing display items (<see cref="DisplayItem.FreeSink"/>) until <see cref="Dispose"/>.</summary>
    public CanvasEngine(CanvasAnimatedControl canvas, IThumbnailController thumbnails)
    {
        _canvas = canvas;
        _thumbnails = thumbnails;
        _queue = new W2dQueue<Cmd>(canvas, Apply);
        _continuousZoomEnd = new DelayedWake(Wake);
        ViewController.AnimationCompleted += CompleteAnimationWaiters;
        DisplayItem.FreeSink = QueueFree;
    }

    // ---------------------------------------------------------------- any thread

    /// <summary>Queues <paramref name="command"/> for the next Update and wakes the loop. Any thread.</summary>
    public void Post(Cmd command) => _queue.Enqueue(command);

    /// <summary>Wakes the render loop for another frame (a renderer's bitmap or a setting changed).</summary>
    public void Wake() => _queue.Wake();

    /// <summary>The last frame, for hit tests on the UI thread.</summary>
    public ViewFrame HitTestFrame
    {
        get { lock (_hitTestLock) return _hitTestFrame; }
    }

    /// <summary>An animation or a continuous zoom is running, as of the last frame.</summary>
    public bool IsAnimating => _publishedAnimating;

    // ---------------------------------------------------------------- W2D thread: commands

    /// <summary>Applies one command; called by <see cref="_queue"/> as it drains.</summary>
    private void Apply(Cmd command)
    {
        switch (command)
        {
            case Cmd.Install c: Install(c); break;
            case Cmd.Resized c: Resize(c); break;
            case Cmd.ChangePage c: ChangePage(c.Direction); break;
            case Cmd.PageLoaded c: OnPageLoaded(c); break;
            case Cmd.Renamed c: ViewController.Rename(c.OldPath, c.NewPath); break;
            case Cmd.ScalingQualityChanged: _renderer?.HandleScalingMethodChange(); break;
            case Cmd.WaitForAnimation c: AddAnimationWaiter(c.Done); break;
            case Cmd.FreeReleasedItems: FreeReleasedItems(); break;
            default:
                if (_renderer != null) ApplyToView(command); // nothing to zoom before the first photo
                break;
        }
    }

    /// <summary>Applies a view command (zoom, pan, rotate...) to the view.</summary>
    private void ApplyToView(Cmd command)
    {
        switch (command)
        {
            case Cmd.WheelZoom c: ViewController.WheelZoom(c.Direction, c.Anchor); break;
            case Cmd.ZoomBy c:
                ViewController.ZoomBy(c.Factor, c.Anchor, c.StopAtOneToOne);
                _continuousZoomUntil = Stopwatch.GetTimestamp() + ContinuousZoomTicks;
                break;
            case Cmd.StepZoom c: ViewController.StepZoom(c.Direction, c.Anchor); break;
            case Cmd.ZoomToHundred c: ViewController.ZoomToHundred(c.Anchor); break;
            case Cmd.Fit c: ViewController.Fit(c.Animate); break;
            case Cmd.Pan c: ViewController.Pan(c.Dx, c.Dy); break;
            case Cmd.Rotate c: ViewController.Rotate(c.Degrees); break;
            case Cmd.Shrug: ViewController.Shrug(); break;
            case Cmd.ExitZoom: ViewController.ExitZoom(); break;
        }
    }

    /// <summary>
    /// Swaps in the new renderer and sets the view for it in the same tick, so no frame shows one with the other.
    /// The old renderer is disposed before its item's lease ends, as it may still draw from that item.
    /// </summary>
    private void Install(Cmd.Install c)
    {
        if (!c.FollowUp) _requestedPageIndex = 0;

        var (oldRenderer, oldItem) = (_renderer, _item);
        (_renderer, _item, _checkered) = (c.Renderer, c.Item, c.Checkered);
        if (oldRenderer is AnimatedImageRenderer)
            _ = Task.Run(oldRenderer.Dispose); // it waits for its frame decode to finish; not on this thread
        else
            oldRenderer?.Dispose();
        oldItem?.Release();

        ViewController.Install(c.Photo, c.PhotoPath, c.LayoutSize, c.Rotation, c.Level, c.FollowUp);
        if (!c.FollowUp) _thumbnails.RequestThumbnailRedraw();
    }

    /// <summary>Hands a resize to the view: before the first photo it only records the size.</summary>
    private void Resize(Cmd.Resized c)
    {
        if (_renderer == null) ViewController.CanvasSize = c.NewSize;
        else if (c.ImageSizedShift is { } shift) ViewController.ImageSizedResize(shift, c.NewSize);
        else ViewController.Resize(c.NewSize);
    }

    /// <summary>Next or previous page, no wraparound. Steps from the page asked for last, so quick presses all count.</summary>
    private void ChangePage(NavDirection direction)
    {
        if (_renderer is not IMultiPage pages) return;
        var target = _requestedPageIndex + (direction == NavDirection.Next ? 1 : -1);
        if (target < 0 || target >= pages.PageCount) return;
        _requestedPageIndex = target;
        _ = LoadPageAsync(pages, target);
    }

    /// <summary>Loads a page off this thread and posts the result back; null (superseded or failed) posts nothing.</summary>
    private async Task LoadPageAsync(IMultiPage pages, int index)
    {
        if (await pages.LoadPageAsync(index) is { } size) Post(new Cmd.PageLoaded(pages, size));
    }

    /// <summary>
    /// A page landed. One of another size (ICO frames) gets a fresh fit; pages of one size (TIFF) keep the user's
    /// zoom and pan.
    /// </summary>
    private void OnPageLoaded(Cmd.PageLoaded c)
    {
        if (!ReferenceEquals(_renderer, c.Renderer)) return; // navigated away while it loaded
        var index = c.Renderer.CurrentPageIndex;
        var shown = ViewController.View.ImageRect;
        if (c.PageSize.Width != shown.Width || c.PageSize.Height != shown.Height)
            ViewController.ShowPage(c.PageSize, isFirstPage: index == 0);
        PageShown?.Invoke(new MultiPagePosition(ViewController.PhotoPath, index, c.Renderer.PageCount,
            (int)c.PageSize.Width, (int)c.PageSize.Height));
    }

    /// <summary>Adds a waiter, dropping those whose timeout already completed them, so waits on a still view don't pile up.</summary>
    private void AddAnimationWaiter(TaskCompletionSource done)
    {
        _animationWaiters.RemoveAll(w => w.Task.IsCompleted);
        _animationWaiters.Add(done);
    }

    /// <summary>The view's AnimationCompleted: releases everyone waiting on <see cref="Cmd.WaitForAnimation"/>.</summary>
    private void CompleteAnimationWaiters()
    {
        foreach (var done in _animationWaiters) done.TrySetResult();
        _animationWaiters.Clear();
    }

    // ---------------------------------------------------------------- W2D thread: frames

    /// <summary>Applies the queued commands, advances the animations, publishes the hit test, and pauses when idle.</summary>
    public void Update()
    {
        _queue.Drain();
        ViewController.Tick();
        var animationFrameDue = _renderer is AnimatedImageRenderer animated && animated.OnUpdate();

        _frame = ViewController.Frame;
        lock (_hitTestLock) _hitTestFrame = _frame;
        _publishedAnimating = IsAnimatingNow;
        // A continuous zoom doesn't keep the loop running: a wake at its end draws the full-quality frame.
        var continuousZoomLeft = _continuousZoomUntil - Stopwatch.GetTimestamp();
        if (continuousZoomLeft > 0) _continuousZoomEnd.After((int)(continuousZoomLeft * 1000 / Stopwatch.Frequency) + 1);
        _queue.PauseIfIdle(ViewController.IsAnimating || animationFrameDue);
    }

    /// <summary>The view is moving: draw at animation quality (faster interpolation, no detail renders).</summary>
    private bool IsAnimatingNow => ViewController.IsAnimating || Stopwatch.GetTimestamp() < _continuousZoomUntil;

    /// <summary>Draws this tick's frame: the checkerboard if needed, then the renderer.</summary>
    public void Draw(CanvasDrawingSession session)
    {
        session.Clear(Colors.Transparent);
        if (_renderer == null) return;

        var animating = IsAnimatingNow;
        var quality = AppConfig.Settings.ImageScalingQuality.ToCanvasInterpolation(animating);
        session.Units = CanvasUnits.Pixels;
        session.Transform = _frame.Mat;
        DrawCheckerboard(session);
        _renderer.Draw(session, _frame, quality, animating);
    }

    /// <summary>
    /// Under a transparent image when that setting is on. It needs aliased edges (a known Win2D pixel-alignment
    /// issue), so it turns antialiasing off for the image too.
    /// </summary>
    private void DrawCheckerboard(CanvasDrawingSession session)
    {
        var draw = _checkered && AppConfig.Settings.CheckeredBackground;
        session.Antialiasing = draw ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (!draw) return;
        _checkeredBrush ??= Util.CreateCheckeredBrush(_canvas, Constants.CheckerSize);
        // The session draws in layout pixels; this keeps the squares a fixed size on screen at any zoom.
        _checkeredBrush.Transform = Matrix3x2.CreateScale(1f / _frame.Scale);
        session.FillRectangle(_frame.ImageRect, _checkeredBrush);
    }

    /// <summary>
    /// The device was (re)created: drop the brush made on the old one, and let the renderer rebuild its scaled copies.
    /// Bitmaps held by the photo cache are not re-created (accepted risk).
    /// </summary>
    public void OnDeviceCreated()
    {
        _checkeredBrush?.Dispose();
        _checkeredBrush = null;
        _renderer?.HandleScalingMethodChange();
    }

    // ---------------------------------------------------------------- freeing items

    /// <summary>
    /// <see cref="DisplayItem.FreeSink"/>, any thread. One queued job per burst, so a far jump that drops hundreds of
    /// previews wakes the canvas once.
    /// </summary>
    private void QueueFree(DisplayItem item)
    {
        _toFree.Enqueue(item);
        if (Interlocked.Exchange(ref _freeQueued, 1) == 0) Post(Cmd.FreeReleasedItems.Instance);
    }

    /// <summary>Frees the queued items. The flag is cleared first, so an item queued meanwhile is freed by this loop or queues a fresh job.</summary>
    private void FreeReleasedItems()
    {
        Volatile.Write(ref _freeQueued, 0);
        while (_toFree.TryDequeue(out var item)) item.Free();
    }

    /// <summary>
    /// UI thread, after the render loop has stopped: frees the renderer and its lease, and from now on items are
    /// freed on whichever thread ends their last lease.
    /// </summary>
    public void Dispose()
    {
        _continuousZoomEnd.Cancel();
        _renderer?.Dispose();
        _renderer = null;
        _item?.Release();
        _item = null;
        DisplayItem.FreeSink = null;
        while (_toFree.TryDequeue(out var item)) item.Free();
        _checkeredBrush?.Dispose();
        _checkeredBrush = null;
    }
}
