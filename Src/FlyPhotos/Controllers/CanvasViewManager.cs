using System;
using Windows.Foundation;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using Microsoft.UI.Xaml.Media;

namespace FlyPhotos.Controllers;

internal class CanvasViewManager(CanvasViewState canvasViewState, Action callbackCanvasRedraw, Action callbackZoomUpdate)
{
    public bool PanZoomAnimationOnGoing { get; private set; }

    private EventHandler<object> _renderingHandler;
    private DateTime _panZoomAnimationStartTime;
    private double _panZoomAnimationDurationMs = Constants.PanZoomAnimationDurationNormal;

    private float _zoomStartScale;
    private float _zoomTargetScale;
    private Point _zoomCenter;
    private Point _panStartPosition;
    private Point _panTargetPosition;

    // A callback to trigger a redraw
    private bool _suppressZoomUpdateForNextAnimation;
    private readonly Action _callbackZoomUpdate = callbackZoomUpdate;
    private readonly Action _callbackCanvasRedraw = callbackCanvasRedraw;
    private readonly CanvasViewState _canvasViewState = canvasViewState;

    public void SetScaleAndPosition(Size imageSize, int imageRotation,
        double canvasWidth, double canvasHeight, bool isFirstPhotoEver)
    {
        var vertical = imageRotation is 270 or 90;

        // Use rotated dimensions for scaling calculation
        var effectiveWidth = vertical ? imageSize.Height : imageSize.Width;
        var effectiveHeight = vertical ? imageSize.Width : imageSize.Height;

        var paddedCanvasWidth = canvasWidth * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var paddedCanvasHeight = canvasHeight * (AppConfig.Settings.ImageFitPercentage / 100.0f);

        var horScale = paddedCanvasWidth / effectiveWidth;
        var vertScale = paddedCanvasHeight / effectiveHeight;
        var scaleFactor = Math.Min(horScale, vertScale);

        if (scaleFactor > 1.0) scaleFactor = 1.0;

        // Note: The _imageRect should always be based on the un-rotated dimensions.
        // The rotation is applied later in the transform matrix.
        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
        _canvasViewState.Rotation = imageRotation;
        _canvasViewState.ImagePos.X = canvasWidth / 2;
        _canvasViewState.ImagePos.Y = canvasHeight / 2;

        _canvasViewState.LastScaleTo = (float)scaleFactor;

        if (isFirstPhotoEver && AppConfig.Settings.OpenExitZoom)
        {
            var targetPosition = new Point(canvasWidth / 2, canvasHeight / 2);
            _canvasViewState.Scale = 0.01f;
            _suppressZoomUpdateForNextAnimation = true;
            StartPanAndZoomAnimation((float)scaleFactor, targetPosition);
        }
        else
        {
            _canvasViewState.Scale = (float)scaleFactor;
        }
        _canvasViewState.UpdateTransform();
    }

    public void UpdateImageMetrics(Size imageSize)
    {
        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
    }

    public void HandleSizeChange(Size newSize, Size previousSize)
    {
        var xChangeRatio = newSize.Width / previousSize.Width;
        var yChangeRatio = newSize.Height / previousSize.Height;
        _canvasViewState.ImagePos.X *= xChangeRatio;
        _canvasViewState.ImagePos.Y *= yChangeRatio;
        _canvasViewState.UpdateTransform();
    }

    public void ZoomAtCenter(ZoomDirection zoomDirection, double canvasWidth, double canvasHeight)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        var center = new Point(canvasWidth / 2, canvasHeight / 2);
        StartZoomAnimation(scaleTo, center);
    }

    public void ZoomAtPoint(ZoomDirection zoomDirection, Point mousePosition)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        StartZoomAnimation(scaleTo, mousePosition);
    }

    public void ZoomOutOnExit(double exitAnimationDuration, double canvasWidth, double canvasHeight)
    {
        _panZoomAnimationDurationMs = exitAnimationDuration;
        var targetPosition = new Point(canvasWidth / 2, canvasHeight / 2);
        _suppressZoomUpdateForNextAnimation = true;
        StartPanAndZoomAnimation(0.001f, targetPosition);
    }

    public void ZoomPanToFit(bool animateChange, Size imageSize, double canvasWidth,
        double canvasHeight)
    {
        var vertical = (_canvasViewState.Rotation % 180) != 0;
        var effectiveWidth = vertical ? imageSize.Height : imageSize.Width;
        var effectiveHeight = vertical ? imageSize.Width : imageSize.Height;
        var paddedCanvasWidth = canvasWidth * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var paddedCanvasHeight = canvasHeight * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var horScale = paddedCanvasWidth / effectiveWidth;
        var vertScale = paddedCanvasHeight / effectiveHeight;
        var scaleFactor = Math.Min(horScale, vertScale);
        if (scaleFactor > 1.0) scaleFactor = 1.0;

        if (!animateChange)
        {
            _canvasViewState.Scale = (float)scaleFactor;
            _canvasViewState.LastScaleTo = (float)scaleFactor;
            _canvasViewState.ImagePos.X = canvasWidth / 2;
            _canvasViewState.ImagePos.Y = canvasHeight / 2;
            return;
        }

        var targetPosition = new Point(canvasWidth / 2, canvasHeight / 2);
        _canvasViewState.LastScaleTo = (float)scaleFactor;
        StartPanAndZoomAnimation((float)scaleFactor, targetPosition);
    }

    public void ZoomToHundred(double canvasWidth, double canvasHeight)
    {
        var targetPosition = new Point(canvasWidth / 2, canvasHeight / 2);
        _canvasViewState.LastScaleTo = 1.0f;
        StartPanAndZoomAnimation(1.0f, targetPosition);
    }

    public void Pan(double dx, double dy)
    {
        _canvasViewState.ImagePos.X += dx;
        _canvasViewState.ImagePos.Y += dy;
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
    }

    public void RotateBy(int rotation)
    {
        _canvasViewState.Rotation += rotation;
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
    }


    private void StartZoomAnimation(float targetScale, Point zoomCenter)
    {
        _panZoomAnimationStartTime = DateTime.UtcNow;
        _zoomStartScale = _canvasViewState.Scale;
        _zoomTargetScale = targetScale;
        _zoomCenter = zoomCenter;

        if (_renderingHandler != null)
            CompositionTarget.Rendering -= _renderingHandler;

        _renderingHandler = (_, _) => AnimateZoom();
        CompositionTarget.Rendering += _renderingHandler;
        PanZoomAnimationOnGoing = true;
    }

    private void StartPanAndZoomAnimation(float targetScale, Point targetPosition)
    {
        // Setup animation state
        _panZoomAnimationStartTime = DateTime.UtcNow;
        _zoomStartScale = _canvasViewState.Scale;
        _zoomTargetScale = targetScale;
        _panStartPosition = _canvasViewState.ImagePos;
        _panTargetPosition = targetPosition;

        // Ensure any previous animation is stopped
        if (_renderingHandler != null)
            CompositionTarget.Rendering -= _renderingHandler;

        // Point the handler to the NEW animation method
        _renderingHandler = (_, _) => AnimatePanAndZoom();
        CompositionTarget.Rendering += _renderingHandler;
        PanZoomAnimationOnGoing = true;
    }

    private void AnimateZoom()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / _panZoomAnimationDurationMs, 0.0, 1.0);

        // Ease-out cubic: f(t) = 1 - (1 - t)^3
        float easedT = 1f - (float)Math.Pow(1 - t, 3);

        var newScale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;

        // Maintain zoom center relative to the mouse
        _canvasViewState.ImagePos.X = _zoomCenter.X - (newScale / _canvasViewState.Scale) * (_zoomCenter.X - _canvasViewState.ImagePos.X);
        _canvasViewState.ImagePos.Y = _zoomCenter.Y - (newScale / _canvasViewState.Scale) * (_zoomCenter.Y - _canvasViewState.ImagePos.Y);

        _canvasViewState.Scale = newScale;
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
        if (!_suppressZoomUpdateForNextAnimation) _callbackZoomUpdate();

        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            PanZoomAnimationOnGoing = false;
            _suppressZoomUpdateForNextAnimation = false;
        }
    }

    private void AnimatePanAndZoom()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / _panZoomAnimationDurationMs, 0.0, 1.0);

        // Ease-out cubic: f(t) = 1 - (1 - t)^3
        float easedT = 1f - (float)Math.Pow(1 - t, 3);

        // Interpolate scale
        _canvasViewState.Scale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;

        // Interpolate position (a simple linear interpolation)
        var newX = _panStartPosition.X + (_panTargetPosition.X - _panStartPosition.X) * easedT;
        var newY = _panStartPosition.Y + (_panTargetPosition.Y - _panStartPosition.Y) * easedT;
        _canvasViewState.ImagePos = new Point(newX, newY);

        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
        if (!_suppressZoomUpdateForNextAnimation) _callbackZoomUpdate();

        if (t >= 1.0)
        {
            // Animation finished, stop the handler
            CompositionTarget.Rendering -= _renderingHandler;
            PanZoomAnimationOnGoing = false;
            _suppressZoomUpdateForNextAnimation = false;
        }
    }

    public void Dispose()
    {
        if (_renderingHandler == null) return;
        CompositionTarget.Rendering -= _renderingHandler;
        _renderingHandler = null;
        PanZoomAnimationOnGoing = false;
    }

    public void SetStartupScale()
    {
        _canvasViewState.Scale = 1.0f;
        _canvasViewState.LastScaleTo = 1.0f;
    }
}