using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI;
using FlyPhotos.Utils;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;

namespace FlyPhotos.Controllers;

internal class Win2dCanvasController : IThumbnailDisplayChangeable
{
    // private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int BoxSize = 40;
    private int NumOfThumbNailsInOneDirection = 20;

    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private CanvasRenderTarget _offscreen;
    private CanvasRenderTarget _thumbnailOffscreen;
    private DisplayItem _currentDisplayItem;

    private Rect _imageRect;
    private Point _imagePos = new(0, 0);
    private Point _lastPoint;

    private Matrix3x2 _mat;
    private Matrix3x2 _matInv;
    private float _scale = 1.0f;
    private float _lastScaleTo = 1.0f;
    private bool _isDragging;

    private readonly Grid _mainLayout;
    private readonly CanvasControl _d2dCanvas;

    private EventHandler<object> _renderingHandler;
    private DateTime _zoomStartTime;
    private float _zoomStartScale;
    private float _zoomTargetScale;
    private Point _zoomCenter;
    private const double ZoomAnimationDurationMs = 300;
    private bool zoomAnimationOnGoing = false;


    private readonly DispatcherTimer _redrawThumbnailTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 500)
    };

    private readonly DispatcherTimer _offScreenDrawTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 350)
    };

    private ConcurrentDictionary<int, Photo> _cachedPreviews;
    private bool _canDrawThumbnails;

    public Win2dCanvasController(Grid mainLayout, CanvasControl d2dCanvas)
    {
        _d2dCanvas = d2dCanvas;
        _mainLayout = mainLayout;

        _d2dCanvas.Draw += CanvasControl_OnDraw;

        _d2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        _d2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        _d2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        _d2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;

        _mainLayout.SizeChanged += Win2dTest_SizeChanged;

        _offScreenDrawTimer.Tick += OffScreenDrawTimer_Tick;
        _redrawThumbnailTimer.Tick += RedrawThumbnailTimer_Tick;
    }

    public void SetSource(Photo value, PhotoDisplayController.DisplayLevel displayLevel)
    {
        Photo.CurrentDisplayLevel = displayLevel;
        DestroyOffScreen();
        var firstPhoto = _currentDisplayItem == null;
        if (_currentDisplayItem != null && _currentDisplayItem.SoftwareBitmap != null) _currentDisplayItem.Bitmap = null;

        _currentDisplayItem = value.GetDisplayItemBasedOn(displayLevel);

        if (_currentDisplayItem.SoftwareBitmap != null)
            _currentDisplayItem.Bitmap = CanvasBitmap.CreateFromSoftwareBitmap(_d2dCanvas, _currentDisplayItem.SoftwareBitmap);
        var vertical = _currentDisplayItem.Rotation is 270 or 90;
        var horScale = _mainLayout.ActualWidth /
                       (vertical ? _currentDisplayItem.Bitmap.Bounds.Height : _currentDisplayItem.Bitmap.Bounds.Width);
        var vertScale = _mainLayout.ActualHeight /
                        (vertical ? _currentDisplayItem.Bitmap.Bounds.Width : _currentDisplayItem.Bitmap.Bounds.Height);
        var scaleFactor = Math.Min(horScale, vertScale);

        _imageRect = new Rect(0, 0, _currentDisplayItem.Bitmap.Bounds.Width * scaleFactor,
            _currentDisplayItem.Bitmap.Bounds.Height * scaleFactor);
        if (firstPhoto)
        {
            _imagePos.X = _mainLayout.ActualWidth / 2;
            _imagePos.Y = _mainLayout.ActualHeight / 2;
        }

        CreateThumbnailRibbonOffScreen();

        _offScreenDrawTimer.Stop();
        _offScreenDrawTimer.Start();

        UpdateTransform();
        _d2dCanvas.Invalidate();
    }


    public void SetHundredPercent(bool redraw)
    {
        if (_currentDisplayItem.Bitmap == null) return;
        _scale = 1f;
        _lastScaleTo = 1f;
        _imagePos.X = _mainLayout.ActualWidth / 2;
        _imagePos.Y = _mainLayout.ActualHeight / 2;
        if (redraw)
        {
            _offScreenDrawTimer.Stop();
            _offScreenDrawTimer.Start();
            UpdateTransform();
            _d2dCanvas.Invalidate();
        }
    }

    private void OffScreenDrawTimer_Tick(object sender, object e)
    {
        _offScreenDrawTimer.Stop();
        CreateOffScreen();
        UpdateTransform();
        _d2dCanvas.Invalidate();
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
        if (imageWidth < _mainLayout.ActualWidth * 1.5)
        {
            var tempOffScreen = new CanvasRenderTarget(_d2dCanvas, (float)imageWidth, (float)imageHeight);
            using var ds = tempOffScreen.CreateDrawingSession();
            ds.DrawImage(_currentDisplayItem.Bitmap, new Rect(0, 0, imageWidth, imageHeight),
                _currentDisplayItem.Bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            _offscreen = tempOffScreen;
        }
        else
        {
            DestroyOffScreen();
        }
    }

    public void RedrawThumbNailsIfNeeded(int index)
    {
        if (index >= Photo.CurrentDisplayIndex - NumOfThumbNailsInOneDirection &&
            index <= Photo.CurrentDisplayIndex + NumOfThumbNailsInOneDirection)
        {
            _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
            {
                _canDrawThumbnails = true;
                _redrawThumbnailTimer.Stop();
                _redrawThumbnailTimer.Start();
            });
        }
    }

    private void RedrawThumbnailTimer_Tick(object sender, object e)
    {
        CreateThumbnailRibbonOffScreen();
        _d2dCanvas.Invalidate();
    }

    private void CreateThumbnailRibbonOffScreen()
    {
        _redrawThumbnailTimer.Stop();
        if (Photo.PhotosCount <= 1 || !_canDrawThumbnails || !App.Settings.ShowThumbNails)
            return;

        if (_thumbnailOffscreen == null)
        {
            NumOfThumbNailsInOneDirection = (int)(_d2dCanvas.ActualWidth / (2 * BoxSize)) + 1;
            _thumbnailOffscreen = new CanvasRenderTarget(_d2dCanvas, (float)_d2dCanvas.ActualWidth, BoxSize);
        }

        using var dsThumbNail = _thumbnailOffscreen.CreateDrawingSession();
        dsThumbNail.Clear(Colors.Transparent);


        if (_cachedPreviews != null)
        {
            var startX = (int)_d2dCanvas.ActualWidth / 2 - BoxSize / 2;
            var startY = 0;

            for (var i = -NumOfThumbNailsInOneDirection; i <= NumOfThumbNailsInOneDirection; i++)
            {
                var index = Photo.CurrentDisplayIndex + i;

                if (index < 0 || index >= (Photo.PhotosCount)) continue;

                CanvasBitmap bitmap;

                if (_cachedPreviews.TryGetValue(index, out var photo) && photo.Preview != null && photo.Preview.Bitmap != null)
                {
                    bitmap = photo.Preview.Bitmap;
                }
                else
                {
                    bitmap = Photo.GetLoadingIndicator().Bitmap;
                }

                // Calculate the center square crop
                float bitmapWidth = bitmap.SizeInPixels.Width;
                float bitmapHeight = bitmap.SizeInPixels.Height;
                var cropSize = Math.Min(bitmapWidth, bitmapHeight);

                var cropX = (bitmapWidth - cropSize) / 2;
                var cropY = (bitmapHeight - cropSize) / 2;

                // Draw the cropped center of the image
                dsThumbNail.DrawImage(
                    bitmap,
                    new Windows.Foundation.Rect(startX + i * BoxSize, startY, BoxSize, BoxSize),
                    new Windows.Foundation.Rect(cropX, cropY, cropSize, cropSize), 0.5f,
                    CanvasImageInterpolation.NearestNeighbor // Source rectangle for the crop
                );
                if (index == Photo.CurrentDisplayIndex)
                {
                    dsThumbNail.DrawRectangle(new Rect(startX + i * BoxSize, startY, BoxSize, BoxSize),
                        Colors.GreenYellow, 3f);
                }
            }
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
            _d2dCanvas.Invalidate();
        }

        _lastPoint = e.GetCurrentPoint(_d2dCanvas).Position;
    }

    private void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var coreWindow = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var isControlPressed = coreWindow.HasFlag(CoreVirtualKeyStates.Down);
        if (isControlPressed)
        {
            return;
        }
        var delta = e.GetCurrentPoint(_d2dCanvas).Properties.MouseWheelDelta;

        var scalePercentage = delta > 0 ? 1.25f : 0.8f;

        var scaleTo = _lastScaleTo * scalePercentage;

        if (scaleTo < 0.05) return;

        _lastScaleTo = scaleTo;
        var adjustedScalePercentage = scaleTo / _scale;
        var newImageWidth = (float)_imageRect.Width * scaleTo;
        var newImageHeight = (float)_imageRect.Height * scaleTo;

        if (newImageWidth <= _device.MaximumBitmapSizeInPixels && newImageHeight <= _device.MaximumBitmapSizeInPixels)
        {
            var mousePosition = e.GetCurrentPoint(_d2dCanvas).Position;
            StartZoomAnimation(scaleTo, mousePosition);
            _offScreenDrawTimer.Stop();
            _offScreenDrawTimer.Start();
        }
    }

    public void ZoomByKeyboard(float scaleFactor)
    {
        var scaleTo = _lastScaleTo * scaleFactor;
        if (scaleTo < 0.05) return;
        _lastScaleTo = scaleTo;
        var center = new Point(_mainLayout.ActualWidth / 2, _mainLayout.ActualHeight / 2);
        StartZoomAnimation(scaleTo, center);
        _offScreenDrawTimer.Stop();
        _offScreenDrawTimer.Start();
    }

    public void PanByKeyboard(double dx, double dy)
    {
        _imagePos.X += dx;
        _imagePos.Y += dy;
        UpdateTransform();
        _d2dCanvas.Invalidate();
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
        zoomAnimationOnGoing = true;
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
        _d2dCanvas.Invalidate();

        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            zoomAnimationOnGoing = false;
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
        var drawingQuality = zoomAnimationOnGoing
            ? CanvasImageInterpolation.NearestNeighbor
            : CanvasImageInterpolation.HighQualityCubic;

        args.DrawingSession.Transform = _mat;
        if (_offscreen != null)
            args.DrawingSession.DrawImage(_offscreen, _imageRect, _offscreen.Bounds, 1f,
                drawingQuality);
        //args.DrawingSession.DrawRectangle(_imageRect, Colors.Green, 10f);
        else if (_currentDisplayItem != null)
            args.DrawingSession.DrawImage(_currentDisplayItem.Bitmap, _imageRect, _currentDisplayItem.Bitmap.Bounds, 1f,
                drawingQuality);
        
        if (_thumbnailOffscreen != null && App.Settings.ShowThumbNails)
        {
            var drawRect = _thumbnailOffscreen.Bounds;
            drawRect._y = (float)(_d2dCanvas.ActualHeight - BoxSize);
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.Identity;
            args.DrawingSession.DrawImage(_thumbnailOffscreen, drawRect, _thumbnailOffscreen.Bounds, 1.0f,
                CanvasImageInterpolation.NearestNeighbor);
        }

    }

    private void Win2dTest_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_currentDisplayItem == null) return;
        var scaleFactor = Math.Min(args.NewSize.Width / _currentDisplayItem.Bitmap.Bounds.Width,
            args.NewSize.Height / _currentDisplayItem.Bitmap.Bounds.Height);
        _imageRect = new Rect(0, 0, _currentDisplayItem.Bitmap.Bounds.Width * scaleFactor,
            _currentDisplayItem.Bitmap.Bounds.Height * scaleFactor);
        var xChangeRatio = args.NewSize.Width / args.PreviousSize.Width;
        var yChangeRatio = args.NewSize.Height / args.PreviousSize.Height;
        _imagePos.X *= xChangeRatio;
        _imagePos.Y *= yChangeRatio;

        NumOfThumbNailsInOneDirection = (int)(_d2dCanvas.ActualWidth / (2 * BoxSize)) + 1;

        CreateThumbnailRibbonOffScreen();
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
        _d2dCanvas.Invalidate();
    }

    public void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews)
    {
        _cachedPreviews = cachedPreviews;
    }

    public void ShowThumbnailBasedOnSettings()
    {
        if (App.Settings.ShowThumbNails)
        {
            CreateThumbnailRibbonOffScreen();
            _d2dCanvas.Invalidate();
        }
        else
        {
            _d2dCanvas.Invalidate();
        }
    }
}

public interface IThumbnailDisplayChangeable
{
    void ShowThumbnailBasedOnSettings();
}
public class ZoomAnimationInfo(float animScale, double animXPos, double animYPos, float incrementalScale)
{
    public float Scale = animScale;
    public double X = animXPos;
    public double Y = animYPos;
    public float IncrementalScale = incrementalScale;
}