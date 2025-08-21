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
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.Controllers;

internal class CanvasController : ICanvasController
{
    public const int PanZoomAnimationDurationForExit = 200;
    public const int PanZoomAnimationDurationNormal = 400;

    private readonly IThumbnailController _thumbNailController;
    private readonly CanvasControl _d2dCanvas;

    private IRenderer _currentRenderer; 

    private bool _invalidatePending;
    private int _latestSetSourceOperationId;

    // For Dragging
    private Point _lastPoint;
    private bool _isDragging;

    // For GIF and APNG File handling
    private readonly SemaphoreSlim _animatorLock = new(1, 1);

    // For Checkered Background
    private CanvasImageBrush _checkeredBrush;
    private const int CheckerSize = 10;

    // For State management
    private readonly CanvasViewState _canvasViewState;
    private readonly CanvasViewManager _canvasViewManager;

    #region Construction and Destruction

    public CanvasController(CanvasControl d2dCanvas, IThumbnailController thumbNailController)
    {
        _d2dCanvas = d2dCanvas;
        _thumbNailController = thumbNailController;

        _d2dCanvas.Draw += D2dCanvas_Draw;
        _d2dCanvas.SizeChanged += D2dCanvas_SizeChanged;
        _d2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        _d2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        _d2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        _d2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;

        _canvasViewState = new CanvasViewState();
        _canvasViewManager = new CanvasViewManager(_canvasViewState, RequestInvalidate);
    }

    public async Task CleanupOnClose()
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

        var currentOperationId = ++_latestSetSourceOperationId;
        var isFirstPhoto = _currentRenderer == null;

        Photo.CurrentDisplayLevel = displayLevel;
        var displayItem = photo.GetDisplayItemBasedOn(displayLevel);

        if (displayItem == null) return;

        if (displayItem.IsGifOrAnimatedPng())
        {
            try
            {
                var preview = photo.GetDisplayItemBasedOn(DisplayLevel.Preview);
                bool previewDrawnAsFirstFrame = false;
                if (preview != null)
                {
                    previewDrawnAsFirstFrame = true;
                    displayItem.Bitmap = preview.Bitmap;
                    IRenderer newRenderer = new StaticImageRenderer(_d2dCanvas, displayItem.Bitmap, photo.SupportsTransparency(), RequestInvalidate, _canvasViewState);
                    SetupNewRenderer(newRenderer, displayItem.Bitmap.Bounds.Width, displayItem.Bitmap.Bounds.Height, displayItem.Rotation, isFirstPhoto, true);
                }

                IAnimator newAnimator = Path.GetExtension(photo.FileName).ToUpper() == ".GIF"
                    ? await GifAnimator.CreateAsync(displayItem.FileAsByteArray)
                    : await PngAnimator.CreateAsync(displayItem.FileAsByteArray);

                if (currentOperationId == _latestSetSourceOperationId)
                {
                    await newAnimator.UpdateAsync(TimeSpan.Zero);
                    IRenderer newRenderer = new AnimatedImageRenderer(newAnimator, RequestInvalidate, _animatorLock, photo.SupportsTransparency());
                    SetupNewRenderer(newRenderer, newAnimator.PixelWidth, newAnimator.PixelHeight, displayItem.Rotation, !previewDrawnAsFirstFrame, !previewDrawnAsFirstFrame);
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
        }
        else
        {
            IRenderer newRenderer = new StaticImageRenderer(_d2dCanvas, displayItem.Bitmap, photo.SupportsTransparency(), RequestInvalidate, _canvasViewState);
            SetupNewRenderer(newRenderer, displayItem.Bitmap.Bounds.Width, displayItem.Bitmap.Bounds.Height,
                displayItem.Rotation, isFirstPhoto, true);
        }
    }

    private void SetupNewRenderer(IRenderer newRenderer, double imageWidth, double imageHeight, int imageRotation, bool isFirstTime, bool forceThumbNailRedraw)
    {
        _currentRenderer?.Dispose();
        _currentRenderer = newRenderer;
        _canvasViewManager.SetScaleAndPosition(imageWidth, imageHeight,
            imageRotation, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight, isFirstTime);
        if (forceThumbNailRedraw)
            _thumbNailController.CreateThumbnailRibbonOffScreen();
        _currentRenderer.RestartOffScreenDrawTimer();
        RequestInvalidate();
    }

    public void SetHundredPercent(bool animateChange)
    {
        _canvasViewManager.ZoomPanToFit(animateChange, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight);
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void ZoomOutOnExit(double exitAnimationDuration)
    {
        _canvasViewManager.ZoomOutOnExit(exitAnimationDuration, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight);
    }

    public void ZoomByKeyboard(ZoomDirection zoomDirection)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.ZoomAtCenter(zoomDirection, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight);
        _currentRenderer?.RestartOffScreenDrawTimer();
    }

    public void PanByKeyboard(double dx, double dy)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.Pan(dx, dy);
    }

    public void RotateCurrentPhotoBy90(bool clockWise)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.RotateBy(clockWise ? 90 : -90);
    }

    #endregion

    #region Event Handlers

    private void D2dCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        args.DrawingSession.Clear(Colors.Transparent);

        if (_checkeredBrush == null)
            _checkeredBrush = Util.CreateCheckeredBrush(sender, CheckerSize);

        if (_currentRenderer == null) return;

        CanvasImageInterpolation drawingQuality;
        if (!AppConfig.Settings.HighQualityInterpolation || _canvasViewManager.PanZoomAnimationOnGoing)
            drawingQuality = CanvasImageInterpolation.NearestNeighbor;
        else
            drawingQuality = CanvasImageInterpolation.HighQualityCubic;

        args.DrawingSession.Transform = _canvasViewState.Mat;

        _currentRenderer.Draw(args.DrawingSession, _canvasViewState, drawingQuality, _checkeredBrush);
    }

    private void D2dCanvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (IsScreenEmpty()) return;
        var imageBounds = _currentRenderer.SourceBounds;
        _canvasViewManager.HandleSizeChange(imageBounds, args.NewSize, args.PreviousSize);
    }

    private void D2dCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsScreenEmpty() || !IsPressedOnImage(e.GetCurrentPoint(_d2dCanvas).Position)) return;
        _d2dCanvas.CapturePointer(e.Pointer);
        _lastPoint = e.GetCurrentPoint(_d2dCanvas).Position;
        _isDragging = true;
    }

    private void D2dCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _canvasViewManager.Pan(e.GetCurrentPoint(_d2dCanvas).Position.X - _lastPoint.X,
            e.GetCurrentPoint(_d2dCanvas).Position.Y - _lastPoint.Y);
        _lastPoint = e.GetCurrentPoint(_d2dCanvas).Position;
    }

    private void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _d2dCanvas.ReleasePointerCapture(e.Pointer);
        _isDragging = false;
    }

    private void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsScreenEmpty() || Util.IsControlPressed()) return;
        var zoomDirection = (e.GetCurrentPoint(_d2dCanvas).Properties.MouseWheelDelta > 0) ? ZoomDirection.In : ZoomDirection.Out;
        _canvasViewManager.ZoomAtPoint(zoomDirection, e.GetCurrentPoint(_d2dCanvas).Position,
            _d2dCanvas.Device.MaximumBitmapSizeInPixels);
        _currentRenderer.RestartOffScreenDrawTimer();
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

    #endregion
}

internal interface ICanvasController
{
    Task SetSource(Photo photo, DisplayLevel hq);
    void SetHundredPercent(bool animateChange);
}