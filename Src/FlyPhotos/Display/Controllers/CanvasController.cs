using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Animators;
using FlyPhotos.Display.ImageRendering;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Win2D rendering controller for the main canvas.
///
/// Threading model — "everything that touches rendering state runs on the W2D thread; the queue
/// is the only door in". The CanvasAnimatedControl runs an Update/Draw worker thread (the "W2D
/// thread"). <see cref="_currentRenderer"/>, <see cref="_checkeredBrush"/>, <see cref="_canvasViewState"/>
/// and <see cref="_canvasViewManager"/> are W2D-owned: they are only ever touched inside actions
/// drained from <see cref="_pendingW2dActions"/> at the top of <see cref="D2dCanvas_Update"/>,
/// or inside Update/Draw itself. The UI thread never touches them directly — it posts work via
/// <see cref="EnqueueW2dAction"/>. There is therefore no lock on the render hot path.
///
/// All public methods must be called on the UI (DispatcherQueue) thread.
/// </summary>
internal partial class CanvasController : ICanvasController
{
    public event Action<int> OnZoomChanged;
    public event Action<bool> OnFitToScreenStateChanged;
    public event Action<bool> OnOneToOneStateChanged;
    public event Action<bool> OnMutliPagePhotoLoaded;

    private readonly IThumbnailController _thumbNailController;
    private readonly PhotoSessionState _photoSessionState;
    private readonly CanvasAnimatedControl _d2dCanvas;

    // The single UI -> W2D handoff. Drained at the top of every Update on the W2D thread.
    private readonly ConcurrentQueue<Action> _pendingW2dActions = new();

    // W2D-owned: swapped/read only on the W2D thread (install action + Draw).
    private IRenderer _currentRenderer;

    // W2D-owned: created lazily in the install action, disposed on device loss in CreateResources.
    private CanvasImageBrush _checkeredBrush;

    // W2D-owned view state and view logic.
    private readonly CanvasViewState _canvasViewState;
    private readonly CanvasViewManager _canvasViewManager;

    // W2D-owned: set inside the ZoomOutOnExit action, read in WaitForPanZoomAnimationAsync.

    // W2D-owned: true while ZoomAtPointPrecision ticks are arriving (right-click continuous zoom).
    // Treated as animating so Draw uses mip-based quality. Cleared 700 ms after the last tick.
    private bool _continuousZoomActive;
    private CancellationTokenSource _continuousZoomCts;

    private int _latestSetSourceOperationId;

    // UI-owned orchestration fields — touched only inside SetSource / InstallRenderer on the UI thread.
    private Size _imageSize;
    private string _currentPhotoPath = string.Empty;
    private bool _realImageDisplayedForCurrentPhoto;
    private bool _isMultiPageActive;

    // Published W2D -> UI for synchronous pointer hit-testing (IsPressedOnImage).
    // Written every Update on the W2D thread; read on the UI thread during pointer events.
    // A Lock is used because Matrix3x2 (6 floats) and Rect (4 doubles) are not atomically writable,
    // making volatile inadequate. Contention is negligible: pointer events are rare vs. 144 Hz Update.
    private Matrix3x2 _hitTestMatInv = Matrix3x2.Identity;
    private Rect _hitTestImageRect;
    private readonly Lock _hitTestLock = new();

    private int _zoomPercentUiUpdatePending;
    private int _pendingZoomPercent;
    private int _lastDispatchedZoomPercent = -1;

    // --- Initialization ---

    public CanvasController(CanvasAnimatedControl d2dCanvas, IThumbnailController thumbNailController,
        PhotoSessionState photoSessionState)
    {
        _d2dCanvas = d2dCanvas;
        _thumbNailController = thumbNailController;
        _photoSessionState = photoSessionState;

        _d2dCanvas.CreateResources += D2dCanvas_CreateResources;
        _d2dCanvas.Update += D2dCanvas_Update;
        _d2dCanvas.Draw += D2dCanvas_Draw;
        _d2dCanvas.SizeChanged += D2dCanvas_SizeChanged;

        _canvasViewState = new CanvasViewState();
        _canvasViewManager = new CanvasViewManager(_canvasViewState);

        // CanvasViewManager events fire on the W2D thread. This is the single place where each one
        // crosses back to the UI thread, so PhotoDisplayWindow's handlers can be plain UI code.
        _canvasViewManager.FitToScreenStateChanged += isFitted =>
            _d2dCanvas.DispatcherQueue.TryEnqueue(() => OnFitToScreenStateChanged?.Invoke(isFitted));
        _canvasViewManager.OneToOneStateChanged += isOneToOne =>
            _d2dCanvas.DispatcherQueue.TryEnqueue(() => OnOneToOneStateChanged?.Invoke(isOneToOne));
        _canvasViewManager.ZoomChanged += RequestZoomUpdate;
        _canvasViewManager.ViewChanged += RequestInvalidate;
    }

    // --- Photo display ---

    /// <summary>
    /// Sets the image source for the canvas, handling different display levels and content types.
    /// This is the primary entry point for changing the displayed photo. It manages race conditions
    /// from rapid photo switching and decides whether to reset the view (pan/zoom) or preserve it
    /// based on user settings and context.
    /// </summary>
    /// <param name="photo">The Photo object containing image data and metadata.</param>
    /// <param name="displayLevel">The quality level to display (e.g., PlaceHolder, Preview, Hq).</param>
    public async Task SetSource(Photo photo, DisplayLevel displayLevel)
    {
        // Read the canvas size on the (UI) calling thread; the install action runs on the W2D thread.
        var canvasSize = _d2dCanvas.GetSize();

        var currentOperationId = Interlocked.Increment(ref _latestSetSourceOperationId);
        var isFirstPhotoEver = string.IsNullOrEmpty(_currentPhotoPath);
        var previousPhotoPath = _currentPhotoPath;
        var isNewPhoto = photo.FilePath != _currentPhotoPath;

        if (isNewPhoto)
        {
            _currentPhotoPath = photo.FilePath;
            _realImageDisplayedForCurrentPhoto = false;
        }

        var isUpgradeFromPlaceholder = !_realImageDisplayedForCurrentPhoto && displayLevel > DisplayLevel.PlaceHolder;

        _photoSessionState.CurrentDisplayLevel = displayLevel;
        var displayItem = photo.GetDisplayItemBasedOn(displayLevel);

        var size = photo.GetActualSize();
        _imageSize = new Size(size.Item1, size.Item2);

        Debug.WriteLine($"Displaying {photo.FilePath} at level {displayLevel} with size {_imageSize.Width}x{_imageSize.Height}");

        if (displayItem == null) return;

        if (displayLevel > DisplayLevel.PlaceHolder)
            _realImageDisplayedForCurrentPhoto = true;

        var ctx = new PhotoInstallContext(_currentPhotoPath, previousPhotoPath, canvasSize,
            isFirstPhotoEver, isNewPhoto, isUpgradeFromPlaceholder);

        // Handle the specific type of display item (Animated, HQ Static, Preview, MultiPage)
        switch (displayItem)
        {
            case AnimatedHqDisplayItem animDispItem:
                await HandleHqAnimatedDisplayItemAsync(currentOperationId, photo, animDispItem, ctx);
                break;
            case MultiPageHqDisplayItem multiDispItem:
                HandleHqMultiPageDisplayItem(photo, multiDispItem, ctx);
                break;
            case HqDisplayItem hqDispItem:
                HandleHqStaticDisplayItem(photo, hqDispItem, ctx);
                break;
            case PreviewDisplayItem previewDispItem:
                HandlePreviewDisplayItem(photo, previewDispItem, ctx);
                break;
        }
    }

    private async Task HandleHqAnimatedDisplayItemAsync(int currentOperationId, Photo photo,
        AnimatedHqDisplayItem animDispItem, PhotoInstallContext ctx)
    {
        try
        {
            // For animated images, first display the static first frame immediately for responsiveness.
            InstallRenderer(
                new StaticImageRenderer(_d2dCanvas, animDispItem.Bitmap,
                    photo.SupportsTransparency, RequestInvalidate, generateMipChain: false),
                _imageSize, animDispItem.Rotation, ctx, forceThumbNailRedraw: true);

            // Asynchronously create the appropriate animator (GIF, WebP, APNG, or AVIF).
            var ext = Path.GetExtension(photo.FilePath);
            IAnimator newAnimator =
                string.Equals(ext, ".avif", StringComparison.OrdinalIgnoreCase) ? await AvifAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas) :
                string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase) ? await GifAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas) :
                string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase) ? await WebpAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas) :
                                                                                  await PngAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas);

            // RACE CONDITION CHECK: If another SetSource call has started while we were creating the
            // animator, this operation is now obsolete. We should discard the result and clean up.
            if (currentOperationId == _latestSetSourceOperationId)
            {
                await newAnimator.UpdateAsync(TimeSpan.Zero);
                // Swap in the animated renderer without re-resetting the view — the static first
                // frame already placed it (AsFollowUp clears the view-reset flags).
                InstallRenderer(
                    new AnimatedImageRenderer(_d2dCanvas, newAnimator, photo.SupportsTransparency, RequestInvalidate),
                    _imageSize, animDispItem.Rotation, ctx.AsFollowUp(), forceThumbNailRedraw: false);
                // Keep the control running while the animated image is active.
                RequestInvalidate();
            }
            else
            {
                // This operation was superseded; dispose the newly created animator to prevent leaks.
                newAnimator.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to display animated image: {ex.Message}");
        }
    }

    private void HandleHqStaticDisplayItem(Photo photo, HqDisplayItem hqDispItem, PhotoInstallContext ctx)
    {
        InstallRenderer(
            new StaticImageRenderer(_d2dCanvas, hqDispItem.Bitmap,
                photo.SupportsTransparency, RequestInvalidate),
            _imageSize, hqDispItem.Rotation, ctx, forceThumbNailRedraw: true);
    }

    private void HandleHqMultiPageDisplayItem(Photo photo, MultiPageHqDisplayItem multiDispItem, PhotoInstallContext ctx)
    {
        InstallRenderer(
            new MultiPageRenderer(_d2dCanvas, multiDispItem.FileAsByteArray, 0,
                photo.SupportsTransparency, RequestInvalidate),
            _imageSize, multiDispItem.Rotation, ctx, forceThumbNailRedraw: true);
    }

    private void HandlePreviewDisplayItem(Photo photo, PreviewDisplayItem previewDispItem, PhotoInstallContext ctx)
    {
        // Previews can sometimes have a slightly different aspect ratio than the HQ image.
        // To prevent distortion when the HQ image eventually loads, we adjust the preview's
        // display dimensions to match the final aspect ratio.
        var previewAspectRatio = previewDispItem.Bitmap.Bounds.Width / previewDispItem.Bitmap.Bounds.Height;
        var correctedWidth = _imageSize.Height * previewAspectRatio;

        InstallRenderer(
            new StaticImageRenderer(_d2dCanvas, previewDispItem.Bitmap,
                photo.SupportsTransparency, RequestInvalidate, generateMipChain: false),
            new Size(correctedWidth, _imageSize.Height), previewDispItem.Rotation, ctx, forceThumbNailRedraw: true);
    }

    /// <summary>
    /// Immutable parameters describing a photo-install request, captured on the UI thread and
    /// applied on the W2D thread inside <see cref="InstallRenderer"/>.
    /// </summary>
    private readonly record struct PhotoInstallContext(
        string PhotoPath, string PreviousPhotoPath, Size CanvasSize,
        bool IsFirstPhotoEver, bool IsNewPhoto, bool IsUpgradeFromPlaceholder)
    {
        /// <summary>A follow-up install for the same photo (e.g. animator after its static first frame) that must not reset the view.</summary>
        public PhotoInstallContext AsFollowUp() =>
            this with { IsFirstPhotoEver = false, IsNewPhoto = false, IsUpgradeFromPlaceholder = false };
    }

    /// <summary>
    /// Installs a new renderer on the W2D thread. The brush is injected there (W2D-owned), and the
    /// swap + view-state update apply atomically in the same tick — no one-frame mismatch, and Draw
    /// can never see a renderer mid-disposal.
    /// </summary>
    private void InstallRenderer(IRenderer newRenderer, Size imageSize,
        int imageRotation, PhotoInstallContext ctx, bool forceThumbNailRedraw)
    {
        // InstallRenderer is always called on the UI thread, so we can fire the event directly here
        // without a DispatcherQueue.TryEnqueue. Only notify when the multipage state actually changes
        // to avoid redundant button-visibility updates on every photo navigation.
        var isMultiPage = newRenderer is MultiPageRenderer;
        if (isMultiPage != _isMultiPageActive)
        {
            _isMultiPageActive = isMultiPage;
            OnMutliPagePhotoLoaded?.Invoke(isMultiPage);
        }

        EnqueueW2dAction(() =>
        {
            _checkeredBrush ??= Util.CreateCheckeredBrush(_d2dCanvas, Constants.CheckerSize);
            newRenderer.CheckeredBrush = _checkeredBrush;

            // Cache the OLD photo's view state before applying the new one. _canvasViewState still
            // holds the previous photo's state at this point, so this reads it race-free.
            if (ctx.IsNewPhoto && !string.IsNullOrEmpty(ctx.PreviousPhotoPath))
                _canvasViewManager.CacheCurrentViewState(ctx.PreviousPhotoPath, ctx.CanvasSize);

            var oldRenderer = _currentRenderer;
            _currentRenderer = newRenderer;

            if (oldRenderer is AnimatedImageRenderer)
                _ = Task.Run(oldRenderer.Dispose); // drain in-flight UpdateFrameAsync off the W2D thread
            else
                oldRenderer?.Dispose();

            _canvasViewManager.SetScaleAndPosition(ctx.PhotoPath, imageSize, imageRotation,
                ctx.CanvasSize, ctx.IsFirstPhotoEver, ctx.IsNewPhoto, ctx.IsUpgradeFromPlaceholder);

            if (forceThumbNailRedraw)
                _thumbNailController.RequestThumbnailRedraw(); // just flags the strip's own W2D loop; thread-safe
        });
    }

    // --- Zoom & pan ---

    public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
    {
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.ZoomAtPoint(zoomDirection, zoomAnchor);
        });
    }

    public void ZoomAtPointPrecision(int delta, Point zoomAnchor)
    {
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.ZoomAtPointPrecision(delta, zoomAnchor);

            // Mark the burst as active so the render path uses mip-based quality instead of
            // source-bitmap quality. Debounced 700ms after the last tick so Draw reverts to
            // non-animating mip selection once the zoom burst ends.
            _continuousZoomActive = true;
            _continuousZoomCts?.Cancel();
            _continuousZoomCts?.Dispose();
            _continuousZoomCts = new CancellationTokenSource();
            var token = _continuousZoomCts.Token;
            Task.Run(async () =>
            {
                try { await Task.Delay(700, token); }
                catch (TaskCanceledException) { return; }
                EnqueueW2dAction(() => _continuousZoomActive = false);
            }, token);
        });
    }

    public void ZoomByKeyboard(ZoomDirection zoomDirection, Point? zoomAnchor = null)
    {
        var canvasSize = _d2dCanvas.GetSize();
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            // While the user is dragging, anchor the keyboard zoom at the cursor (like wheel zoom) so the image
            // doesn't jump away from the point being dragged; otherwise fall back to the canvas centre.
            if (zoomAnchor.HasValue)
                _canvasViewManager.ZoomAtPoint(zoomDirection, zoomAnchor.Value);
            else
                _canvasViewManager.ZoomAtCenter(zoomDirection, canvasSize);
        });
    }

    public void StepZoom(ZoomDirection zoomDirection, Point? zoomAnchor = null)
    {
        var canvasSize = _d2dCanvas.GetSize();
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.StepZoom(zoomDirection, canvasSize, zoomAnchor);
        });
    }

    public void ZoomToHundred()
    {
        var canvasSize = _d2dCanvas.GetSize();
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.ZoomToHundred(canvasSize);
        });
    }

    public void ZoomToHundred(Point anchor)
    {
        var canvasSize = _d2dCanvas.GetSize();
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.ZoomToHundred(canvasSize, anchor);
        });
    }

    public void ZoomOutOnExit(double exitAnimationDuration)
    {
        var canvasSize = _d2dCanvas.GetSize();
        EnqueueW2dAction(() =>
        {
            _canvasViewManager.ZoomOutOnExit(exitAnimationDuration, canvasSize);
        });
    }

    public void FitToScreen(bool animateChange)
    {
        var canvasSize = _d2dCanvas.GetSize();
        var imageSize = _imageSize;
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.ZoomPanToFit(animateChange, imageSize, canvasSize);
        });
    }

    public void Pan(double dx, double dy)
    {
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.Pan(dx, dy);
        });
    }

    public void RotateCurrentPhotoBy90(bool clockWise)
    {
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.RotateBy(clockWise ? 90 : -90);
        });
    }

    public void Shrug()
    {
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.Shrug();
        });
    }

    public void ChangePage(NavDirection navDirection)
    {
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer is not MultiPageRenderer multiPageRenderer) return;
            var targetIndex = navDirection == NavDirection.Next
                ? multiPageRenderer.CurrentPageIndex + 1
                : multiPageRenderer.CurrentPageIndex - 1;
            _ = multiPageRenderer.LoadPageAsync(targetIndex);
        });
    }

    // --- Win2D event loop ---

    private void D2dCanvas_CreateResources(CanvasAnimatedControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        // Device (re)created (first load or device loss). Drop the device-bound brush so it is
        // rebuilt lazily, and rebuild the mip chain with the current scaling quality.
        // Note: CanvasBitmaps held in the photo cache are NOT re-created here (accepted risk).
        // Runs on the W2D thread.
        _checkeredBrush?.Dispose();
        _checkeredBrush = null;
        _currentRenderer?.HandleScalingMethodChange();
    }

    /// <summary>
    /// Called on the W2D thread before each Draw. Drains the queue (the single UI->W2D handoff),
    /// drives animation, publishes the hit-test snapshot, and pauses when idle.
    /// </summary>
    private void D2dCanvas_Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        // ① Apply all queued UI-thread requests. Every view-state / renderer change happens here.
        while (_pendingW2dActions.TryDequeue(out var action))
            action();

        // ② Drive pan/zoom/shrug animation ticks.
        _canvasViewManager.OnUpdate();

        // ③ Drive animated image frame advancement.
        var animatedFrameReady = _currentRenderer is AnimatedImageRenderer animRenderer && animRenderer.OnUpdate();

        // ④ Publish the current transform for UI-thread hit-testing (IsPressedOnImage).
        lock (_hitTestLock)
        {
            _hitTestMatInv = _canvasViewState.MatInv;
            _hitTestImageRect = _canvasViewState.ImageRect;
        }

        // ⑤ Pause when there is nothing to do. Re-check the queue after pausing: an item enqueued
        //    during the pause decision stays in the queue until drained, so this closes the
        //    enqueue/pause lost-wakeup race without a lock.
        if (_pendingW2dActions.IsEmpty && !_canvasViewManager.PanZoomAnimationOnGoing && !animatedFrameReady)
        {
            sender.Paused = true;
            if (!_pendingW2dActions.IsEmpty)
                sender.Paused = false;
        }
    }

    /// <summary>
    /// Called on the W2D thread to draw the current frame. Reads W2D-owned state directly — no lock.
    /// </summary>
    private void D2dCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        args.DrawingSession.Clear(Colors.Transparent);

        var renderer = _currentRenderer;
        if (renderer == null) return;

        var isAnimating = _canvasViewManager.PanZoomAnimationOnGoing || _continuousZoomActive;
        var drawingQuality = AppConfig.Settings.ImageScalingQuality.ToCanvasInterpolation(isAnimating);

        args.DrawingSession.Transform = _canvasViewState.Mat;
        renderer.Draw(args.DrawingSession, _canvasViewState, drawingQuality, isAnimating);
    }

    private void D2dCanvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var newSize = args.NewSize.AdjustForDpi(_d2dCanvas);
        var previousSize = args.PreviousSize.AdjustForDpi(_d2dCanvas);
        EnqueueW2dAction(() =>
        {
            if (_currentRenderer == null) return;
            _canvasViewManager.HandleSizeChange(newSize, previousSize);
        });
    }

    /// <summary>
    /// Returns a Task that completes when the next pan/zoom animation finishes (via the W2D-thread
    /// <see cref="CanvasViewManager.AnimationCompleted"/> event), or after <paramref name="timeoutMs"/>
    /// as a safety ceiling so the caller can never hang if the render loop drops the completion. The
    /// one-shot handler is subscribed on the W2D thread, ordered before any subsequently started
    /// animation. Used for the open-zoom (startup) and exit-zoom waits instead of a fixed delay.
    /// </summary>
    public Task WaitForPanZoomAnimationAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void handler()
        {
            _canvasViewManager.AnimationCompleted -= handler;
            tcs.TrySetResult();
        }
        // Subscribe on the W2D thread so the add is ordered relative to animation ticks and the
        // permanent subscriber — no cross-thread race on the event delegate.
        EnqueueW2dAction(() => _canvasViewManager.AnimationCompleted += handler);

        // Safety net: never hang if AnimationCompleted is missed. Unsubscribe back on the W2D thread.
        _ = Task.Delay(timeoutMs).ContinueWith(_ =>
        {
            if (tcs.TrySetResult())
                EnqueueW2dAction(() => _canvasViewManager.AnimationCompleted -= handler);
        });
        return tcs.Task;
    }

    // --- Hit-testing ---

    public bool IsPressedOnImage(Point position)
    {
        Matrix3x2 matInv;
        Rect imageRect;
        lock (_hitTestLock)
        {
            matInv = _hitTestMatInv;
            imageRect = _hitTestImageRect;
        }
        var tp = Vector2.Transform(new Vector2((float)position.X, (float)position.Y), matInv);
        return tp.X >= imageRect.X && tp.Y >= imageRect.Y
                                   && tp.X <= imageRect.Right && tp.Y <= imageRect.Bottom;
    }

    // --- Settings ---

    public void HandleCheckeredBackgroundChange() => EnqueueW2dAction(() => { }); // wake the canvas; Draw() reads the setting live

    public void HandleImageScalingQualityChange() => EnqueueW2dAction(() => _currentRenderer?.HandleScalingMethodChange());

    // --- Threading helpers ---

    /// <summary>
    /// Posts an action to the W2D thread and wakes the render loop. Safe to call from the UI thread.
    /// </summary>
    private void EnqueueW2dAction(Action action)
    {
        _pendingW2dActions.Enqueue(action);
        _d2dCanvas.Paused = false; // thread-safe; Win2D documents this as safe to call from any thread
    }

    /// <summary>
    /// Wakes the render loop for at least one more Update+Draw. Safe to call from any thread; used
    /// when a renderer's off-screen surface or animation frame becomes ready.
    /// </summary>
    private void RequestInvalidate()
    {
        _d2dCanvas.Paused = false;
    }

    // --- Zoom UI publishing ---

    private void RequestZoomUpdate()
    {
        // Capture the zoom percentage here (W2D thread) so the dispatched lambda does not read
        // W2D-owned view state from the UI thread. Coalesced: latest value wins.
        var newZoom = (int)Math.Round(_canvasViewState.Scale * 100);
        // Skip when the displayed integer is unchanged — avoids dispatching to the UI thread
        // every frame during pan animations where scale is constant.
        if (newZoom == _lastDispatchedZoomPercent) return;
        _lastDispatchedZoomPercent = newZoom;
        Volatile.Write(ref _pendingZoomPercent, newZoom);
        if (Interlocked.CompareExchange(ref _zoomPercentUiUpdatePending, 1, 0) == 0)
            _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
            {
                Volatile.Write(ref _zoomPercentUiUpdatePending, 0);
                OnZoomChanged?.Invoke(Volatile.Read(ref _pendingZoomPercent));
            });
    }

    // --- Cleanup ---

    public void Dispose()
    {
        try
        {
            // Stop and join the Win2D worker thread BEFORE freeing GPU resources, so no in-flight
            // Update/Draw can observe a disposed renderer or brush. After this returns there is no
            // concurrency, so disposal needs no lock.
            _d2dCanvas.RemoveFromVisualTree();
            _d2dCanvas.CreateResources -= D2dCanvas_CreateResources;
            _d2dCanvas.Update -= D2dCanvas_Update;
            _d2dCanvas.Draw -= D2dCanvas_Draw;
            _d2dCanvas.SizeChanged -= D2dCanvas_SizeChanged;

            _continuousZoomCts?.Cancel();
            _continuousZoomCts?.Dispose();
            _canvasViewManager?.Dispose();
            _currentRenderer?.Dispose();
            _currentRenderer = null;
            _checkeredBrush?.Dispose();
            _checkeredBrush = null;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
}
