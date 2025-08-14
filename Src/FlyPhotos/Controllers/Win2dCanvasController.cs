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
using FlyPhotos.AppSettings;
using static FlyPhotos.Controllers.PhotoDisplayController;

namespace FlyPhotos.Controllers;

internal class Win2dCanvasController : ICanvasController
{
    // private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IThumbnailController _thumbNailController;
    private readonly CanvasControl _d2dCanvas;
    private DisplayItem _currentDisplayItem;

    private bool _invalidatePending;
    private int _latestSetSourceOperationId;

    // For Image Positioning and Scaling (Zoom and Pan)
    private Rect _imageRect;
    private Point _imagePos = new(0, 0);
    private Matrix3x2 _mat;
    private Matrix3x2 _matInv;
    private float _scale = 1.0f;
    private float _lastScaleTo = 1.0f;

    // For Dragging
    private Point _lastPoint;
    private bool _isDragging;

    // For Offscreen drawing
    private CanvasRenderTarget _offscreen;
    private readonly DispatcherTimer _offScreenDrawTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 350)
    };

    // FOR ZOOM
    private EventHandler<object> _renderingHandler;

    private DateTime _panZoomAnimationStartTime;
    private double PanZoomAnimationDurationMs = 400;
    private bool _panZoomAnimationOnGoing;

    private float _zoomStartScale;
    private float _zoomTargetScale;
    private Point _zoomCenter;

    private Point _panStartPosition;
    private Point _panTargetPosition;


    // For GIF and APNG File handling
    private IAnimator _animator;
    private readonly Stopwatch _animationStopwatch = new();
    private readonly SemaphoreSlim _animatorLock = new(1, 1);

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
        }
        finally
        {
            _animatorLock.Release();
        }
    }

    #endregion

    #region Public API

    public async Task SetSource(Photo value, DisplayLevel displayLevel)
    {
        await _animatorLock.WaitAsync();
        _animatorLock.Release();

        var currentOperationId = ++_latestSetSourceOperationId;
        // Cleanup
        DestroyOffScreen();

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
                    SetScaleAndPosition(new Size(_currentDisplayItem.Bitmap.Bounds.Width, _currentDisplayItem.Bitmap.Bounds.Height), isFirstPhoto);
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
                    SetScaleAndPosition(new Size(_animator.PixelWidth, _animator.PixelHeight), !previewDrawnAsFirstFrame);
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
            SetScaleAndPosition(new Size(_currentDisplayItem.Bitmap.Bounds.Width, _currentDisplayItem.Bitmap.Bounds.Height), isFirstPhoto);
            _thumbNailController.CreateThumbnailRibbonOffScreen();
            RestartOffScreenDrawTimer();
            UpdateTransform();
            RequestInvalidate();
        }
    }

    public void SetHundredPercent(bool redraw)
    {
        if (!redraw)
        {
            // If not redrawing, set instantly as before
            _scale = 1f;
            _lastScaleTo = 1f;
            _imagePos.X = _d2dCanvas.ActualWidth / 2;
            _imagePos.Y = _d2dCanvas.ActualHeight / 2;
            return;
        }

        // Define the target state
        const float targetScale = 1.0f;
        var targetPosition = new Point(_d2dCanvas.ActualWidth / 2, _d2dCanvas.ActualHeight / 2);

        // This is important for subsequent mouse-wheel zooms to work correctly
        _lastScaleTo = targetScale;

        // Start the new pan-and-zoom animation
        StartPanAndZoomAnimation(targetScale, targetPosition);
        RestartOffScreenDrawTimer();
    }
    public void ZoomOutOnExit(double exitAnimationDuration)
    {
        PanZoomAnimationDurationMs = exitAnimationDuration;
        var targetPosition = new Point(_d2dCanvas.ActualWidth / 2, _d2dCanvas.ActualHeight / 2);
        StartPanAndZoomAnimation(0.001f, targetPosition);
    }

    public void ZoomByKeyboard(float scaleFactor)
    {
        if (IsScreenEmpty()) return;
        var scaleTo = _lastScaleTo * scaleFactor;
        if (scaleTo < 0.05) return;
        _lastScaleTo = scaleTo;
        var center = new Point(_d2dCanvas.ActualWidth / 2, _d2dCanvas.ActualHeight / 2);
        StartZoomAnimation(scaleTo, center);
        RestartOffScreenDrawTimer();
    }

    public void PanByKeyboard(double dx, double dy)
    {
        if (IsScreenEmpty()) return;
        _imagePos.X += dx;
        _imagePos.Y += dy;
        UpdateTransform();
        RequestInvalidate();
    }

    public void RotateCurrentPhotoBy90(bool clockWise)
    {
        if (IsScreenEmpty()) return;
        _currentDisplayItem.Rotation += (clockWise ? 90 : -90);
        UpdateTransform();
        RequestInvalidate();
    }

    #endregion

    #region Event Handlers

    private void D2dCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_currentDisplayItem == null) return;

        var drawingQuality = _panZoomAnimationOnGoing
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

            _ = RunAnimationLoop();
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
        if (IsScreenEmpty()) return;

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
        if (_isDragging)
        {
            _imagePos.X += e.GetCurrentPoint(_d2dCanvas).Position.X - _lastPoint.X;
            _imagePos.Y += e.GetCurrentPoint(_d2dCanvas).Position.Y - _lastPoint.Y;
            UpdateTransform();
            RequestInvalidate();
        }

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

        var newImageWidth = (float)_imageRect.Width * scaleTo;
        var newImageHeight = (float)_imageRect.Height * scaleTo;

        // Upper limit of zoom
        if (newImageWidth > _d2dCanvas.Device.MaximumBitmapSizeInPixels ||
            newImageHeight > _d2dCanvas.Device.MaximumBitmapSizeInPixels)
        {
            return;
        }

        _lastScaleTo = scaleTo;
        var mousePosition = e.GetCurrentPoint(_d2dCanvas).Position;
        StartZoomAnimation(scaleTo, mousePosition);
        RestartOffScreenDrawTimer();
    }

    #endregion

    #region Rendering And State Management

    private void SetScaleAndPosition(Size imageSize, bool isFirstPhoto)
    {
        var vertical = _currentDisplayItem.Rotation is 270 or 90;
        var canvasWidth = _d2dCanvas.ActualWidth;
        var canvasHeight = _d2dCanvas.ActualHeight;

        // Use rotated dimensions for scaling calculation
        var effectiveWidth = vertical ? imageSize.Height : imageSize.Width;
        var effectiveHeight = vertical ? imageSize.Width : imageSize.Height;

        var horScale = canvasWidth / effectiveWidth;
        var vertScale = canvasHeight / effectiveHeight;
        var scaleFactor = Math.Min(horScale, vertScale);

        // Note: The _imageRect should always be based on the un-rotated dimensions.
        // The rotation is applied later in the transform matrix.
        _imageRect = new Rect(0, 0, imageSize.Width * scaleFactor, imageSize.Height * scaleFactor);

        if (isFirstPhoto)
        {
            _imagePos.X = canvasWidth / 2;
            _imagePos.Y = canvasHeight / 2;

            if (AppConfig.Settings.OpenExitZoom)
            {
                var targetPosition = new Point(_d2dCanvas.ActualWidth / 2, _d2dCanvas.ActualHeight / 2);
                _scale = 0.1f;
                StartPanAndZoomAnimation(1.0f, targetPosition);
            }
        }
    }

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

    private void StartZoomAnimation(float targetScale, Point zoomCenter)
    {
        _panZoomAnimationStartTime = DateTime.UtcNow;
        _zoomStartScale = _scale;
        _zoomTargetScale = targetScale;
        _zoomCenter = zoomCenter;

        if (_renderingHandler != null)
            CompositionTarget.Rendering -= _renderingHandler;

        _renderingHandler = (_, _) => AnimateZoom();
        CompositionTarget.Rendering += _renderingHandler;
        _panZoomAnimationOnGoing = true;
    }

    private void StartPanAndZoomAnimation(float targetScale, Point targetPosition)
    {
        // Setup animation state
        _panZoomAnimationStartTime = DateTime.UtcNow;
        _zoomStartScale = _scale;
        _zoomTargetScale = targetScale;
        _panStartPosition = _imagePos;
        _panTargetPosition = targetPosition;

        // Ensure any previous animation is stopped
        if (_renderingHandler != null)
            CompositionTarget.Rendering -= _renderingHandler;

        // Point the handler to the NEW animation method
        _renderingHandler = (_, _) => AnimatePanAndZoom();
        CompositionTarget.Rendering += _renderingHandler;
        _panZoomAnimationOnGoing = true;
    }

    private void AnimateZoom()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / PanZoomAnimationDurationMs, 0.0, 1.0);

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
            _panZoomAnimationOnGoing = false;
        }
    }

    private void AnimatePanAndZoom()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / PanZoomAnimationDurationMs, 0.0, 1.0);

        // Ease-out cubic: f(t) = 1 - (1 - t)^3
        float easedT = 1f - (float)Math.Pow(1 - t, 3);

        // Interpolate scale
        _scale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;

        // Interpolate position (a simple linear interpolation)
        var newX = _panStartPosition.X + (_panTargetPosition.X - _panStartPosition.X) * easedT;
        var newY = _panStartPosition.Y + (_panTargetPosition.Y - _panStartPosition.Y) * easedT;
        _imagePos = new Point(newX, newY);

        UpdateTransform();
        RequestInvalidate();

        if (t >= 1.0)
        {
            // Animation finished, stop the handler
            CompositionTarget.Rendering -= _renderingHandler;
            _panZoomAnimationOnGoing = false;
        }
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
        UpdateTransform();
        RequestInvalidate();
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

    public bool IsScreenEmpty()
    {
        return _currentDisplayItem == null;
    }

    public bool IsPressedOnImage(Point position)
    {
        var tp = Vector2.Transform(new Vector2((float)position.X, (float)position.Y), _matInv);
        return tp.X >= _imageRect.X && tp.Y >= _imageRect.Y
                                    && tp.X <= _imageRect.Right && tp.Y <= _imageRect.Bottom;
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
    Task SetSource(Photo firstPhoto, DisplayLevel hq);
    void SetHundredPercent(bool redraw);
}