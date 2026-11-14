using System;
using System.Collections.Generic;
using System.Numerics;
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
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Renders the thumbnail strip on its own <see cref="CanvasAnimatedControl"/> — a dedicated swap-chain
/// render thread (the "W2D thread"), separate from the main canvas. This mirrors the threading model on
/// <see cref="CanvasController"/>: everything that touches rendering state runs on the W2D thread and the
/// only door in is <see cref="_pump"/> (drained at the top of <see cref="D2dCanvasThumbNail_Update"/>)
/// plus the <see cref="_rebuildRequested"/> flag. The UI thread never touches the offscreen, the cached
/// bitmaps, or the slide state directly — it posts work and wakes the loop. The control is paused whenever
/// it isn't sliding or rebuilding, so at rest it costs nothing.
/// <para>
/// Reading the photo collection on the W2D thread is safe: <see cref="PhotoList"/> exposes a lock-free
/// <c>GetPhoto</c> and a volatile copy-on-write <c>Keys</c> snapshot expressly for non-UI readers, and the viewer's
/// position comes as one immutable <see cref="NavState"/>, so the keys and the position always belong together.
/// </para>
/// <para>
/// The strip draws each photo's <see cref="DisplaySlots.Thumbnail"/>: device-independent pixels the prefetch cache
/// stores with the preview and drops with it. The GPU copies made from them are the strip's own, so the cache can
/// drop a thumbnail on its thread without racing a draw here.
/// </para>
/// </summary>
internal partial class ThumbNailController : IThumbnailController
{
    /// <summary>A thumbnail other than the centre one was clicked: the offset (positions) to move by. UI thread.</summary>
    public event Action<int> ThumbnailClicked;

    // The strip's XAML control; its W2D thread runs Update/Draw.
    private readonly CanvasAnimatedControl _d2dCanvasThumbNail;

    // Set by SetPhotoSource: resolve a key to its photo, and read the viewer's list and position (one NavState, so
    // the keys and the position always belong together). Both are lock-free and read on any thread.
    private Func<int, Photo> _getPhoto;
    private Func<NavState> _getNav;

    // How many boxes fit on each side of the centre one. Written on the UI thread (resize) and the W2D thread (ribbon
    // pass); read there and on the prefetch thread (RedrawThumbNailsIfNeeded). A plain int: a stale read only means
    // one redraw too many or too few, and the next redraw corrects it.
    private int _numOfThumbNailsInOneDirection = 20;
    // One box's width and height in pixels (the ThumbnailSize setting). UI thread writes it; the W2D thread reads it.
    private int _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;
    // The frame drawn around the centre box (the ThumbnailSelectionColor setting).
    private Color _thumbNailSelectionColor;

    // W2D-owned: created/used/disposed only on the W2D thread (build + draw + CreateResources).
    private CanvasRenderTarget _thumbnailOffscreen;   // the whole strip, pre-rendered; Draw only blits it
    private CanvasBitmap _loadingIndicatorBitmap;     // drawn in a box whose photo has no thumbnail yet

    // W2D-owned GPU copies of DisplaySlots.Thumbnail pixels, keyed by Thumbnail instance so a regenerated
    // thumbnail never reuses a stale bitmap. The prefetch cache only drops Thumbnail references. Each
    // ribbon pass moves the bitmaps it draws from _previousPassBitmaps into _currentPassBitmaps; whatever
    // is left behind wasn't drawn (scrolled off, evicted or deleted) and is disposed.
    private Dictionary<Thumbnail, CanvasBitmap> _currentPassBitmaps = new();
    private Dictionary<Thumbnail, CanvasBitmap> _previousPassBitmaps = new();

    // Thumbnails arriving while the window fills (hundreds a second) trigger at most one rebuild per interval.
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(150);
    // UI thread: a one-shot timer started by the first thumbnail in an interval; its tick asks for the rebuild.
    private readonly DispatcherTimer _throttledRedrawTimer = new() { Interval = ThrottleInterval };
    // UI thread: a visible thumbnail arrived since the last tick.
    private bool _redrawNeeded;
    // Set (UI thread) once there is something worth drawing: the first visible thumbnail, or the strip turned on.
    // Until then a ribbon pass draws nothing and frees the strip's surfaces.
    private bool _canDrawThumbnails;

    // The single UI/other-thread -> W2D handoff. Arbitrary device-free work (slide reset, offscreen
    // disposal) is queued here; the heavier offscreen rebuild is flagged via _rebuildRequested because it
    // needs the live W2D `sender` for the device and render size. Both are drained at the top of Update.
    private readonly W2dActionPump _pump;
    // 1 when a ribbon rebuild is wanted. Set (Interlocked) on any thread; cleared by the W2D thread as it rebuilds.
    private int _rebuildRequested;

    // Slide animation (W2D-owned): the strip is always rendered centered on the current photo, but on
    // navigation we seed a horizontal offset (delta * boxSize) so it appears to start at the old position,
    // then spring it back to 0 — a damped pixel-space slide stepped each frame in Update.
    private readonly SpringAxis _slideOffset = new();  // the strip's horizontal offset in pixels, and its velocity
    private int? _lastRenderedKey; // the photo centred by the last ribbon pass; W2D thread only
    private bool _slideAnimating;  // the spring is moving: keeps the render loop awake

    // Off-screen is rendered this many pixels wider than the canvas on each side (see
    // ThumbnailSlideMarginBoxes) so the sliding strip never exposes a transparent edge.
    private int _offscreenMarginPx;

    // --- Initialization ---

    /// <summary>UI thread. Hooks the strip's render loop and pointer input. Nothing is drawn until <see cref="SetPhotoSource"/>.</summary>
    public ThumbNailController(CanvasAnimatedControl d2dCanvasThumbNail)
    {
        _d2dCanvasThumbNail = d2dCanvasThumbNail;
        _pump = new W2dActionPump(_d2dCanvasThumbNail);
        _d2dCanvasThumbNail.CreateResources += D2dCanvasThumbNail_CreateResources;
        _d2dCanvasThumbNail.Update += D2dCanvasThumbNail_Update;
        _d2dCanvasThumbNail.Draw += D2dCanvasThumbNail_Draw;
        _d2dCanvasThumbNail.SizeChanged += D2dCanvasThumbNail_SizeChanged;
        _d2dCanvasThumbNail.Loaded += D2dCanvasThumbNail_Loaded;
        _throttledRedrawTimer.Tick += ThrottledRedrawTimer_Tick;
        _d2dCanvasThumbNail.PointerPressed += D2dCanvasThumbNail_PointerPressed;
        _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
    }

    /// <inheritdoc />
    public void SetPhotoSource(Func<int, Photo> getPhoto, Func<NavState> getNav) => (_getPhoto, _getNav) = (getPhoto, getNav);

    // --- Canvas event handlers ---

    // UI thread. Applies the ShowThumbnails setting once the control is in the tree.
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

    // Device (re)created (first load or device loss). Drop the device-bound surfaces so they rebuild
    // from the device-independent Thumbnail pixels. Runs on the W2D thread.
    private void D2dCanvasThumbNail_CreateResources(CanvasAnimatedControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        ReleaseDeviceResources();
        RequestRebuild();
    }

    /// <summary>
    /// W2D thread, once per frame before Draw. Drains queued work, rebuilds the offscreen if requested,
    /// steps the slide spring, and pauses the loop when there is nothing left to do.
    /// </summary>
    private void D2dCanvasThumbNail_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        // ① Apply queued UI -> W2D requests (slide reset, offscreen disposal).
        _pump.Drain();

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
        _pump.PauseIfIdle(WorkPending());
    }

    // W2D thread. Whether the loop must keep running: a rebuild is wanted or the strip is sliding.
    private bool WorkPending() =>
        Volatile.Read(ref _rebuildRequested) == 1 || _slideAnimating;

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

    // UI thread. A left click on a box other than the centre raises ThumbnailClicked with how far it is from the centre.
    private void D2dCanvasThumbNail_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(_d2dCanvasThumbNail).Properties.PointerUpdateKind
            is not Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed) return;

        var nav = _getNav?.Invoke();
        var keys = nav?.Keys;
        if (keys == null || keys.Count <= 1) return;

        var pos = e.GetCurrentPoint(_d2dCanvasThumbNail).Position;
        double canvasCenterX = _d2dCanvasThumbNail.ActualWidth / 2;
        double clickedX = pos.X;

        // 1. Calculate the positional offset. This logic was correct.
        int offset = (int)Math.Round((clickedX - canvasCenterX) / _thumbnailBoxSize);

        if (offset == 0) return; // No navigation needed if the center thumbnail is clicked.

        // 2. Find the POSITION of the current key.
        int currentPosition = nav.Position;
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

    /// <summary>The ShowThumbnails setting changed: shows the strip (and rebuilds it), or hides it and frees its GPU surfaces. Any thread.</summary>
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
                _pump.Enqueue(() =>
                {
                    ResetSlide();
                    ReleaseDeviceResources();
                });
            }
        });
    }

    /// <summary>The thumbnail size or selection colour setting changed: re-reads them and rebuilds. Any thread.</summary>
    public void RefreshThumbnail()
    {
        RunOnUiThread(() =>
        {
            _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
            _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;
            // Box size changed → pixel-space offset is stale; reset so the next render doesn't slide.
            _pump.Enqueue(ResetSlide);
            if (AppConfig.Settings.ShowThumbnails)
                RequestRebuild();
        });
    }

    /// <summary>
    /// Called when a photo's thumbnail has been loaded. Redraws only if that photo is in view, and
    /// throttles redraw requests to prevent overwhelming the UI thread.
    /// </summary>
    public void RedrawThumbNailsIfNeeded(int updatedKey)
    {
        // Capture the snapshot once — this method is called on the prefetch thread and the list may be
        // swapped by a list change on the UI thread at any moment.
        var nav = _getNav?.Invoke();
        var keys = nav?.Keys;
        if (keys == null || keys.Count == 0) return;

        int currentPosition = nav.Position;
        if (currentPosition < 0) return;

        // Redraw only if the updated photo sits in one of the visible slots.
        if (KeyWindow.IndexOf(keys, updatedKey, currentPosition, _numOfThumbNailsInOneDirection) >= 0)
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

    /// <summary>
    /// Overscroll feedback when navigation hits the first/last photo: kicks the strip the way it would
    /// have scrolled for <paramref name="direction"/> and lets the slide spring pull it back. Thread-safe.
    /// </summary>
    public void BounceAtEdge(NavDirection direction)
    {
        if (!AppConfig.Settings.ShowThumbnails || !AppConfig.Settings.EnableThumbnailAnimation) return;
        // Next scrolls the strip left (negative X) — the same way BeginSlide's +box offset settles back
        // to 0 on a normal next-photo step — so the kick is negative for Next and positive for Prev.
        var sign = direction == NavDirection.Next ? -1f : 1f;
        _pump.Enqueue(() =>
        {
            // Ignore while already moving so a held key doesn't pile up velocity.
            if (_slideAnimating || _thumbnailOffscreen == null) return;
            _slideOffset.Velocity = sign * Constants.ThumbnailEdgeBounceBoxesPerSec * _thumbnailBoxSize;
            _slideAnimating = true;
        });
    }

    // --- Slide animation ---

    /// <summary>
    /// Seeds and starts (or feeds) the slide spring for a navigation of <paramref name="delta"/>
    /// positions. Small steps glide; large jumps (Home/End, far clicks) snap to avoid a long slide
    /// and transparent edge gaps beyond the rendered margin. W2D thread only.
    /// </summary>
    private void BeginSlide(int delta)
    {
        if (delta == 0) return;

        // Animation disabled → snap: settle the offset to 0 and leave _slideAnimating false so the
        // render loop pauses after this single frame (see GPU-idle invariant). Reuses the snap path.
        if (!AppConfig.Settings.EnableThumbnailAnimation)
        {
            StopSlide();
            return;
        }

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
        _lastRenderedKey = null;
    }

    // --- Rendering internals ---

    /// <summary>
    /// Renders the entire visible thumbnail strip to the off-screen bitmap. Runs on the W2D thread from
    /// <see cref="D2dCanvasThumbNail_Update"/>; <paramref name="sender"/> provides the device and size.
    /// </summary>
    private void BuildRibbon(ICanvasAnimatedControl sender)
    {
        // Capture the key snapshot once — lock-free per the PhotoList contract.
        var nav = _getNav?.Invoke();
        var keys = nav?.Keys;
        if (keys == null || keys.Count < 1 || !_canDrawThumbnails || !AppConfig.Settings.ShowThumbnails)
        {
            ReleaseDeviceResources();
            return;
        }

        // Find the position of the currently displayed photo. Guard BEFORE (re)creating the offscreen, so
        // an invalid position can never leave a freshly allocated, uncleared render target for Draw to blit.
        int currentPosition = nav.Position;
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
            (_previousPassBitmaps, _currentPassBitmaps) = (_currentPassBitmaps, _previousPassBitmaps);
            try
            {
                using var brush = new CanvasImageBrush(sender) { Interpolation = CanvasImageInterpolation.HighQualityCubic };
                for (var i = -renderHalfCount; i <= renderHalfCount; i++)
                    DrawThumbnailSlot(dsThumbNail, brush, sender, startX, i, keys, currentPosition);
            }
            finally
            {
                // Even if a slot throws mid-pass (e.g. device removed), never carry undrawn bitmaps into
                // the next pass, where a re-created bitmap would overwrite their key without disposing them.
                DisposeAndClear(_previousPassBitmaps);
            }
        }

        // The strip is now centered on currentPosition. If a different photo is centred than last time,
        // slide it in from where that photo now sits. Tracking the photo (key) rather than the position means
        // a list change that only shifts positions (Folder mode inserts/removals) is delta 0 and doesn't
        // animate, and preview-load redraws don't either.
        BeginSlide(SlideDelta(keys, currentPosition, _numOfThumbNailsInOneDirection + Constants.ThumbnailSlideMarginBoxes));
        _lastRenderedKey = keys[currentPosition];
    }

    // How many positions the strip moved since the last render. Only the rendered slots are searched; a
    // last photo that is no longer among them (a far jump, or it was removed) counts as a far jump, which snaps.
    private int SlideDelta(IReadOnlyList<int> keys, int currentPosition, int renderHalfCount)
    {
        if (_lastRenderedKey is not { } last || last == keys[currentPosition]) return 0;
        int lastPosition = KeyWindow.IndexOf(keys, last, currentPosition, renderHalfCount);
        return lastPosition < 0 ? int.MaxValue : currentPosition - lastPosition;
    }

    /// <summary>
    /// UI thread, at the end of a throttle interval: rebuilds once for every thumbnail that arrived during it.
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

    // W2D thread. The strip's own copy of the loading indicator, on the strip's device (the shared one belongs to the
    // main canvas's device). Made once, freed with the other device resources.
    private CanvasBitmap GetOrCreateLoadingIndicatorBitmap(ICanvasResourceCreator resourceCreator)
    {
        if (_loadingIndicatorBitmap != null) return _loadingIndicatorBitmap;
        var src = FlyPhotos.Display.ImageReading.ImageReader.GetLoadingIndicator().Bitmap;
        if (src == null) return null;
        var pixels = src.GetPixelBytes();
        _loadingIndicatorBitmap = CanvasBitmap.CreateFromBytes(resourceCreator,
            pixels, (int)src.SizeInPixels.Width, (int)src.SizeInPixels.Height, src.Format);
        return _loadingIndicatorBitmap;
    }

    /// <summary>
    /// W2D thread, during a ribbon pass. Carries this thumbnail's bitmap over from the previous pass,
    /// or creates it, and records it as drawn in this pass.
    /// </summary>
    private CanvasBitmap TakeOrCreateThumbnailBitmap(Thumbnail thumbnail, ICanvasResourceCreator creator)
    {
        if (!_previousPassBitmaps.Remove(thumbnail, out var bitmap))
        {
            try
            {
                int s = Constants.ThumbnailPixelBufferSize;
                bitmap = CanvasBitmap.CreateFromBytes(creator,
                    thumbnail.Pixels, s, s, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            }
            catch { return null; }
        }
        _currentPassBitmaps[thumbnail] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// W2D thread (or after the worker is stopped in Dispose). Frees every device-bound surface; they are
    /// recreated lazily by the next ribbon pass.
    /// </summary>
    private void ReleaseDeviceResources()
    {
        _thumbnailOffscreen?.Dispose();
        _thumbnailOffscreen = null;
        _loadingIndicatorBitmap?.Dispose();
        _loadingIndicatorBitmap = null;
        DisposeAndClear(_currentPassBitmaps);
    }

    // W2D thread. Disposes every bitmap in the map and empties it.
    private static void DisposeAndClear(Dictionary<Thumbnail, CanvasBitmap> bitmaps)
    {
        foreach (var bitmap in bitmaps.Values)
            bitmap.Dispose();
        bitmaps.Clear();
    }

    // W2D thread, during a ribbon pass. Draws box slotIndex (0 = the centre, negative = before it) as a rounded
    // thumbnail, or the loading indicator when the photo has no thumbnail (yet, or any more).
    private void DrawThumbnailSlot(CanvasDrawingSession ds, CanvasImageBrush brush, ICanvasResourceCreator creator,
        int startX, int slotIndex, IReadOnlyList<int> keys, int currentPosition)
    {
        var thumbnailPosition = currentPosition + slotIndex;
        if (thumbnailPosition < 0 || thumbnailPosition >= keys.Count) return;

        var key = keys[thumbnailPosition];
        var photo = _getPhoto?.Invoke(key);
        // Read once: the prefetch cache may drop the thumbnail (preview eviction) at any moment.
        var thumbnail = photo?.Slots.Thumbnail;

        var bitmap = (thumbnail != null ? TakeOrCreateThumbnailBitmap(thumbnail, creator) : null)
                     ?? GetOrCreateLoadingIndicatorBitmap(creator);
        if (bitmap == null) return;

        var destX = startX + slotIndex * _thumbnailBoxSize;
        var destRect = new Rect(
            destX + Constants.ThumbnailPadding,
            Constants.ThumbnailPadding,
            _thumbnailBoxSize - (Constants.ThumbnailPadding * 2),
            _thumbnailBoxSize - (Constants.ThumbnailPadding * 2));
        // Fill the rounded rect with the bitmap mapped onto it: no clip layer, and one resample at any DPI.
        brush.Image = bitmap;
        brush.Transform = Matrix3x2.CreateScale((float)(destRect.Width / bitmap.Size.Width),
                              (float)(destRect.Height / bitmap.Size.Height))
                          * Matrix3x2.CreateTranslation((float)destRect.X, (float)destRect.Y);
        ds.FillRoundedRectangle(destRect, Constants.ThumbnailCornerRadius, Constants.ThumbnailCornerRadius, brush);
    }

    // --- Threading helpers ---

    /// <summary>Flags an offscreen rebuild and wakes the loop. Safe from any thread.</summary>
    private void RequestRebuild()
    {
        Interlocked.Exchange(ref _rebuildRequested, 1);
        _pump.Wake();
    }

    // Runs action now if on the UI thread, else posts it there.
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

    /// <summary>UI thread, when the window closes. Stops the render loop first, then frees the GPU surfaces.</summary>
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

        ReleaseDeviceResources();
        ThumbnailClicked = null;
        _getPhoto = null;
        _getNav = null;
    }
}
