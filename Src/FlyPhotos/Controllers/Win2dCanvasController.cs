using FlyPhotos.AppSettings;
using FlyPhotos.Controllers.Animators;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using static FlyPhotos.Controllers.PhotoDisplayController;

namespace FlyPhotos.Controllers;

internal class Win2dCanvasController : ICanvasController
{
    // private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public const int PanZoomAnimationDurationForExit = 200;
    public const int PanZoomAnimationDurationNormal = 400;

    private readonly IThumbnailController _thumbNailController;
    private readonly CanvasControl _d2dCanvas;
    private DisplayItem _currentDisplayItem;

    private bool _invalidatePending;
    private int _latestSetSourceOperationId;


    // For Dragging
    private Point _lastPoint;
    private bool _isDragging;

    // For Offscreen drawing
    private CanvasRenderTarget _offscreen;

    private readonly DispatcherTimer _offScreenDrawTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 410)
    };

    // For GIF and APNG File handling
    private IAnimator _animator;
    private readonly Stopwatch _animationStopwatch = new();
    private readonly SemaphoreSlim _animatorLock = new(1, 1);

    // For Checkered Background
    private CanvasImageBrush _checkeredBrush;
    private const int CheckerSize = 10;
    private bool _currentPhotoSupportsTransparency = false;

    private readonly CanvasViewState _canvasViewState;
    private readonly CanvasViewManager _canvasViewManager;

    #region Construction and Destruction

    public Win2dCanvasController(CanvasControl d2dCanvas, IThumbnailController thumbNailController)
    {
        _d2dCanvas = d2dCanvas;
        _thumbNailController = thumbNailController;

        _d2dCanvas.Draw += D2dCanvas_Draw;
        _d2dCanvas.SizeChanged += D2dCanvas_SizeChanged;

        _d2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        _d2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        _d2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        _d2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;

        _offScreenDrawTimer.Tick += OffScreenDrawTimer_Tick;
        _canvasViewState = new CanvasViewState();
        _canvasViewManager = new CanvasViewManager(_canvasViewState, RequestInvalidate);
    }

    public async Task CleanupOnClose()
    {
        await _animatorLock.WaitAsync();
        try
        {
            _offScreenDrawTimer.Stop();
            _animationStopwatch.Stop();
            if (_d2dCanvas != null) _d2dCanvas.Draw -= D2dCanvas_Draw;
            _d2dCanvas?.RemoveFromVisualTree();
            _animator?.Dispose();
            _animator = null;
            _checkeredBrush?.Dispose();
            _checkeredBrush = null;
        }
        finally
        {
            _animatorLock.Release();
        }
    }

    #endregion

    #region Public API

    public async Task SetSource(Photo photo, DisplayLevel displayLevel)
    {
        await _animatorLock.WaitAsync();
        _animatorLock.Release();

        var currentOperationId = ++_latestSetSourceOperationId;
        // Cleanup
        DestroyOffScreen();

        Photo.CurrentDisplayLevel = displayLevel;
        var isFirstPhoto = _currentDisplayItem == null;

        _currentDisplayItem = photo.GetDisplayItemBasedOn(displayLevel);
        _currentPhotoSupportsTransparency = photo.SupportsTransparency();

        if (_currentDisplayItem == null) return;

        if (_currentDisplayItem.IsGifOrAnimatedPng())
        {
            try
            {
                // Clean up previous animation
                _animator?.Dispose();
                _animator = null;
                _animationStopwatch.Stop();


                var preview = photo.GetDisplayItemBasedOn(DisplayLevel.Preview);
                bool previewDrawnAsFirstFrame = false;
                if (preview != null)
                {
                    previewDrawnAsFirstFrame = true;
                    _currentDisplayItem.Bitmap = preview.Bitmap;
                    _canvasViewManager.SetScaleAndPosition(_currentDisplayItem.Bitmap.Bounds.Width,
                        _currentDisplayItem.Bitmap.Bounds.Height,
                        _currentDisplayItem.Rotation, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight, isFirstPhoto);
                    _thumbNailController.CreateThumbnailRibbonOffScreen();
                    RequestInvalidate();
                }

                IAnimator newAnimator = Path.GetExtension(photo.FileName).ToUpper() switch
                {
                    ".GIF" => await GifAnimator.CreateAsync(_currentDisplayItem.FileAsByteArray),
                    _ => await PngAnimator.CreateAsync(_currentDisplayItem.FileAsByteArray)
                };

                // If SetSource had already been called a next time before returning from CreateAsync
                if (currentOperationId == _latestSetSourceOperationId)
                {
                    await newAnimator.UpdateAsync(TimeSpan.Zero);
                    _animator = newAnimator;
                    _animationStopwatch.Restart();
                    _canvasViewManager.SetScaleAndPosition(_animator.PixelWidth, _animator.PixelHeight,
                        _currentDisplayItem.Rotation, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight,
                        !previewDrawnAsFirstFrame);
                    if (!previewDrawnAsFirstFrame)
                        _thumbNailController.CreateThumbnailRibbonOffScreen();
                    RequestInvalidate();
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
            _canvasViewManager.SetScaleAndPosition(_currentDisplayItem.Bitmap.Bounds.Width,
                _currentDisplayItem.Bitmap.Bounds.Height,
                _currentDisplayItem.Rotation, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight, isFirstPhoto);

            _thumbNailController.CreateThumbnailRibbonOffScreen();
            RestartOffScreenDrawTimer();
            RequestInvalidate();
        }
    }

    public void SetHundredPercent(bool animateChange)
    {
        _canvasViewManager.ZoomPanToFit(animateChange, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight);
        RestartOffScreenDrawTimer();
    }

    public void ZoomOutOnExit(double exitAnimationDuration)
    {
        _canvasViewManager.ZoomOutOnExit(exitAnimationDuration, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight);
    }

    public void ZoomByKeyboard(bool zoomingIn)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.ZoomAtCenter(zoomingIn, _d2dCanvas.ActualWidth, _d2dCanvas.ActualHeight);
        RestartOffScreenDrawTimer();
    }

    public void PanByKeyboard(double dx, double dy)
    {
        if (IsScreenEmpty()) return;
        _canvasViewManager.Pan(dx, dy);
    }

    public void RotateCurrentPhotoBy90(bool clockWise)
    {
        if (IsScreenEmpty()) return;
        _currentDisplayItem.Rotation += (clockWise ? 90 : -90);
        _canvasViewManager.Rotate(_currentDisplayItem.Rotation);
    }

    #endregion

    #region Event Handlers

    private void D2dCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_checkeredBrush == null) CreateCheckeredBrush(sender);

        if (_currentDisplayItem == null) return;

        args.DrawingSession.Clear(Colors.Transparent);

        CanvasImageInterpolation drawingQuality;
        if (!AppConfig.Settings.HighQualityInterpolation || _canvasViewManager.PanZoomAnimationOnGoing)
            drawingQuality = CanvasImageInterpolation.NearestNeighbor;
        else
            drawingQuality = CanvasImageInterpolation.HighQualityCubic;

        if (_currentDisplayItem.IsGifOrAnimatedPng() && _animator != null)
        {
            if (_animator?.Surface == null) return;
            args.DrawingSession.Transform = _canvasViewState.Mat;

            if (AppConfig.Settings.CheckeredBackground && _currentPhotoSupportsTransparency)
                args.DrawingSession.FillRectangle(_canvasViewState.ImageRect, _checkeredBrush);

            args.DrawingSession.DrawImage(
                _animator.Surface, _canvasViewState.ImageRect,
                _animator.Surface.GetBounds(_d2dCanvas),
                1.0f,
                drawingQuality);

            _ = RunAnimationLoop();
        }
        else
        {
            args.DrawingSession.Transform = _canvasViewState.Mat;

            if (AppConfig.Settings.CheckeredBackground && _currentPhotoSupportsTransparency)
                args.DrawingSession.FillRectangle(_canvasViewState.ImageRect, _checkeredBrush);

            if (_offscreen != null)
                args.DrawingSession.DrawImage(_offscreen, _canvasViewState.ImageRect, _offscreen.Bounds, 1f,
                    drawingQuality);
            //args.DrawingSession.DrawRectangle(_imageRect, Colors.Green, 10f);
            else if (_currentDisplayItem != null)
                args.DrawingSession.DrawImage(_currentDisplayItem.Bitmap, _canvasViewState.ImageRect,
                    _currentDisplayItem.Bitmap.Bounds, 1f,
                    drawingQuality);
        }
    }

    private void D2dCanvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (IsScreenEmpty()) return;
        var imageBounds = _currentDisplayItem.IsGifOrAnimatedPng()
            ? _animator.Surface.GetBounds(_d2dCanvas)
            : _currentDisplayItem.Bitmap.Bounds;
        _canvasViewManager.HandleSizeChange(imageBounds, args.NewSize, args.PreviousSize);
    }

    private void D2dCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsScreenEmpty()) return;
        _lastPoint = e.GetCurrentPoint(_d2dCanvas).Position;
        if (!IsPressedOnImage(_lastPoint)) return;
        _d2dCanvas.CapturePointer(e.Pointer);
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
        if (IsScreenEmpty()) return;
        if (Util.IsControlPressed()) return;

        bool zoomingIn = e.GetCurrentPoint(_d2dCanvas).Properties.MouseWheelDelta > 0;
        _canvasViewManager.ZoomAtPoint(zoomingIn, e.GetCurrentPoint(_d2dCanvas).Position,
            _d2dCanvas.Device.MaximumBitmapSizeInPixels);
        RestartOffScreenDrawTimer();
    }

    #endregion

    #region Rendering And State Management

    private async Task RunAnimationLoop()
    {
        if (!await _animatorLock.WaitAsync(0)) return;
        try
        {
            if (_animator == null) return;
            await _animator.UpdateAsync(_animationStopwatch.Elapsed);
            RequestInvalidate();
        }
        catch (Exception ex)
        {
            _animationStopwatch.Stop();
        }
        finally
        {
            _animatorLock.Release();
        }
    }

    private void CreateCheckeredBrush(ICanvasResourceCreator resourceCreator)
    {
        _checkeredBrush?.Dispose();

        // Create a render target for the small 2x2 chekcer pattern
        using var patternRenderTarget = new CanvasRenderTarget(resourceCreator, CheckerSize * 2, CheckerSize * 2, 96);

        using (var ds = patternRenderTarget.CreateDrawingSession())
        {
            // The pattern is two white and two grey squares, forming a checkerboard
            var grey = Color.FromArgb(255, 204, 204, 204);
            ds.Clear(grey);
            ds.FillRectangle(0, 0, CheckerSize, CheckerSize, Colors.White);
            ds.FillRectangle(CheckerSize, CheckerSize, CheckerSize, CheckerSize, Colors.White);
        }

        // Create a brush from this pattern that can be tiled
        _checkeredBrush = new CanvasImageBrush(resourceCreator, patternRenderTarget)
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap,
            Interpolation = CanvasImageInterpolation.NearestNeighbor
        };
    }

    private void RestartOffScreenDrawTimer()
    {
        if (_currentDisplayItem != null && _currentDisplayItem.IsGifOrAnimatedPng()) return;
        _offScreenDrawTimer.Stop();
        _offScreenDrawTimer.Start();
    }

    private void OffScreenDrawTimer_Tick(object sender, object e)
    {
        _offScreenDrawTimer.Stop();

        if (_currentDisplayItem.IsGifOrAnimatedPng()) return;
        CreateOffScreen();
        _canvasViewState.UpdateTransform();
        RequestInvalidate();
    }

    private void CreateOffScreen()
    {
        var imageWidth = _canvasViewState.ImageRect.Width * _canvasViewState.Scale;
        var imageHeight = _canvasViewState.ImageRect.Height * _canvasViewState.Scale;

        if (_offscreen != null &&
            (_offscreen.SizeInPixels.Width != (int)imageWidth ||
             _offscreen.SizeInPixels.Height != (int)imageHeight))
        {
            DestroyOffScreen();
        }

        var drawingQuality = AppConfig.Settings.HighQualityInterpolation
            ? CanvasImageInterpolation.HighQualityCubic
            : CanvasImageInterpolation.NearestNeighbor;

        if (_offscreen == null && imageWidth < _d2dCanvas.ActualWidth * 1.5)
        {
            var tempOffScreen = new CanvasRenderTarget(_d2dCanvas, (float)imageWidth, (float)imageHeight);
            using var ds = tempOffScreen.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
            ds.DrawImage(_currentDisplayItem.Bitmap, new Rect(0, 0, imageWidth, imageHeight),
                _currentDisplayItem.Bitmap.Bounds, 1, drawingQuality);
            _offscreen = tempOffScreen;
        }
    }

    private void DestroyOffScreen()
    {
        if (_offscreen == null) return;
        _offscreen.Dispose();
        _offscreen = null;
    }

    private bool IsScreenEmpty()
    {
        return _currentDisplayItem == null;
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