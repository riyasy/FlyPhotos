using FlyPhotos.Controllers.Animators;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using static FlyPhotos.Controllers.PhotoDisplayController;

namespace FlyPhotos.Controllers;

internal class Win2dCanvasController : ICanvasController
{
    // private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly SemaphoreSlim _animatorLock = new SemaphoreSlim(1, 1);

    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private CanvasRenderTarget _offscreen;
    
    private DisplayItem _currentDisplayItem;

    private Rect _imageRect;
    private Point _imagePos = new(0, 0);
    private Point _lastPoint;

    private Matrix3x2 _mat;
    private Matrix3x2 _matInv;
    private float _scale = 1.0f;
    private float _lastScaleTo = 1.0f;
    private bool _isDragging;

    private readonly CanvasControl _d2dCanvas;

    private EventHandler<object> _renderingHandler;
    private DateTime _zoomStartTime;
    private float _zoomStartScale;
    private float _zoomTargetScale;
    private Point _zoomCenter;
    private const double ZoomAnimationDurationMs = 300;
    private bool _zoomAnimationOnGoing = false;

    private bool _invalidatePending = false;

    private int _latestSetSourceOperationId;

    private readonly DispatcherTimer _offScreenDrawTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 350)
    };    
    
    private readonly IThumbnailController _thumbNailController;

    // For GIF File handling
    private IAnimator _animator;
    private readonly Stopwatch _animationStopwatch = new();
    private bool _isAnimationLoopRunning = false;

    public Win2dCanvasController(CanvasControl d2dCanvas, IThumbnailController thumbNailController)
    {
        _d2dCanvas = d2dCanvas;
        _thumbNailController = thumbNailController;        

        _d2dCanvas.Draw += CanvasControl_OnDraw;
        _d2dCanvas.SizeChanged += D2dCanvas_SizeChanged;

        _d2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        _d2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        _d2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        _d2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;        

        _offScreenDrawTimer.Tick += OffScreenDrawTimer_Tick;        
    }

    public async Task SetSource(Photo value, DisplayLevel displayLevel)
    {
        await _animatorLock.WaitAsync();
        _animatorLock.Release();

        var currentOperationId = ++_latestSetSourceOperationId;
        // Cleanup
        DestroyOffScreen();
        if (_currentDisplayItem != null && _currentDisplayItem.SoftwareBitmap != null) _currentDisplayItem.Bitmap = null;

        Photo.CurrentDisplayLevel = displayLevel;
        var isFirstPhoto = _currentDisplayItem == null;

        _currentDisplayItem = value.GetDisplayItemBasedOn(displayLevel);

        if (_currentDisplayItem == null) return;

        if (_currentDisplayItem.IsGifOrAnimatedPng())
        {
            try
            {
                // Clean up previous animation
                _animator?.Dispose();
                _animator = null;
                _animationStopwatch.Stop();


                var preview = value.GetDisplayItemBasedOn(DisplayLevel.Preview);
                bool previewDrawnAsFirstFrame = false;
                if (preview != null)
                {
                    previewDrawnAsFirstFrame = true;
                    _currentDisplayItem.Bitmap = preview.Bitmap;
                    SetScaleAndPositionForStaticImage(isFirstPhoto);
                    _thumbNailController.CreateThumbnailRibbonOffScreen();
                    UpdateTransform();
                    RequestInvalidate();
                }

                IAnimator newAnimator = Path.GetExtension(value.FileName).ToUpper() switch
                {
                    ".GIF" => await GifAnimator.CreateAsync(_currentDisplayItem.FileAsByteArray),
                    _ => await PngAnimator.CreateAsync(_currentDisplayItem.FileAsByteArray)
                };

                // If SetSource had already been called a next time before returning from CreateAsync
                if (currentOperationId == _latestSetSourceOperationId)
                {
                    _animator = newAnimator;
                    _animationStopwatch.Restart();
                    await _animator.UpdateAsync(TimeSpan.Zero);
                    SetScaleAndPositionForGif(!previewDrawnAsFirstFrame);
                    if (!previewDrawnAsFirstFrame)
                        _thumbNailController.CreateThumbnailRibbonOffScreen();
                    UpdateTransform();
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
            if (_currentDisplayItem.SoftwareBitmap != null)
                _currentDisplayItem.Bitmap = CanvasBitmap.CreateFromSoftwareBitmap(_d2dCanvas, _currentDisplayItem.SoftwareBitmap);
            
            SetScaleAndPositionForStaticImage(isFirstPhoto);
            _thumbNailController.CreateThumbnailRibbonOffScreen();
            RestartOffScreenDrawTimer();
            UpdateTransform();
            RequestInvalidate();
        }
    }

    private void SetScaleAndPositionForGif(bool isFirstPhoto)
    {
        var vertical = _currentDisplayItem.Rotation is 270 or 90;
        var horScale = _d2dCanvas.ActualWidth /
                       (vertical ? _animator.PixelHeight : _animator.PixelWidth);
        var vertScale = _d2dCanvas.ActualHeight /
                        (vertical ? _animator.PixelWidth : _animator.PixelHeight);
        var scaleFactor = Math.Min(horScale, vertScale);

        _imageRect = new Rect(0, 0, _animator.PixelWidth * scaleFactor,
            _animator.PixelHeight * scaleFactor);
        if (isFirstPhoto)
        {
            _imagePos.X = _d2dCanvas.ActualWidth / 2;
            _imagePos.Y = _d2dCanvas.ActualHeight / 2;
        }
    }

    private void SetScaleAndPositionForStaticImage(bool isFirstPhoto)
    {
        var vertical = _currentDisplayItem.Rotation is 270 or 90;
        var horScale = _d2dCanvas.ActualWidth /
                       (vertical
                           ? _currentDisplayItem.Bitmap.Bounds.Height
                           : _currentDisplayItem.Bitmap.Bounds.Width);
        var vertScale = _d2dCanvas.ActualHeight /
                        (vertical
                            ? _currentDisplayItem.Bitmap.Bounds.Width
                            : _currentDisplayItem.Bitmap.Bounds.Height);
        var scaleFactor = Math.Min(horScale, vertScale);

        _imageRect = new Rect(0, 0, _currentDisplayItem.Bitmap.Bounds.Width * scaleFactor,
            _currentDisplayItem.Bitmap.Bounds.Height * scaleFactor);

        if (isFirstPhoto)
        {
            _imagePos.X = _d2dCanvas.ActualWidth / 2;
            _imagePos.Y = _d2dCanvas.ActualHeight / 2;
        }
    }

    private async Task RunGifAnimationLoop()
    {
        if (!_animatorLock.Wait(0)) return;

        try
        {
            if (_animator == null) return;
            _isAnimationLoopRunning = true;
            await _animator.UpdateAsync(_animationStopwatch.Elapsed);
            RequestInvalidate();
        }
        catch (Exception ex)
        {
            _animationStopwatch.Stop();
        }
        finally
        {
            _isAnimationLoopRunning = false;
            _animatorLock.Release();
        }
    }

    private void RestartOffScreenDrawTimer()
    {
        if (_currentDisplayItem.IsGifOrAnimatedPng()) return;
        _offScreenDrawTimer.Stop();
        _offScreenDrawTimer.Start();
    }

    public void SetHundredPercent(bool redraw)
    {
        _scale = 1f;
        _lastScaleTo = 1f;
        _imagePos.X = _d2dCanvas.ActualWidth / 2;
        _imagePos.Y = _d2dCanvas.ActualHeight / 2;
        if (!redraw) return;
        RestartOffScreenDrawTimer();
        UpdateTransform();
        RequestInvalidate();
    }

    private void OffScreenDrawTimer_Tick(object sender, object e)
    {
        _offScreenDrawTimer.Stop();

        if (_currentDisplayItem.IsGifOrAnimatedPng()) return;
        CreateOffScreen();
        UpdateTransform();
        RequestInvalidate();
    }

    private void D2dCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _lastPoint = e.GetCurrentPoint(_d2dCanvas).Position;
        if (!IsPressedOnImage(_lastPoint)) return;
        _d2dCanvas.CapturePointer(e.Pointer);
        _isDragging = true;
    }

    private void CreateOffScreen()
    {
        var imageWidth = _imageRect.Width * _scale;
        var imageHeight = _imageRect.Height * _scale;

        if (_offscreen != null &&
            (_offscreen.SizeInPixels.Width != (int)imageWidth || 
            _offscreen.SizeInPixels.Height != (int)imageHeight))
        {
            DestroyOffScreen();
        }

        if (_offscreen == null && imageWidth < _d2dCanvas.ActualWidth * 1.5)
        {
            var tempOffScreen = new CanvasRenderTarget(_d2dCanvas, (float)imageWidth, (float)imageHeight);
            using var ds = tempOffScreen.CreateDrawingSession();
            ds.DrawImage(_currentDisplayItem.Bitmap, new Rect(0, 0, imageWidth, imageHeight),
                _currentDisplayItem.Bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            _offscreen = tempOffScreen;
        }
    }

    private void DestroyOffScreen()
    {
        if (_offscreen == null) return;
        _offscreen.Dispose();
        _offscreen = null;
    }

    private void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _d2dCanvas.ReleasePointerCapture(e.Pointer);
        _isDragging = false;
    }

    private void D2dCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _imagePos.X += e.GetCurrentPoint(_d2dCanvas).Position.X - _lastPoint.X;
            _imagePos.Y += e.GetCurrentPoint(_d2dCanvas).Position.Y - _lastPoint.Y;
            UpdateTransform();
            RequestInvalidate();
        }

        _lastPoint = e.GetCurrentPoint(_d2dCanvas).Position;
    }

    private void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var coreWindow = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var isControlPressed = coreWindow.HasFlag(CoreVirtualKeyStates.Down);
        if (isControlPressed)
        {
            return;
        }
        var delta = e.GetCurrentPoint(_d2dCanvas).Properties.MouseWheelDelta;

        var scalePercentage = delta > 0 ? 1.25f : 0.8f;

        var scaleTo = _lastScaleTo * scalePercentage;

        // Lower limit of zoom
        if (scaleTo < 0.05) return;

        var adjustedScalePercentage = scaleTo / _scale;
        var newImageWidth = (float)_imageRect.Width * scaleTo;
        var newImageHeight = (float)_imageRect.Height * scaleTo;

        // Upper limit of zoom
        if (newImageWidth > _device.MaximumBitmapSizeInPixels || 
            newImageHeight > _device.MaximumBitmapSizeInPixels)
        {
            return;
        }

        _lastScaleTo = scaleTo;
        var mousePosition = e.GetCurrentPoint(_d2dCanvas).Position;
        StartZoomAnimation(scaleTo, mousePosition);
        RestartOffScreenDrawTimer();
    }

    public void ZoomByKeyboard(float scaleFactor)
    {
        var scaleTo = _lastScaleTo * scaleFactor;
        if (scaleTo < 0.05) return;
        _lastScaleTo = scaleTo;
        var center = new Point(_d2dCanvas.ActualWidth / 2, _d2dCanvas.ActualHeight / 2);
        StartZoomAnimation(scaleTo, center);
        RestartOffScreenDrawTimer();
    }

    public void PanByKeyboard(double dx, double dy)
    {
        _imagePos.X += dx;
        _imagePos.Y += dy;
        UpdateTransform();
        RequestInvalidate();
    }

    private void StartZoomAnimation(float targetScale, Point zoomCenter)
    {
        _zoomStartTime = DateTime.UtcNow;
        _zoomStartScale = _scale;
        _zoomTargetScale = targetScale;
        _zoomCenter = zoomCenter;

        if (_renderingHandler != null)
            CompositionTarget.Rendering -= _renderingHandler;

        _renderingHandler = (_, _) => AnimateZoom();
        CompositionTarget.Rendering += _renderingHandler;
        _zoomAnimationOnGoing = true;
    }

    private void AnimateZoom()
    {
        var elapsed = (DateTime.UtcNow - _zoomStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / ZoomAnimationDurationMs, 0.0, 1.0);

        // Ease-out cubic: f(t) = 1 - (1 - t)^3
        float easedT = 1f - (float)Math.Pow(1 - t, 3);

        var newScale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;

        // Maintain zoom center relative to the mouse
        _imagePos.X = _zoomCenter.X - (newScale / _scale) * (_zoomCenter.X - _imagePos.X);
        _imagePos.Y = _zoomCenter.Y - (newScale / _scale) * (_zoomCenter.Y - _imagePos.Y);

        _scale = newScale;
        UpdateTransform();
        RequestInvalidate();

        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            _zoomAnimationOnGoing = false;
        }
    }

    private void UpdateTransform()
    {
        _mat = Matrix3x2.Identity;
        _mat *= Matrix3x2.CreateTranslation((float)(-_imageRect.Width * 0.5f), (float)(-_imageRect.Height * 0.5f));
        _mat *= Matrix3x2.CreateScale(_scale, _scale);
        _mat *= Matrix3x2.CreateRotation((float)(Math.PI * _currentDisplayItem.Rotation / 180f));
        _mat *= Matrix3x2.CreateTranslation((float)_imagePos.X, (float)_imagePos.Y);
        Matrix3x2.Invert(_mat, out _matInv);
    }

    private void CanvasControl_OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_currentDisplayItem == null) return;

        var drawingQuality = _zoomAnimationOnGoing
            ? CanvasImageInterpolation.NearestNeighbor
            : CanvasImageInterpolation.HighQualityCubic;

        if (_currentDisplayItem.IsGifOrAnimatedPng() && _animator != null)
        {
            if (_animator?.Surface == null) return;
            args.DrawingSession.Transform = _mat;

            args.DrawingSession.DrawImage(
                _animator.Surface,
                _imageRect,
                _animator.Surface.GetBounds(_d2dCanvas),
                1.0f,
                drawingQuality);

            if (!_isAnimationLoopRunning) RunGifAnimationLoop();
        }
        else
        { 
            args.DrawingSession.Transform = _mat;
            if (_offscreen != null)
                args.DrawingSession.DrawImage(_offscreen, _imageRect, _offscreen.Bounds, 1f,
                    drawingQuality);
            //args.DrawingSession.DrawRectangle(_imageRect, Colors.Green, 10f);
            else if (_currentDisplayItem != null)
                args.DrawingSession.DrawImage(_currentDisplayItem.Bitmap, _imageRect, _currentDisplayItem.Bitmap.Bounds, 1f,
                    drawingQuality);
        }
    }

    private void D2dCanvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_currentDisplayItem == null) return;

        var imageBounds = _currentDisplayItem.IsGifOrAnimatedPng() ? _animator.Surface.GetBounds(_d2dCanvas) : _currentDisplayItem.Bitmap.Bounds;

        var scaleFactor = Math.Min(args.NewSize.Width / imageBounds.Width,
            args.NewSize.Height / imageBounds.Height);
        _imageRect = new Rect(0, 0, imageBounds.Width * scaleFactor,
            imageBounds.Height * scaleFactor);

        var xChangeRatio = args.NewSize.Width / args.PreviousSize.Width;
        var yChangeRatio = args.NewSize.Height / args.PreviousSize.Height;
        _imagePos.X *= xChangeRatio;
        _imagePos.Y *= yChangeRatio;

        UpdateTransform();
    }

    public bool IsPressedOnImage(Point position)
    {
        var tp = Vector2.Transform(new Vector2((float)position.X, (float)position.Y), _matInv);
        return ContainsPoint(_imageRect, tp);
    }

    private static bool ContainsPoint(Rect rect, Vector2 p)
    {
        return p.X >= rect.X && p.Y >= rect.Y
                             && p.X <= rect.Right && p.Y <= rect.Bottom;
    }

    internal void RotateCurrentPhotoBy90(bool clockWise)
    {
        _currentDisplayItem.Rotation += (clockWise ? 90 : -90);
        UpdateTransform();
        RequestInvalidate();
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

    public async Task CleanupOnClose()
    {
        await _animatorLock.WaitAsync();
        try
        {
            _animationStopwatch.Stop();
            if (_d2dCanvas != null) _d2dCanvas.Draw -= CanvasControl_OnDraw;
            _d2dCanvas?.RemoveFromVisualTree();
            _animator?.Dispose();
            _animator = null;
        }
        finally
        {
            _animatorLock.Release();
        }
    }
}

internal interface ICanvasController
{    
    Task SetSource(Photo firstPhoto, DisplayLevel hq);
    void SetHundredPercent(bool redraw);
}