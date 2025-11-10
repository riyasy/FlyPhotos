﻿﻿using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.AppSettings;
using FlyPhotos.Controllers.Animators;
using FlyPhotos.Controllers.Renderers;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Size = FlyPhotos.Data.Size;

namespace FlyPhotos.Controllers;

internal class CanvasController : ICanvasController
{
    public event Action<int> OnZoomChanged;
    public event Action<bool> OnFitToScreenStateChanged;
    public event Action<bool> OnOneToOneStateChanged;

    private readonly IThumbnailController _thumbNailController;
    private readonly PhotoSessionState _photoSessionState;
    private readonly CanvasControl _d2dCanvas;

    private IRenderer _currentRenderer;

    private bool _invalidatePending;
    private int _latestSetSourceOperationId;

    // For GIF and APNG File handling
    private readonly SemaphoreSlim _animatorLock = new(1, 1);

    // For Checkered Background
    private CanvasImageBrush _checkeredBrush;

    // For State management
    private readonly CanvasViewState _canvasViewState;
    private readonly CanvasViewManager _canvasViewManager;
    private Size _imageSize;
    private string _currentPhotoPath = string.Empty;
    private bool _realImageDisplayedForCurrentPhoto;

    #region Construction and Destruction

    public CanvasController(CanvasControl d2dCanvas, IThumbnailController thumbNailController,
        PhotoSessionState photoSessionState)
    {
        _d2dCanvas = d2dCanvas;
        _thumbNailController = thumbNailController;
        _photoSessionState = photoSessionState;

        _d2dCanvas.Draw += D2dCanvas_Draw;
        _d2dCanvas.SizeChanged += D2dCanvas_SizeChanged;

        _canvasViewState = new CanvasViewState();
        _canvasViewManager = new CanvasViewManager(_canvasViewState);
        _canvasViewManager.FitToScreenStateChanged += (isFitted) => OnFitToScreenStateChanged?.Invoke(isFitted);
        _canvasViewManager.OneToOneStateChanged += (isOneToOne) => OnOneToOneStateChanged?.Invoke(isOneToOne);
        _canvasViewManager.ZoomChanged += RequestZoomUpdate;
        _canvasViewManager.ViewChanged += RequestInvalidate;
    }

    public async ValueTask DisposeAsync()
    {
        await _animatorLock.WaitAsync();
        try
        {
            if (_d2dCanvas != null) _d2dCanvas.Draw -= D2dCanvas_Draw;
            _canvasViewManager?.Dispose();
            _currentRenderer?.Dispose();
            _currentRenderer = null;
            _checkeredBrush?.Dispose();
            _checkeredBrush = null;
            _d2dCanvas?.RemoveFromVisualTree();
        }
        finally
        {
            _animatorLock.Release();
            _animatorLock.Dispose();
        }
    }

    #endregion

    #region Public API

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
        // Ensure that any ongoing animation processing is complete before changing the source.
        await _animatorLock.WaitAsync();
        _animatorLock.Release();

        // Lazily create the checkered brush used for transparent images on first use.
        _checkeredBrush ??= Util.CreateCheckeredBrush(_d2dCanvas, Constants.CheckerSize);

        // Assign a unique ID to this operation. This is crucial for handling race conditions where
        // a user navigates to another photo before the current one has fully loaded (e.g., before an
        // animated GIF has been processed).
        var currentOperationId = ++_latestSetSourceOperationId;
        var isFirstPhotoEver = string.IsNullOrEmpty(_currentPhotoPath);

        // Determine if this is a request for a completely new photo file.
        bool isNewPhoto = photo.FileName != _currentPhotoPath;
        if (isNewPhoto)
        {
            _currentPhotoPath = photo.FileName;
            // Reset the flag indicating whether a "real" image (Preview or HQ) has been shown for this file.
            _realImageDisplayedForCurrentPhoto = false;
        }

        // Check if we are upgrading from a temporary placeholder to a real image.
        bool isUpgradeFromPlaceholder = !_realImageDisplayedForCurrentPhoto && displayLevel > DisplayLevel.PlaceHolder;

        // Decide whether to reset the view's pan, zoom, and rotation.
        // The view is reset only for the very first photo, or when changing photos/upgrading
        // from a placeholder AND the setting to preserve zoom is OFF.
        bool shouldResetView = isFirstPhotoEver || (!AppConfig.Settings.PreserveZoomAndPan && (isNewPhoto || isUpgradeFromPlaceholder));

        _photoSessionState.CurrentDisplayLevel = displayLevel;
        var displayItem = photo.GetDisplayItemBasedOn(displayLevel);

        var size = photo.GetActualSize();
        _imageSize = new Size(size.Item1, size.Item2);

        Debug.WriteLine($"Displaying {photo.FileName} at level {displayLevel} with size {_imageSize.Width}x{_imageSize.Height}");

        if (displayItem == null) return;

        // If we've reached Preview or HQ, mark that a real image is now displayed.
        if (displayLevel > DisplayLevel.PlaceHolder)
        {
            _realImageDisplayedForCurrentPhoto = true;
        }

        // Handle the specific type of display item (Animated, HQ Static, Preview).
        switch (displayItem)
        {
            case AnimatedHqDisplayItem animDispItem:
                try
                {
                    // For animated images, first display the static first frame immediately for responsiveness.
                    IRenderer firstFrameRenderer = new StaticImageRenderer(_d2dCanvas, _canvasViewState, animDispItem.Bitmap, _checkeredBrush, photo.SupportsTransparency(), RequestInvalidate, false);
                    SetupNewRenderer(firstFrameRenderer, _imageSize, animDispItem.Rotation, isFirstPhotoEver, shouldResetView, true);

                    // Asynchronously create the appropriate animator (GIF or APNG).
                    IAnimator newAnimator =
                        string.Equals(Path.GetExtension(photo.FileName), ".gif", StringComparison.OrdinalIgnoreCase)
                        ? await GifAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas)
                        : await PngAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas);

                    // RACE CONDITION CHECK: If another SetSource call has started while we were creating the
                    // animator, this operation is now obsolete. We should discard the result and clean up.
                    if (currentOperationId == _latestSetSourceOperationId)
                    {
                        // The operation is still valid. Prepare the animator and swap the renderer.
                        await newAnimator.UpdateAsync(TimeSpan.Zero);
                        IRenderer newRenderer = new AnimatedImageRenderer(_d2dCanvas, _checkeredBrush, newAnimator, _animatorLock, photo.SupportsTransparency(), RequestInvalidate);
                        // For animation, we don't reset the view again, as the static frame is already there.
                        SetupNewRenderer(newRenderer, _imageSize, animDispItem.Rotation, false, false, false);
                    }
                    else
                    {
                        // This operation was superseded; dispose the newly created animator to prevent leaks.
                        newAnimator.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to display GIF: {ex.Message}");
                }
                break;
            case HqDisplayItem hqDispItem:
                {
                    // For a high-quality static image, create and set the static renderer.
                    IRenderer newRenderer = new StaticImageRenderer(_d2dCanvas, _canvasViewState, hqDispItem.Bitmap, _checkeredBrush, photo.SupportsTransparency(), RequestInvalidate);
                    SetupNewRenderer(newRenderer, _imageSize, hqDispItem.Rotation, isFirstPhotoEver, shouldResetView, true);
                    break;
                }
            case PreviewDisplayItem previewDispItem:
                {
                    // Previews can sometimes have a slightly different aspect ratio than the HQ image.
                    // To prevent distortion when the HQ image eventually loads, we adjust the preview's
                    // display dimensions to match the final aspect ratio.
                    var previewAspectRatio = previewDispItem.Bitmap.Bounds.Width / previewDispItem.Bitmap.Bounds.Height;
                    var correctedWidth = _imageSize.Height * previewAspectRatio;

                    IRenderer newRenderer = new StaticImageRenderer(_d2dCanvas, _canvasViewState, previewDispItem.Bitmap, _checkeredBrush, photo.SupportsTransparency(), RequestInvalidate);
                    SetupNewRenderer(newRenderer, new Size(correctedWidth, _imageSize.Height), previewDispItem.Rotation, isFirstPhotoEver, shouldResetView, true);
                    break;
                }
        }
    }

    /// <summary>
    /// Configures the canvas with a new renderer and updates the view state. This method centralizes
    /// the logic for swapping renderers and informing the CanvasViewManager of new image metrics.
    /// </summary>
    /// <param name="newRenderer">The new renderer instance to be used for drawing.</param>
    /// <param name="imageSize">The actual dimensions of the image being displayed.</param>
    /// <param name="imageRotation">The rotation of the image.</param>
    /// <param name="isFirstPhotoEver">True if this is the very first image loaded in the session.</param>
    /// <param name="resetView">True if the pan, zoom, and rotation should be reset to the default view.</param>
    /// <param name="forceThumbNailRedraw">True to force the thumbnail strip to regenerate its off-screen bitmap.</param>
    private void SetupNewRenderer(IRenderer newRenderer, Size imageSize, int imageRotation, bool isFirstPhotoEver, bool resetView, bool forceThumbNailRedraw)
    {
        // Dispose the previous renderer to release its resources (e.g., CanvasBitmap).
        _currentRenderer?.Dispose();
        _currentRenderer = newRenderer;

        if (resetView)
        {
            // If resetting, tell the view manager to calculate a fresh scale and position.
            _canvasViewManager.SetScaleAndPosition(imageSize,
                imageRotation, _d2dCanvas.GetSize(), isFirstPhotoEver);
        }
        else
        {
            // Otherwise, just update the image metrics, preserving the current pan/zoom state.
            _canvasViewManager.UpdateImageMetrics(imageSize, _d2dCanvas.GetSize());
        }

        // The thumbnail ribbon is drawn to an off-screen surface. If the set of images changes,
        // we need to force a redraw of this surface.
        if (forceThumbNailRedraw)
            _thumbNailController.CreateThumbnailRibbonOffScreen();

        // Each renderer may have its own off-screen drawing logic 
        _currentRenderer.RestartOffScreenDrawTimer();

        // Invalidate the main canvas to trigger a D2dCanvas_Draw event with the new renderer.
        RequestInvalidate();
    }

    public void FitToScreen(bool animateChange)
    {
        _canvasViewManager.ZoomPanToFit(animateChange, _imageSize, _d2dCanvas.GetSize());
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void ZoomToHundred()
    {
        _canvasViewManager.ZoomToHundred(_d2dCanvas.GetSize());
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void ZoomOutOnExit(double exitAnimationDuration)
    {
        _canvasViewManager.ZoomOutOnExit(exitAnimationDuration, _d2dCanvas.GetSize());
    }

    public void ZoomByKeyboard(ZoomDirection zoomDirection)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.ZoomAtCenter(zoomDirection, _d2dCanvas.GetSize());
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void StepZoom(ZoomDirection zoomDirection, Point? zoomAnchor = null)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.StepZoom(zoomDirection, _d2dCanvas.GetSize(), zoomAnchor);
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.ZoomAtPoint(zoomDirection, zoomAnchor);
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void ZoomAtPointPrecision(int delta, Point zoomAnchor)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.ZoomAtPointPrecision(delta, zoomAnchor);
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void Pan(double dx, double dy)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.Pan(dx, dy);
    }

    public void RotateCurrentPhotoBy90(bool clockWise)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.RotateBy(clockWise ? 90 : -90);
    }

    public void Shrug()
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.Shrug();
    }

    #endregion

    #region Event Handlers

    private void D2dCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        args.DrawingSession.Clear(Colors.Transparent);

        if (_currentRenderer == null) return;

        CanvasImageInterpolation drawingQuality;
        if (!AppConfig.Settings.HighQualityInterpolation || _canvasViewManager.PanZoomAnimationOnGoing)
            drawingQuality = CanvasImageInterpolation.NearestNeighbor;
        else
            drawingQuality = CanvasImageInterpolation.HighQualityCubic;

        args.DrawingSession.Transform = _canvasViewState.Mat;

        _currentRenderer.Draw(args.DrawingSession, _canvasViewState, drawingQuality);
    }

    private void D2dCanvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.HandleSizeChange(Size.FromFoundationSize(args.NewSize), Size.FromFoundationSize(args.PreviousSize));
    }

    #endregion

    #region Rendering And State Management

    private bool IsScreenEmpty()
    {
        return _currentRenderer == null;
    }

    public bool IsPressedOnImage(Point position)
    {
        var tp = Vector2.Transform(new Vector2((float)position.X, (float)position.Y), _canvasViewState.MatInv);
        return tp.X >= _canvasViewState.ImageRect.X && tp.Y >= _canvasViewState.ImageRect.Y
                                                    && tp.X <= _canvasViewState.ImageRect.Right &&
                                                    tp.Y <= _canvasViewState.ImageRect.Bottom;
    }

    public void HandleCheckeredBackgroundChange()
    {
        _currentRenderer?.TryRedrawOffScreen();
    }

    private void RequestInvalidate()
    {
        if (_invalidatePending) return;
        _invalidatePending = true;

        _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
        {
            _invalidatePending = false;
            _d2dCanvas.Invalidate();
        });
    }

    private void RequestZoomUpdate()
    {
        _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
        {
            OnZoomChanged?.Invoke((int)Math.Round(_canvasViewState.Scale * 100));
        });
    }

    #endregion
}