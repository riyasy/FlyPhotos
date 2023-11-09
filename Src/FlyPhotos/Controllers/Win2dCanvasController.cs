using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NLog;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;

namespace FlyPhotos.Controllers;

internal class Win2dCanvasController
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private CanvasRenderTarget _offscreen;
    private Photo _currentPhoto;

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

    private readonly Queue<ZoomAnimationInfo> _zoomAnimationInfos = new();

    private readonly DispatcherTimer _zoomAnimationTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 10)
    };

    private readonly DispatcherTimer _offScreenDrawTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 350)
    };

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
        _zoomAnimationTimer.Tick += ZoomAnimationTimer_Tick;
    }

    public Photo Source
    {
        get => _currentPhoto;
        set
        {
            DestroyOffScreen();
            var firstPhoto = _currentPhoto == null;
            if (_currentPhoto != null && _currentPhoto.SoftwareBitmap != null) _currentPhoto.Bitmap = null;
            _currentPhoto = value;
            if (_currentPhoto.SoftwareBitmap != null)
                _currentPhoto.Bitmap = CanvasBitmap.CreateFromSoftwareBitmap(_d2dCanvas, _currentPhoto.SoftwareBitmap);
            var vertical = _currentPhoto.Rotation is 270 or 90;
            var horScale = _mainLayout.ActualWidth /
                           (vertical ? _currentPhoto.Bitmap.Bounds.Height : _currentPhoto.Bitmap.Bounds.Width);
            var vertScale = _mainLayout.ActualHeight /
                            (vertical ? _currentPhoto.Bitmap.Bounds.Width : _currentPhoto.Bitmap.Bounds.Height);
            var scaleFactor = Math.Min(horScale, vertScale);

            _imageRect = new Rect(0, 0, _currentPhoto.Bitmap.Bounds.Width * scaleFactor,
                _currentPhoto.Bitmap.Bounds.Height * scaleFactor);
            if (firstPhoto)
            {
                _imagePos.X = _mainLayout.ActualWidth / 2;
                _imagePos.Y = _mainLayout.ActualHeight / 2;
            }

            _offScreenDrawTimer.Stop();
            _offScreenDrawTimer.Start();

            UpdateTransform();
            _d2dCanvas.Invalidate();
        }
    }

    public void SetHundredPercent(bool redraw)
    {
        if (_currentPhoto.Bitmap == null) return;
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
            ds.DrawImage(_currentPhoto.Bitmap, new Rect(0, 0, imageWidth, imageHeight),
                _currentPhoto.Bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            _offscreen = tempOffScreen;
        }
        else
        {
            DestroyOffScreen();
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
            CreateAnimationData(adjustedScalePercentage, mousePosition);
            _offScreenDrawTimer.Stop();
            _offScreenDrawTimer.Start();
        }
    }

    private void CreateAnimationData(float scalePercentage, Point mousePosition)
    {
        _zoomAnimationTimer.Stop();
        _zoomAnimationInfos.Clear();
        const int noOfFrames = 5;
        var incrementalScale = (scalePercentage - 1f) / noOfFrames;
        for (var i = 0; i < noOfFrames; i++)
        {
            var animScale = _scale * (1f + incrementalScale * i);
            var animXPos = mousePosition.X - (1f + incrementalScale * i) * (mousePosition.X - _imagePos.X);
            var animYPos = mousePosition.Y - (1f + incrementalScale * i) * (mousePosition.Y - _imagePos.Y);
            _zoomAnimationInfos.Enqueue(new ZoomAnimationInfo(animScale, animXPos, animYPos, incrementalScale));
        }

        _zoomAnimationTimer.Start();
    }

    private void ZoomAnimationTimer_Tick(object sender, object e)
    {
        if (_zoomAnimationInfos.Count > 0)
        {
            var zoomInfo = _zoomAnimationInfos.Dequeue();
            _scale = zoomInfo.Scale;
            _imagePos.X = zoomInfo.X;
            _imagePos.Y = zoomInfo.Y;
            UpdateTransform();
            _d2dCanvas.Invalidate();
        }
        else
        {
            _zoomAnimationTimer.Stop();
        }
    }

    private void UpdateTransform()
    {
        _mat = Matrix3x2.Identity;
        _mat *= Matrix3x2.CreateTranslation((float)(-_imageRect.Width * 0.5f), (float)(-_imageRect.Height * 0.5f));
        _mat *= Matrix3x2.CreateScale(_scale, _scale);
        _mat *= Matrix3x2.CreateRotation((float)(Math.PI * _currentPhoto.Rotation / 180f));
        _mat *= Matrix3x2.CreateTranslation((float)_imagePos.X, (float)_imagePos.Y);
        Matrix3x2.Invert(_mat, out _matInv);
    }

    private void CanvasControl_OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        args.DrawingSession.Transform = _mat;
        if (_offscreen != null)
            args.DrawingSession.DrawImage(_offscreen, _imageRect, _offscreen.Bounds, 1f,
                CanvasImageInterpolation.HighQualityCubic);

        //args.DrawingSession.DrawRectangle(_imageRect, Colors.Green, 10f);
        else if (_currentPhoto != null)
            args.DrawingSession.DrawImage(_currentPhoto.Bitmap, _imageRect, _currentPhoto.Bitmap.Bounds, 1,
                CanvasImageInterpolation.HighQualityCubic);
        //args.DrawingSession.DrawRectangle(_imageRect, Colors.Red, 10f);
    }

    private void Win2dTest_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_currentPhoto == null) return;
        var scaleFactor = Math.Min(args.NewSize.Width / _currentPhoto.Bitmap.Bounds.Width,
            args.NewSize.Height / _currentPhoto.Bitmap.Bounds.Height);
        _imageRect = new Rect(0, 0, _currentPhoto.Bitmap.Bounds.Width * scaleFactor,
            _currentPhoto.Bitmap.Bounds.Height * scaleFactor);
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

    internal void RotateCurrentPhotoBy90()
    {
        _currentPhoto.Rotation += 90;
        UpdateTransform();
        _d2dCanvas.Invalidate();
    }
}

public class ZoomAnimationInfo(float animScale, double animXPos, double animYPos, float incrementalScale)
{
    public float Scale = animScale;
    public double X = animXPos;
    public double Y = animYPos;
    public float IncrementalScale = incrementalScale;
}