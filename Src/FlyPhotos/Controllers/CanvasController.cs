using System;
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
        _canvasViewManager = new CanvasViewManager(_canvasViewState, RequestInvalidate, RequestZoomUpdate);
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

    public async Task SetSource(Photo photo, DisplayLevel displayLevel)
    {
        await _animatorLock.WaitAsync();
        _animatorLock.Release();

        _checkeredBrush ??= Util.CreateCheckeredBrush(_d2dCanvas, Constants.CheckerSize);

        var currentOperationId = ++_latestSetSourceOperationId;
        var isFirstPhotoEver = string.IsNullOrEmpty(_currentPhotoPath);

        bool isNewPhoto = photo.FileName != _currentPhotoPath;
        if (isNewPhoto)
        {
            _currentPhotoPath = photo.FileName;
            // Real image means, not a placeholder. It can be either preview or hq.
            _realImageDisplayedForCurrentPhoto = false;
        }

        bool isUpgradeFromPlaceholder = !_realImageDisplayedForCurrentPhoto && displayLevel > DisplayLevel.PlaceHolder;
        bool shouldResetView = isNewPhoto || isUpgradeFromPlaceholder;

        _photoSessionState.CurrentDisplayLevel = displayLevel;
        var displayItem = photo.GetDisplayItemBasedOn(displayLevel);

        var size = photo.GetActualSize();
        _imageSize = new Size(size.Item1, size.Item2);

        Debug.WriteLine($"Displaying {photo.FileName} at level {displayLevel} with size {_imageSize.Width}x{_imageSize.Height}");

        if (displayItem == null) return;

        if (displayLevel > DisplayLevel.PlaceHolder)
        {
            _realImageDisplayedForCurrentPhoto = true;
        }

        switch (displayItem)
        {
            case AnimatedHqDisplayItem animDispItem:
                try
                {
                    IRenderer firstFrameRenderer = new StaticImageRenderer(_d2dCanvas, _canvasViewState, animDispItem.Bitmap, _checkeredBrush, photo.SupportsTransparency(), RequestInvalidate, false);
                    SetupNewRenderer(firstFrameRenderer, _imageSize, animDispItem.Rotation, isFirstPhotoEver, shouldResetView, true);

                    IAnimator newAnimator = 
                        string.Equals(Path.GetExtension(photo.FileName), ".gif", StringComparison.OrdinalIgnoreCase)
                        ? await GifAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas)
                        : await PngAnimator.CreateAsync(animDispItem.FileAsByteArray, _d2dCanvas);

                    if (currentOperationId == _latestSetSourceOperationId)
                    {
                        await newAnimator.UpdateAsync(TimeSpan.Zero);
                        IRenderer newRenderer = new AnimatedImageRenderer(_d2dCanvas, _checkeredBrush, newAnimator, _animatorLock, photo.SupportsTransparency(), RequestInvalidate);
                        // For animation, we don't reset the view again, as the static frame is already there
                        SetupNewRenderer(newRenderer, _imageSize, animDispItem.Rotation, false, false, false);
                    }
                    else
                    {
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
                    IRenderer newRenderer = new StaticImageRenderer(_d2dCanvas, _canvasViewState, hqDispItem.Bitmap, _checkeredBrush, photo.SupportsTransparency(), RequestInvalidate);
                    SetupNewRenderer(newRenderer, _imageSize, hqDispItem.Rotation, isFirstPhotoEver, shouldResetView, true);
                    break;
                }
            case PreviewDisplayItem previewDispItem:
                {
                    // Sometimes preview aspect ratio is different from actual image aspect ratio.
                    // To avoid image distortion, we calculate the width based on actual image height and preview aspect ratio.
                    var previewAspectRatio = previewDispItem.Bitmap.Bounds.Width / previewDispItem.Bitmap.Bounds.Height;
                    var correctedWidth = _imageSize.Height * previewAspectRatio;

                    IRenderer newRenderer = new StaticImageRenderer(_d2dCanvas, _canvasViewState, previewDispItem.Bitmap, _checkeredBrush, photo.SupportsTransparency(), RequestInvalidate);
                    SetupNewRenderer(newRenderer, new Size(correctedWidth, _imageSize.Height), previewDispItem.Rotation, isFirstPhotoEver, shouldResetView, true);
                    break;
                }
        }
    }

    private void SetupNewRenderer(IRenderer newRenderer, Size imageSize, int imageRotation, bool isFirstPhotoEver, bool resetView, bool forceThumbNailRedraw)
    {
        _currentRenderer?.Dispose();
        _currentRenderer = newRenderer;

        if (resetView)
        {
            _canvasViewManager.SetScaleAndPosition(imageSize,
                imageRotation, _d2dCanvas.GetSize(), isFirstPhotoEver);
        }
        else
        {
            _canvasViewManager.UpdateImageMetrics(imageSize);
        }

        if (forceThumbNailRedraw)
            _thumbNailController.CreateThumbnailRibbonOffScreen();
        _currentRenderer.RestartOffScreenDrawTimer();
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
