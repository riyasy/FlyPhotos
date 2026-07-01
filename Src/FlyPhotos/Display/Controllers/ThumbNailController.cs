using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers.Animation;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using FlyPhotos.UI.Views;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Renders the thumbnail strip on its own <see cref="CanvasAnimatedControl"/> — a dedicated swap-chain
/// render thread (the "W2D thread"), separate from the main canvas. This mirrors the threading model on
/// <see cref="CanvasController"/>: everything that touches rendering state runs on the W2D thread and the
/// only door in is <see cref="_pendingW2dActions"/> (drained at the top of <see cref="D2dCanvasThumbNail_Update"/>)
/// plus the <see cref="_rebuildRequested"/> flag. The UI thread never touches the offscreen, the cached
/// bitmaps, or the slide state directly — it posts work and wakes the loop. The control is paused whenever
/// it isn't sliding or rebuilding, so at rest it costs nothing.
/// <para>
/// Reading the photo collection on the W2D thread is safe: <see cref="PhotoList"/> exposes a lock-free
/// <c>GetPhoto</c> and a volatile copy-on-write <c>Keys</c> snapshot expressly for non-UI readers.
/// </para>
/// </summary>
internal partial class ThumbNailController : IThumbnailController
{
    public event Action<int> ThumbnailClicked;

    private readonly CanvasAnimatedControl _d2dCanvasThumbNail;
    private readonly PhotoSessionState _photoSessionState;
    private IPhotoProvider _provider;
    private Func<int, bool> _isPreviewLoaded;

    private int _numOfThumbNailsInOneDirection = 20;
    private int _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;
    private Color _thumbNailSelectionColor;

    // W2D-owned: created/used/disposed only on the W2D thread (build + draw + CreateResources).
    private CanvasRenderTarget _thumbnailOffscreen;
    private CanvasBitmap _loadingIndicatorBitmap;

    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(150);
    private readonly DispatcherTimer _throttledRedrawTimer = new() { Interval = ThrottleInterval };
    private bool _redrawNeeded;
    private bool _canDrawThumbnails;

    // The single UI/other-thread -> W2D handoff. Arbitrary device-free work (slide reset, offscreen
    // disposal) is queued here; the heavier offscreen rebuild is flagged via _rebuildRequested because it
    // needs the live W2D `sender` for the device and render size. Both are drained at the top of Update.
    private readonly ConcurrentQueue<Action> _pendingW2dActions = new();
    private int _rebuildRequested;

    // Slide animation (W2D-owned): the strip is always rendered centered on the current photo, but on
    // navigation we seed a horizontal offset (delta * boxSize) so it appears to start at the old position,
    // then spring it back to 0 — a damped pixel-space slide stepped each frame in Update.
    private readonly SpringAxis _slideOffset = new();
    private int _lastRenderedPosition = -1;
    private bool _slideAnimating;

    // Off-screen is rendered this many pixels wider than the canvas on each side (see
    // ThumbnailSlideMarginBoxes) so the sliding strip never exposes a transparent edge.
    private int _offscreenMarginPx;

    // --- Initialization ---

    public ThumbNailController(CanvasAnimatedControl d2dCanvasThumbNail, PhotoSessionState photoSessionState)
    {
        _d2dCanvasThumbNail = d2dCanvasThumbNail;
        _photoSessionState = photoSessionState;
        _d2dCanvasThumbNail.CreateResources += D2dCanvasThumbNail_CreateResources;
        _d2dCanvasThumbNail.Update += D2dCanvasThumbNail_Update;
        _d2dCanvasThumbNail.Draw += D2dCanvasThumbNail_Draw;
        _d2dCanvasThumbNail.SizeChanged += D2dCanvasThumbNail_SizeChanged;
        _d2dCanvasThumbNail.Loaded += D2dCanvasThumbNail_Loaded;
        _throttledRedrawTimer.Tick += ThrottledRedrawTimer_Tick;
        _d2dCanvasThumbNail.PointerPressed += D2dCanvasThumbNail_PointerPressed;
        _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
    }

    public void SetPhotoProvider(IPhotoProvider provider) => _provider = provider;

    public void SetPreviewLoadedProbe(Func<int, bool> isPreviewLoaded) => _isPreviewLoaded = isPreviewLoaded;

    // --- Canvas event handlers ---

    private void D2dCanvasThumbNail_Loaded(object sender, RoutedEventArgs e)
    {
        _d2dCanvasThumbNail.Visibility = AppConfig.Settings.ShowThumbnails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void D2dCanvasThumbNail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The render size moved; rebuild on the W2D thread (which recomputes the visible-box count from
        // the live size). _numOfThumbNailsInOneDirection is also kept roughly current for the UI-thread
        // visibility heuristic in RedrawThumbNailsIfNeeded.
        _numOfThumbNailsInOneDirection = (int)(_d2dCanvasThumbNail.ActualWidth / (2 * _thumbnailBoxSize)) + 1;
        RequestRebuild();
    }

    // Device (re)created (first load or device loss). Drop the device-bound surfaces so they rebuild.
    // Cached photo.Thumbnail.Bitmap objects belong to the old device and are NOT cleared here — the same
    // accepted device-loss risk the main canvas carries. Runs on the W2D thread.
    private void D2dCanvasThumbNail_CreateResources(CanvasAnimatedControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _thumbnailOffscreen?.Dispose();
        _thumbnailOffscreen = null;
        _loadingIndicatorBitmap?.Dispose();
        _loadingIndicatorBitmap = null;
        RequestRebuild();
    }

    /// <summary>
    /// W2D thread, once per frame before Draw. Drains queued work, rebuilds the offscreen if requested,
    /// steps the slide spring, and pauses the loop when there is nothing left to do.
    /// </summary>
    private void D2dCanvasThumbNail_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        // ① Apply queued UI -> W2D requests (slide reset, offscreen disposal).
        while (_pendingW2dActions.TryDequeue(out var action))
            action();

        // ② Rebuild the offscreen ribbon if asked. Needs the live `sender` for the device + render size.
        if (Interlocked.Exchange(ref _rebuildRequested, 0) == 1)
            BuildRibbon(sender);

        // ③ Drive the slide. dt comes from the AOT-safe Timing value struct (no projected-arg cast).
        if (_slideAnimating)
        {
            var dt = (float)args.Timing.ElapsedTime.TotalSeconds;
            if (dt > Constants.SpringMaxDtSeconds) dt = Constants.SpringMaxDtSeconds;
            if (dt > 0f)
            {
                _slideOffset.Step(0f, dt);
                if (_slideOffset.IsSettled(0f, Constants.SpringPanSettleEpsilon, Constants.SpringPanVelocitySettle))
                    StopSlide();
            }
        }

        // ④ Pause when idle. Re-check after pausing so an enqueue/flag that landed during the decision
        //    isn't lost (closes the lost-wakeup race without a lock, mirroring CanvasController).
        if (!WorkPending())
        {
            sender.Paused = true;
            if (WorkPending())
                sender.Paused = false;
        }
    }

    private bool WorkPending() =>
        !_pendingW2dActions.IsEmpty || Volatile.Read(ref _rebuildRequested) == 1 || _slideAnimating;

    /// <summary>W2D thread. Blits the pre-rendered ribbon, shifted by the current slide offset.</summary>
    private void D2dCanvasThumbNail_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        args.DrawingSession.Clear(Colors.Transparent);
        if (_thumbnailOffscreen != null && AppConfig.Settings.ShowThumbnails)
        {
            // The off-screen is wider than the canvas by _offscreenMarginPx on each side; shift it left
            // by that margin so its center thumbnail lands at the canvas center, then add the slide offset.
            var drawRect = new Rect(-_offscreenMarginPx, 0, _thumbnailOffscreen.Size.Width, _thumbnailOffscreen.Size.Height);
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.CreateTranslation(_slideOffset.Position, 0f);
            args.DrawingSession.DrawImage(_thumbnailOffscreen, drawRect, _thumbnailOffscreen.Bounds, 0.8f,
                CanvasImageInterpolation.Linear);

            // Selection highlight: a fixed frame over the center box, untranslated, so it stays put while
            // the strip slides beneath it. At rest (offset 0) it lines up exactly with the centered photo.
            var centerBoxX = (int)sender.Size.Width / 2 - _thumbnailBoxSize / 2;
            var selRect = new Rect(
                centerBoxX + Constants.ThumbnailPadding,
                Constants.ThumbnailPadding,
                _thumbnailBoxSize - 2 * Constants.ThumbnailPadding,
                _thumbnailBoxSize - 2 * Constants.ThumbnailPadding);
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.Identity;
            args.DrawingSession.DrawRoundedRectangle(selRect, Constants.ThumbnailCornerRadius,
                Constants.ThumbnailCornerRadius, _thumbNailSelectionColor, Constants.ThumbnailSelectionBorderThickness);
        }
    }

    private void D2dCanvasThumbNail_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_d2dCanvasThumbNail).Properties.PointerUpdateKind
            is not Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;

        var keys = _provider?.Keys;
        if (keys == null || keys.Count <= 1) return;

        var pos = e.GetCurrentPoint(_d2dCanvasThumbNail).Position;
        double canvasCenterX = _d2dCanvasThumbNail.ActualWidth / 2;
        double clickedX = pos.X;

        // 1. Calculate the positional offset. This logic was correct.
        int offset = (int)Math.Round((clickedX - canvasCenterX) / _thumbnailBoxSize);

        if (offset == 0) return; // No navigation needed if the center thumbnail is clicked.

        // 2. Find the POSITION of the current key.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return;

        // 3. Calculate the new target POSITION.
        int newPosition = currentPosition + offset;

        // 4. Validate the NEW POSITION against the bounds of the sorted key list.
        if (newPosition >= 0 && newPosition < keys.Count)
        {
            // 5. Fire the event. The PhotoDisplayController will handle the navigation.
            ThumbnailClicked?.Invoke(offset);
        }
    }

    // --- Public API ---

    public void ShowHideThumbnailBasedOnSettings()
    {
        RunOnUiThread(() =>
        {
            if (AppConfig.Settings.ShowThumbnails)
            {
                _d2dCanvasThumbNail.Visibility = Visibility.Visible;
                _canDrawThumbnails = true;
                RequestRebuild();
            }
            else
            {
                _d2dCanvasThumbNail.Visibility = Visibility.Collapsed;
                // Reset slide + dispose the offscreen on the W2D thread so there's no race with Draw.
                EnqueueW2dAction(() =>
                {
                    ResetSlide();
                    _thumbnailOffscreen?.Dispose();
                    _thumbnailOffscreen = null;
                });
            }
        });
    }

    public void RefreshThumbnail()
    {
        RunOnUiThread(() =>
        {
            _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
            _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;
            // Box size changed → pixel-space offset is stale; reset so the next render doesn't slide.
            EnqueueW2dAction(ResetSlide);
            if (AppConfig.Settings.ShowThumbnails)
                RequestRebuild();
        });
    }

    /// <summary>
    /// Called when an external thumbnail has been loaded.
    /// This method throttles redraw requests to prevent overwhelming the UI thread.
    /// </summary>
    public void RedrawThumbNailsIfNeeded(int updatedKey)
    {
        // Capture the snapshot once — this method is called from a ThreadPool continuation and the
        // list may be swapped by DeleteCurrentPhoto on the UI thread at any moment.
        var keys = _provider?.Keys;
        if (keys == null || keys.Count == 0) return;

        // Find the POSITION of the current and updated keys.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        int updatedPosition = keys.BinarySearch(updatedKey);

        // If either key isn't found (e.g., already deleted), we can't proceed.
        if (currentPosition < 0 || updatedPosition < 0) return;

        // Check if the updated thumbnail is within the visible POSITIONAL range.
        if (updatedPosition >= currentPosition - _numOfThumbNailsInOneDirection &&
            updatedPosition <= currentPosition + _numOfThumbNailsInOneDirection)
        {
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() =>
            {
                _canDrawThumbnails = true;
                _redrawNeeded = true;
                if (!_throttledRedrawTimer.IsEnabled)
                {
                    _throttledRedrawTimer.Start();
                }
            });
        }
    }

    /// <summary>
    /// Requests a rebuild of the off-screen thumbnail strip. Thread-safe and cheap — it just flags the
    /// W2D loop, which does the actual (expensive) render on its own thread. Safe to call from the UI
    /// thread or from the main canvas's W2D thread (CanvasController.InstallRenderer).
    /// </summary>
    public void RequestThumbnailRedraw() => RequestRebuild();

    // --- Slide animation ---

    /// <summary>
    /// Seeds and starts (or feeds) the slide spring for a navigation of <paramref name="delta"/>
    /// positions. Small steps glide; large jumps (Home/End, far clicks) snap to avoid a long slide
    /// and transparent edge gaps beyond the rendered margin. W2D thread only.
    /// </summary>
    private void BeginSlide(int delta)
    {
        if (delta == 0) return;

        if (Math.Abs(delta) > _numOfThumbNailsInOneDirection)
        {
            StopSlide();
            return;
        }

        // Start the new strip shifted to the old position, then spring back to 0. Keep any existing
        // velocity so rapid repeated steps (held arrow key) compound into one smooth flow. Clamp to the
        // rendered margin so the strip never lags further than the boxes we drew — otherwise a gap shows.
        _slideOffset.Position += delta * _thumbnailBoxSize;
        _slideOffset.Position = Math.Clamp(_slideOffset.Position, -_offscreenMarginPx, _offscreenMarginPx);
        // Called from BuildRibbon inside Update, so the loop is already running; just mark it animating so
        // this frame's pause-check keeps it alive.
        _slideAnimating = true;
    }

    /// <summary>Ends an in-flight slide, snapping the offset to 0. W2D thread only.</summary>
    private void StopSlide()
    {
        _slideOffset.SettleTo(0f);
        _slideAnimating = false;
    }

    /// <summary>Stops the slide and forgets the last position so the next render doesn't animate. W2D thread.</summary>
    private void ResetSlide()
    {
        StopSlide();
        _lastRenderedPosition = -1;
    }

    // --- Rendering internals ---

    /// <summary>
    /// Renders the entire visible thumbnail strip to the off-screen bitmap. Runs on the W2D thread from
    /// <see cref="D2dCanvasThumbNail_Update"/>; <paramref name="sender"/> provides the device and size.
    /// </summary>
    private void BuildRibbon(ICanvasAnimatedControl sender)
    {
        // Capture the key snapshot once — lock-free per the PhotoList contract.
        var keys = _provider?.Keys;
        if (keys == null || keys.Count < 1 || !_canDrawThumbnails || !AppConfig.Settings.ShowThumbnails)
        {
            _thumbnailOffscreen?.Dispose();
            _thumbnailOffscreen = null;
            return;
        }

        // Find the position of the currently displayed photo. Guard BEFORE (re)creating the offscreen, so
        // an invalid position can never leave a freshly allocated, uncleared render target for Draw to blit.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return; // Can't draw if the current photo is invalid.

        var canvasWidth = (float)sender.Size.Width;
        if (canvasWidth <= 0) return; // Guard against drawing with no size

        _offscreenMarginPx = Constants.ThumbnailSlideMarginBoxes * _thumbnailBoxSize;
        var offscreenWidth = canvasWidth + 2 * _offscreenMarginPx;

        // Recreate render target if needed (e.g., after size change)
        if (_thumbnailOffscreen == null ||
            (int)Math.Round(_thumbnailOffscreen.Size.Width) != (int)Math.Round(offscreenWidth) ||
            (int)Math.Round(_thumbnailOffscreen.Size.Height) != _thumbnailBoxSize)
        {
            _thumbnailOffscreen?.Dispose();
            _numOfThumbNailsInOneDirection = (int)(canvasWidth / (2 * _thumbnailBoxSize)) + 1;
            _thumbnailOffscreen = new CanvasRenderTarget(sender, offscreenWidth, _thumbnailBoxSize);
        }

        using (var dsThumbNail = _thumbnailOffscreen.CreateDrawingSession())
        {
            dsThumbNail.Clear(Colors.Transparent);

            // Center thumbnail sits at the canvas center, offset by the margin baked into the wider surface.
            var startX = _offscreenMarginPx + (int)canvasWidth / 2 - _thumbnailBoxSize / 2;

            // Render the visible boxes plus the slide margin on each side so a lagging slide shows no gap.
            // The selection border is NOT baked here — it is drawn as a fixed frame in D2dCanvasThumbNail_Draw,
            // so it stays put while the strip slides beneath it.
            var renderHalfCount = _numOfThumbNailsInOneDirection + Constants.ThumbnailSlideMarginBoxes;
            for (var i = -renderHalfCount; i <= renderHalfCount; i++)
                DrawThumbnailSlot(dsThumbNail, sender, startX, i, keys, currentPosition);
        }

        // The strip is now centered on currentPosition. If the position changed since the last render,
        // slide it in from where it used to be. Preview-load redraws leave the position unchanged
        // (delta == 0) and so don't animate.
        var delta = _lastRenderedPosition < 0 ? 0 : currentPosition - _lastRenderedPosition;
        _lastRenderedPosition = currentPosition;
        BeginSlide(delta);
    }

    /// <summary>
    /// This now fires once per throttled interval.
    /// </summary>
    private void ThrottledRedrawTimer_Tick(object sender, object e)
    {
        // Stop the timer, making it a one-shot. It will be restarted by the next call
        // to RedrawThumbNailsIfNeeded if more updates come in.
        _throttledRedrawTimer.Stop();
        if (!_redrawNeeded) return;
        _redrawNeeded = false; // Reset the flag
        RequestRebuild();
    }

    private CanvasBitmap GetOrCreateLoadingIndicatorBitmap(ICanvasResourceCreator resourceCreator)
    {
        if (_loadingIndicatorBitmap != null) return _loadingIndicatorBitmap;
        var src = Photo.GetLoadingIndicator().Bitmap;
        if (src == null) return null;
        var pixels = src.GetPixelBytes();
        _loadingIndicatorBitmap = CanvasBitmap.CreateFromBytes(resourceCreator,
            pixels, (int)src.SizeInPixels.Width, (int)src.SizeInPixels.Height, src.Format);
        return _loadingIndicatorBitmap;
    }

    private static CanvasBitmap EnsureThumbnailBitmap(Photo photo, ICanvasResourceCreator creator)
    {
        if (photo.Thumbnail == null) return null;
        if (photo.Thumbnail.Bitmap != null) return photo.Thumbnail.Bitmap;
        try
        {
            int s = Constants.ThumbnailPixelBufferSize;
            photo.Thumbnail.Bitmap = CanvasBitmap.CreateFromBytes(creator,
                photo.Thumbnail.Pixels, s, s, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        catch { }
        return photo.Thumbnail.Bitmap;
    }

    private void DrawThumbnailSlot(CanvasDrawingSession ds, ICanvasResourceCreator creator,
        int startX, int slotIndex, IReadOnlyList<int> keys, int currentPosition)
    {
        var thumbnailPosition = currentPosition + slotIndex;
        if (thumbnailPosition < 0 || thumbnailPosition >= keys.Count) return;

        var key = keys[thumbnailPosition];
        var photo = _isPreviewLoaded?.Invoke(key) == true ? _provider?.GetPhoto(key) : null;

        var bitmap = (photo != null ? EnsureThumbnailBitmap(photo, creator) : null)
                     ?? GetOrCreateLoadingIndicatorBitmap(creator);
        if (bitmap == null) return;

        var destX = startX + slotIndex * _thumbnailBoxSize;
        var destRect = new Rect(
            destX + Constants.ThumbnailPadding,
            Constants.ThumbnailPadding,
            _thumbnailBoxSize - (Constants.ThumbnailPadding * 2),
            _thumbnailBoxSize - (Constants.ThumbnailPadding * 2));
        var srcRect = new Rect(0, 0, bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height);

        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, destRect,
            Constants.ThumbnailCornerRadius, Constants.ThumbnailCornerRadius);
        using var layer = ds.CreateLayer(1.0f, clip);
        ds.DrawImage(bitmap, destRect, srcRect, 1f, CanvasImageInterpolation.HighQualityCubic);
    }

    // --- Threading helpers ---

    /// <summary>Posts device-free work to the W2D thread and wakes the loop. Safe from any thread.</summary>
    private void EnqueueW2dAction(Action action)
    {
        _pendingW2dActions.Enqueue(action);
        _d2dCanvasThumbNail.Paused = false; // thread-safe per Win2D
    }

    /// <summary>Flags an offscreen rebuild and wakes the loop. Safe from any thread.</summary>
    private void RequestRebuild()
    {
        Interlocked.Exchange(ref _rebuildRequested, 1);
        _d2dCanvasThumbNail.Paused = false; // thread-safe per Win2D
    }

    private void RunOnUiThread(Action action)
    {
        if (!_d2dCanvasThumbNail.DispatcherQueue.HasThreadAccess)
        {
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() => action());
            return;
        }
        action();
    }

    // --- Lifecycle ---

    public void Dispose()
    {
        _throttledRedrawTimer.Stop();
        _throttledRedrawTimer.Tick -= ThrottledRedrawTimer_Tick;

        if (_d2dCanvasThumbNail != null)
        {
            // Stop and join the Win2D worker thread BEFORE freeing GPU resources, so no in-flight
            // Update/Draw can observe a disposed offscreen. After this returns there is no concurrency.
            _d2dCanvasThumbNail.RemoveFromVisualTree();
            _d2dCanvasThumbNail.CreateResources -= D2dCanvasThumbNail_CreateResources;
            _d2dCanvasThumbNail.Update -= D2dCanvasThumbNail_Update;
            _d2dCanvasThumbNail.Draw -= D2dCanvasThumbNail_Draw;
            _d2dCanvasThumbNail.SizeChanged -= D2dCanvasThumbNail_SizeChanged;
            _d2dCanvasThumbNail.Loaded -= D2dCanvasThumbNail_Loaded;
            _d2dCanvasThumbNail.PointerPressed -= D2dCanvasThumbNail_PointerPressed;
        }

        _thumbnailOffscreen?.Dispose();
        _thumbnailOffscreen = null;
        _loadingIndicatorBitmap?.Dispose();
        _loadingIndicatorBitmap = null;
        ThumbnailClicked = null;
        _provider = null;
        _isPreviewLoaded = null;
    }
}
