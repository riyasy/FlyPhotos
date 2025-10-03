using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Size = FlyPhotos.Data.Size;

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

    public void SetScaleAndPosition(Size imageSize, int imageRotation, Size canvasSize, bool isFirstPhotoEver)
    {
        // Use the reusable helper to calculate the scale factor.
        var scaleFactor = CalculateScreenFitScale(canvasSize, imageSize, imageRotation);

        if (scaleFactor > 1.0f) scaleFactor = 1.0f;

        // Note: The _imageRect should always be based on the un-rotated dimensions.
        // The rotation is applied later in the transform matrix.
        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
        _canvasViewState.Rotation = imageRotation;
        _canvasViewState.ImagePos.X = canvasSize.Width / 2;
        _canvasViewState.ImagePos.Y = canvasSize.Height / 2;

        _canvasViewState.LastScaleTo = (float)scaleFactor;

        if (isFirstPhotoEver && AppConfig.Settings.OpenExitZoom)
        {
            var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
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

    public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        var center = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
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

    public void ZoomAtPointPrecision(int delta, Point mousePosition)
    {
        if (delta == 0) return;

        // Base scale for one "full" mouse wheel step
        const float baseZoomIn = 1.25f;
        const float minScale = 0.05f;

        // Compute scale factor proportional to delta
        float scaleFactor = (float)Math.Pow(baseZoomIn, delta / 120.0);
        float newScale = _canvasViewState.LastScaleTo * scaleFactor;

        if (newScale < minScale) return;

        // 1. Capture the scale *before* it's changed.
        float oldScale = _canvasViewState.Scale;
        // 2. Calculate the new position based on the ratio of the new scale to the old scale.
        // This is the core formula for zooming at a point.
        var newPosX = mousePosition.X - (newScale / oldScale) * (mousePosition.X - _canvasViewState.ImagePos.X);
        var newPosY = mousePosition.Y - (newScale / oldScale) * (mousePosition.Y - _canvasViewState.ImagePos.Y);
        // 3. Now, update the state with the new values.
        _canvasViewState.Scale = newScale;
        _canvasViewState.LastScaleTo = newScale; // Keep LastScaleTo in sync
        _canvasViewState.ImagePos = new Point(newPosX, newPosY);
        // 4. Update transform and notify for redraw.
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
        _callbackZoomUpdate();
    }

    /// <summary>
    /// Zooms in or out to predefined steps: Screen Fit, 100%, and 400%.
    /// When zooming in, it moves to the next highest step.
    /// When zooming out, it moves to the next lowest step.
    /// The zoom is animated and centered on the canvas.
    /// </summary>
    public void StepZoom(ZoomDirection zoomDirection, Size canvasSize)
    {
        // 1. Establish the ordered list of zoom stops, including the dynamic "screen fit" size.
        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        var zoomStops = new List<float> { screenFitScale, 1.0f, 4.0f }
            .Distinct().OrderBy(s => s).ToList();

        // 2. Find the index of the next logical stop based on the current scale and direction.
        const float tolerance = 0.001f;
        var currentScale = _canvasViewState.LastScaleTo;

        int nextStopIndex = zoomDirection == ZoomDirection.In
            ? zoomStops.FindIndex(stop => stop > currentScale + tolerance)
            : zoomStops.FindLastIndex(stop => stop < currentScale - tolerance);

        // 3. If a valid next stop doesn't exist (i.e., we're at the end), do nothing.
        if (nextStopIndex == -1) return;

        // 4. Animate the zoom and pan to the center for the new scale.
        var targetScale = zoomStops[nextStopIndex];
        _canvasViewState.LastScaleTo = targetScale;
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        StartPanAndZoomAnimation(targetScale, targetPosition);
    }

    public void ZoomOutOnExit(double exitAnimationDuration, Size canvasSize)
    {
        _panZoomAnimationDurationMs = exitAnimationDuration;
        var targetPosition = new Point(canvasSize.Width/ 2, canvasSize.Height / 2);
        _suppressZoomUpdateForNextAnimation = true;
        StartPanAndZoomAnimation(0.001f, targetPosition);
    }

    public void ZoomPanToFit(bool animateChange, Size imageSize, Size canvasSize)
    {
        var vertical = (_canvasViewState.Rotation % 180) != 0;
        var effectiveWidth = vertical ? imageSize.Height : imageSize.Width;
        var effectiveHeight = vertical ? imageSize.Width : imageSize.Height;
        var paddedCanvasWidth = canvasSize.Width * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var paddedCanvasHeight = canvasSize.Height * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var horScale = paddedCanvasWidth / effectiveWidth;
        var vertScale = paddedCanvasHeight / effectiveHeight;
        var scaleFactor = Math.Min(horScale, vertScale);
        //if (scaleFactor > 1.0) scaleFactor = 1.0;

        if (!animateChange)
        {
            _canvasViewState.Scale = (float)scaleFactor;
            _canvasViewState.LastScaleTo = (float)scaleFactor;
            _canvasViewState.ImagePos.X = canvasSize.Width / 2;
            _canvasViewState.ImagePos.Y = canvasSize.Height / 2;
            return;
        }

        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = (float)scaleFactor;
        StartPanAndZoomAnimation((float)scaleFactor, targetPosition);
    }

    public void ZoomToHundred(Size canvasSize)
    {
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
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

    /// <summary>
    /// Calculates the scale factor required to fit an image fully within the canvas.
    /// This calculation considers three key factors:
    /// 1.  The current rotation of the image (e.g., a tall image rotated 90 degrees becomes wide).
    /// 2.  The dimensions of the canvas area available for display.
    /// 3.  A user-configurable padding percentage (`ImageFitPercentage`) that reduces the effective canvas area.
    /// The function determines whether to fit the image based on its width or height, choosing the
    /// scale that ensures the entire image remains visible.
    /// </summary>
    private float CalculateScreenFitScale(Size canvasSize, Size imageSize, int imageRotation)
    {
        // Determine if the image is rotated to a vertical orientation (e.g., 90 or 270 degrees).
        var isVertical = (imageRotation % 180) != 0;

        // Calculate the "effective" dimensions of the image after rotation.
        // If vertical, the original width becomes the new height, and vice-versa.
        var effectiveWidth = isVertical ? imageSize.Height : imageSize.Width;
        var effectiveHeight = isVertical ? imageSize.Width : imageSize.Height;

        // Calculate the usable canvas dimensions, applying the padding from user settings.
        var paddedCanvasWidth = canvasSize.Width * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var paddedCanvasHeight = canvasSize.Height * (AppConfig.Settings.ImageFitPercentage / 100.0f);

        // Determine the scale factor needed to fit the image horizontally and vertically.
        var horizontalScale = paddedCanvasWidth / effectiveWidth;
        var verticalScale = paddedCanvasHeight / effectiveHeight;

        // Return the smaller of the two scale factors. This ensures that the image fits
        // completely on the canvas without being cropped on either axis.
        return (float)Math.Min(horizontalScale, verticalScale);
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
}