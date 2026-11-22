using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Animators;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.ImageRendering;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NLog;
using Cmd = FlyPhotos.Display.Canvas.CanvasCommand;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The main canvas as the UI sees it. UI thread only, unless marked. Every request becomes a
/// <see cref="CanvasCommand"/> for the <see cref="CanvasEngine"/> on the W2D thread, which owns everything that is
/// drawn; this class keeps only UI state (the page label, the pending image-sized resize) and brings the engine's
/// events back to the UI thread.
/// </summary>
internal sealed class CanvasController : ICanvasController
{
    /// <summary>To the app log (%TEMP%\FlyPhotos\FlyPhotos.log), so failures show in release builds too.</summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>The zoom percentage, coalesced: the latest value wins.</summary>
    public event Action<int> ZoomChanged;

    /// <summary>The view started or stopped following the fit (the Fit button's state).</summary>
    public event Action<bool> FitToScreenStateChanged;

    /// <summary>The view's target became, or stopped being, 100% (the 1:1 button's state).</summary>
    public event Action<bool> OneToOneStateChanged;

    /// <summary>The photo on screen became, or stopped being, multi-page (shows the page buttons).</summary>
    public event Action<bool> MultiPageStateChanged;

    /// <summary>A page step landed on a new page; <see cref="CurrentPage"/> has it.</summary>
    public event Action PageChanged;

    /// <summary>The Win2D control the canvas draws into; its Update and Draw run on the W2D thread.</summary>
    private readonly CanvasAnimatedControl _canvas;

    /// <summary>The UI thread's queue, taken once: engine events are posted to it from the W2D thread.</summary>
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Everything on the W2D thread; reached only through posted commands and its thread-safe members.</summary>
    private readonly CanvasEngine _engine;

    /// <summary>Counts <see cref="SetSource"/> calls; an animator that finishes after a newer call is dropped.</summary>
    private int _latestSetSourceId;

    /// <summary>
    /// Set by <see cref="PrepareForImageSizedWindow"/>: the next SizeChanged to this canvas size is the window being
    /// sized to the image, and moves the canvas origin by Shift.
    /// </summary>
    private (Point Shift, Size CanvasSize)? _pendingImageSizedResize;

    /// <summary>W2D thread → UI: 1 while a zoom-percentage hand-off is queued, so at most one is.</summary>
    private int _zoomPercentPending;

    /// <summary>The percentage the queued hand-off delivers; it reads the latest when it runs.</summary>
    private int _zoomPercent;

    /// <summary>W2D thread: the last percentage queued, so an unchanged one queues nothing.</summary>
    private int _lastQueuedZoomPercent = -1;

    /// <summary>Backs <see cref="CurrentPage"/>; volatile, as other threads read it.</summary>
    private volatile MultiPagePosition _currentPage;

    /// <summary>The page on screen of a multi-page photo, or null. Written on the UI thread, read anywhere.</summary>
    public MultiPagePosition CurrentPage { get => _currentPage; private set => _currentPage = value; }

    /// <summary>Hooks the canvas's render loop and posts its current size, as its first SizeChanged may have passed.</summary>
    public CanvasController(CanvasAnimatedControl canvas, IThumbnailController thumbNailController)
    {
        _canvas = canvas;
        _dispatcher = canvas.DispatcherQueue;
        _engine = new CanvasEngine(canvas, thumbNailController);

        // Before the render loop is hooked: these fire on its thread.
        var view = _engine.ViewController;
        view.FitToScreenStateChanged += fitted => OnUi(() => FitToScreenStateChanged?.Invoke(fitted));
        view.OneToOneStateChanged += oneToOne => OnUi(() => OneToOneStateChanged?.Invoke(oneToOne));
        view.ZoomChanged += QueueZoomPercent;
        _engine.PageShown += page => OnUi(() => ShowPage(page));

        _canvas.CreateResources += Canvas_CreateResources;
        _canvas.Update += Canvas_Update;
        _canvas.Draw += Canvas_Draw;
        _canvas.SizeChanged += Canvas_SizeChanged;

        // The canvas may be laid out already, so its first SizeChanged has passed.
        _engine.Post(new Cmd.Resized(_canvas.GetSize()));
    }

    // ---------------------------------------------------------------- photos

    /// <summary>
    /// Shows <paramref name="item"/>, the photo's image at <paramref name="level"/>. The caller's lease on the item
    /// travels with the install. Each install is posted before the first await, so they reach the engine in call
    /// order; an animated image posts its first frame, then its animator once that is built.
    /// </summary>
    public async Task SetSource(Photo photo, DisplayItem item, DisplayLevel level)
    {
        var setSourceId = ++_latestSetSourceId; // even a null item supersedes a pending animator
        if (item == null) return;

        Cmd.Install install;
        try
        {
            var renderer = RendererFactory.Create(_canvas, photo, item, _engine.Wake);
            install = new Cmd.Install(photo, photo.FilePath, item, level, renderer, ViewRules.LayoutSize(photo, item),
                item.Rotation, Checkered: photo.SupportsTransparency);
        }
        catch
        {
            item.Release(); // nothing was installed, so the lease is still ours
            throw;
        }
        ShowPageButtons(install);
        _engine.Post(install);

        if (item is AnimatedHqDisplayItem animated)
            await InstallAnimatorAsync(setSourceId, animated, install);
    }

    /// <summary>Builds the animator and swaps it in for the first frame, unless a newer photo came meanwhile.</summary>
    private async Task InstallAnimatorAsync(int setSourceId, AnimatedHqDisplayItem item, Cmd.Install firstFrame)
    {
        try
        {
            var bytes = item.FileAsByteArray;
            IAnimator animator = item.Format switch
            {
                ImageFormat.Avif => await AvifAnimator.CreateAsync(bytes, _canvas),
                ImageFormat.Gif => await GifAnimator.CreateAsync(bytes, _canvas),
                ImageFormat.Webp => await WebpAnimator.CreateAsync(bytes, _canvas),
                ImageFormat.Png => await PngAnimator.CreateAsync(bytes, _canvas),
                _ => throw new ArgumentOutOfRangeException(nameof(item), item.Format, null)
            };
            try
            {
                // Checked again after the first-frame decode, which yields for GIF, WebP and APNG.
                if (setSourceId == _latestSetSourceId) await animator.UpdateAsync(TimeSpan.Zero);
                if (setSourceId != _latestSetSourceId)
                {
                    animator.Dispose();
                    return;
                }
            }
            catch
            {
                animator.Dispose();
                throw;
            }
            // The animator draws its own frames, so the first frame's lease ends with the swap (Item = null).
            _engine.Post(firstFrame with
            {
                Renderer = new AnimatedImageRenderer(_canvas, animator, _engine.Wake),
                Item = null,
                FollowUp = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "CanvasController - animator failed; the first frame stays up");
        }
    }

    /// <summary>
    /// The page buttons and label follow the install at once, before the engine applies it; a one-page PDF gets
    /// neither, like a one-page TIFF.
    /// </summary>
    private void ShowPageButtons(Cmd.Install install)
    {
        var pages = install.Renderer is IMultiPage { PageCount: > 1 } m ? m : null;
        var wasMultiPage = CurrentPage != null;
        CurrentPage = pages == null
            ? null
            : new MultiPagePosition(install.PhotoPath, 0, pages.PageCount, (int)install.LayoutSize.Width, (int)install.LayoutSize.Height);
        if ((pages != null) != wasMultiPage) MultiPageStateChanged?.Invoke(pages != null);
    }

    /// <summary>The engine's PageShown, on the UI thread: updates the page label if that photo is still up.</summary>
    private void ShowPage(MultiPagePosition page)
    {
        if (CurrentPage?.Path != page.Path) return; // another photo is up by now
        CurrentPage = page;
        PageChanged?.Invoke();
    }

    /// <summary>A photo was renamed: the canvas knows photos by path, so it follows the new one.</summary>
    public void OnPhotoRenamed(string oldPath, string newPath)
    {
        if (CurrentPage is { } page && page.Path == oldPath) CurrentPage = page with { Path = newPath };
        _engine.Post(new Cmd.Renamed(oldPath, newPath));
    }

    /// <summary>Next or previous page of a multi-page photo; nothing at either end.</summary>
    public void ChangePage(NavDirection direction) => _engine.Post(new Cmd.ChangePage(direction));

    // ---------------------------------------------------------------- view

    /// <summary>A wheel notch or zoom key, animated, keeping <paramref name="anchor"/> (default: the centre) still.</summary>
    public void WheelZoom(ZoomDirection direction, Point? anchor = null) => _engine.Post(new Cmd.WheelZoom(direction, anchor));

    /// <summary>
    /// Smooth zoom by wheel delta, at once (touchpad scrolling, the right-button hold): 120 units scale by 1.25, and a
    /// small delta by proportionally less.
    /// </summary>
    public void SmoothZoom(double delta, Point anchor) =>
        ZoomBy((float)Math.Pow(1.25, delta / 120.0), anchor);

    /// <summary>Shift + wheel: each notch scales by FineZoomStepPercent, and stops at 1:1 on the way past.</summary>
    public void FineZoom(int delta, Point anchor) =>
        ZoomBy((float)Math.Pow(1 + AppConfig.Settings.FineZoomStepPercent / 100.0, delta / 120.0),
            anchor, stopAtOneToOne: true);

    /// <summary>Scales by <paramref name="factor"/> at once around <paramref name="anchor"/>: pinch, and the two zooms above.</summary>
    public void ZoomBy(float factor, Point anchor, bool stopAtOneToOne = false)
    {
        if (factor == 1f) return; // would only keep the continuous-zoom quality on
        _engine.Post(new Cmd.ZoomBy(factor, anchor, stopAtOneToOne));
    }

    /// <summary>The next of fit, 100% and 400%: centred, or around <paramref name="anchor"/>.</summary>
    public void StepZoom(ZoomDirection direction, Point? anchor = null) => _engine.Post(new Cmd.StepZoom(direction, anchor));

    /// <summary>100%: centred, or keeping <paramref name="anchor"/> over the same image pixel.</summary>
    public void ZoomToHundred(Point? anchor = null) => _engine.Post(new Cmd.ZoomToHundred(anchor));

    /// <summary>The closing animation; the window waits for it with <see cref="WaitForPanZoomAnimationAsync"/>.</summary>
    public void ZoomOutOnExit() => _engine.Post(new Cmd.ExitZoom());

    /// <summary>Fits the image to the canvas (upscaling a small one); at once when not <paramref name="animateChange"/>.</summary>
    public void FitToScreen(bool animateChange) => _engine.Post(new Cmd.Fit(animateChange));

    /// <summary>Moves the image by (<paramref name="dx"/>, <paramref name="dy"/>) canvas pixels.</summary>
    public void Pan(double dx, double dy) => _engine.Post(new Cmd.Pan(dx, dy));

    /// <summary>Turns the view (not the file) by 90° about the image centre.</summary>
    public void RotateCurrentPhotoBy90(bool clockWise) => _engine.Post(new Cmd.Rotate(clockWise ? 90 : -90));

    /// <summary>A small shake: an action was refused (e.g. delete before the HQ is on screen).</summary>
    public void Shrug() => _engine.Post(new Cmd.Shrug());

    /// <summary>Completes when the next pan/zoom animation arrives, or after <paramref name="timeoutMs"/> at the latest.</summary>
    public Task WaitForPanZoomAnimationAsync(int timeoutMs)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Post(new Cmd.WaitForAnimation(done));
        _ = Task.Delay(timeoutMs).ContinueWith(_ => done.TrySetResult());
        return done.Task;
    }

    /// <summary>True while a pan/zoom animation or a continuous zoom runs, as of the last frame.</summary>
    public bool IsPanZoomAnimating => _engine.IsAnimating;

    // ---------------------------------------------------------------- hit tests

    /// <summary>Whether canvas point <paramref name="position"/> is on the image, as of the last frame.</summary>
    public bool IsPressedOnImage(Point position)
    {
        var frame = _engine.HitTestFrame;
        var r = frame.ImageRect;
        if (r.Width <= 0 || r.Height <= 0) return false; // nothing drawn yet
        var p = Vector2.Transform(new Vector2((float)position.X, (float)position.Y), frame.MatInv);
        return p.X >= r.X && p.Y >= r.Y && p.X <= r.Right && p.Y <= r.Bottom;
    }

    /// <summary>The image's bounds on the canvas, in physical pixels; false with no image.</summary>
    public bool TryGetDisplayedImageBounds(out Rect bounds)
    {
        var frame = _engine.HitTestFrame;
        var r = frame.ImageRect;
        bounds = default;
        if (r.Width <= 0 || r.Height <= 0) return false;
        // Rotation is in 90° steps, so two opposite corners span the box.
        var a = Vector2.Transform(new Vector2((float)r.Left, (float)r.Top), frame.Mat);
        var b = Vector2.Transform(new Vector2((float)r.Right, (float)r.Bottom), frame.Mat);
        bounds = new Rect(new Point(a.X, a.Y), new Point(b.X, b.Y));
        return true;
    }

    // ---------------------------------------------------------------- window

    /// <summary>
    /// The window is about to be sized to the image: the next canvas resize keeps the image where it is on screen.
    /// </summary>
    /// <param name="canvasOriginShift">How far the canvas origin moves on screen, in physical pixels.</param>
    /// <param name="canvasWidth">The canvas width the resize will produce, in physical pixels.</param>
    /// <param name="canvasHeight">The canvas height the resize will produce, in physical pixels.</param>
    public void PrepareForImageSizedWindow(Point canvasOriginShift, double canvasWidth, double canvasHeight)
    {
        var newSize = new Size(canvasWidth, canvasHeight);
        var size = _canvas.GetSize();
        if (IsSameCanvasSize(size, newSize)) // no SizeChanged will follow
            _engine.Post(new Cmd.Resized(size, canvasOriginShift));
        else
            _pendingImageSizedResize = (canvasOriginShift, newSize);
    }

    /// <summary>Posts the resize in physical pixels, as image-sized if <see cref="PrepareForImageSizedWindow"/> announced it.</summary>
    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var newSize = args.NewSize.AdjustForDpi(_canvas);
        // The marker applies only to the resize it was prepared for, and is spent either way.
        Point? shift = null;
        if (_pendingImageSizedResize is { } pending)
        {
            _pendingImageSizedResize = null;
            if (IsSameCanvasSize(newSize, pending.CanvasSize)) shift = pending.Shift;
        }
        _engine.Post(new Cmd.Resized(newSize, shift));
    }

    /// <summary>Equal within a pixel and a half: layout rounds DIPs to physical pixels.</summary>
    private static bool IsSameCanvasSize(Size a, Size b) =>
        Math.Abs(a.Width - b.Width) <= 1.5 && Math.Abs(a.Height - b.Height) <= 1.5;

    // ---------------------------------------------------------------- settings

    /// <summary>The checkerboard setting changed: one redraw, as Draw reads the setting each frame.</summary>
    public void HandleCheckeredBackgroundChange() => _engine.Wake();

    /// <summary>The image scaling setting changed: the renderer rebuilds its scaled copies.</summary>
    public void HandleImageScalingQualityChange() => _engine.Post(new Cmd.ScalingQualityChanged());

    // ---------------------------------------------------------------- render loop (W2D thread)
    // Win2D's CanvasAnimatedControl raises these on its game-loop thread; the engine does the work.

    /// <summary>
    /// The control made its device, first at load and again after a device loss: device-bound resources (brushes,
    /// scaled copies) must be rebuilt.
    /// </summary>
    private void Canvas_CreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args) =>
        _engine.OnDeviceCreated();

    /// <summary>Once per frame, before Draw: applies the posted commands and advances the view; pauses the loop when idle.</summary>
    private void Canvas_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args) => _engine.Update();

    /// <summary>Once per frame, after Update: draws the frame Update took.</summary>
    private void Canvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args) =>
        _engine.Draw(args.DrawingSession);

    /// <summary>
    /// W2D thread (the view's ZoomChanged): hands the percentage to the UI. At most one hand-off is queued, and it
    /// delivers the latest value, so a fast animation can't flood the UI queue.
    /// </summary>
    private void QueueZoomPercent(float scale)
    {
        var percent = (int)Math.Round(scale * 100);
        if (percent == _lastQueuedZoomPercent) return;
        _lastQueuedZoomPercent = percent;
        Volatile.Write(ref _zoomPercent, percent);
        if (Interlocked.CompareExchange(ref _zoomPercentPending, 1, 0) == 0)
            OnUi(() =>
            {
                Volatile.Write(ref _zoomPercentPending, 0);
                ZoomChanged?.Invoke(Volatile.Read(ref _zoomPercent));
            });
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread. Any thread.</summary>
    private void OnUi(DispatcherQueueHandler action) => _dispatcher.TryEnqueue(action);

    // ---------------------------------------------------------------- cleanup

    /// <summary>UI thread, when the window closes. Stops the render loop first, so nothing draws while it is freed.</summary>
    public void Dispose()
    {
        try
        {
            _canvas.RemoveFromVisualTree();
            _canvas.CreateResources -= Canvas_CreateResources;
            _canvas.Update -= Canvas_Update;
            _canvas.Draw -= Canvas_Draw;
            _canvas.SizeChanged -= Canvas_SizeChanged;
            _engine.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
}
