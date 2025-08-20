using FlyPhotos.AppSettings;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Foundation;

namespace FlyPhotos.Controllers;

internal class CanvasViewManager
{
    public EventHandler<object> RenderingHandler;
    public DateTime PanZoomAnimationStartTime;
    public double PanZoomAnimationDurationMs = CanvasController.PanZoomAnimationDurationNormal;
    public bool PanZoomAnimationOnGoing;
    public float ZoomStartScale;
    public float ZoomTargetScale;
    public Point ZoomCenter;
    public Point PanStartPosition;
    public Point PanTargetPosition;

    private readonly CanvasViewState _canvasViewState;
    private readonly Action _invalidateCanvas; // A callback to trigger a redraw

    public CanvasViewManager(CanvasViewState canvasViewState, Action invalidateCanvas)
    {
        _canvasViewState = canvasViewState;
        _invalidateCanvas = invalidateCanvas;
    }

    public void SetScaleAndPosition(double imageWidth, double imageHeight, int imageRotation,
        double canvasWidth, double canvasHeight, bool isFirstPhoto)
    {
        var vertical = imageRotation is 270 or 90;

        // Use rotated dimensions for scaling calculation
        var effectiveWidth = vertical ? imageHeight : imageWidth;
        var effectiveHeight = vertical ? imageWidth : imageHeight;

        var horScale = canvasWidth / effectiveWidth;
        var vertScale = canvasHeight / effectiveHeight;
        var scaleFactor = Math.Min(horScale, vertScale);

        // Note: The _imageRect should always be based on the un-rotated dimensions.
        // The rotation is applied later in the transform matrix.
        _canvasViewState.ImageRect = new Rect(0, 0, imageWidth * scaleFactor, imageHeight * scaleFactor);

        _canvasViewState.Rotation = imageRotation;

        if (isFirstPhoto)
        {
            _canvasViewState.ImagePos.X = canvasWidth / 2;
            _canvasViewState.ImagePos.Y = canvasHeight / 2;

            if (AppConfig.Settings.OpenExitZoom)
            {
                var targetPosition = new Point(canvasWidth / 2, canvasHeight / 2);
                _canvasViewState.Scale = 0.01f;
                StartPanAndZoomAnimation(1.0f, targetPosition);
            }
        }

        _canvasViewState.UpdateTransform();
    }

    public void HandleSizeChange(Rect imageBounds, Size newSize, Size previousSize)
    {
        var scaleFactor = Math.Min(newSize.Width / imageBounds.Width,
            newSize.Height / imageBounds.Height);
        _canvasViewState.ImageRect = new Rect(0, 0, imageBounds.Width * scaleFactor,
            imageBounds.Height * scaleFactor);
        var xChangeRatio = newSize.Width / previousSize.Width;
        var yChangeRatio = newSize.Height / previousSize.Height;
        _canvasViewState.ImagePos.X *= xChangeRatio;
        _canvasViewState.ImagePos.Y *= yChangeRatio;
        _canvasViewState.UpdateTransform();
    }

    public void ZoomAtCenter(bool zoomingIn, double canvasWidth, double canvasHeight)
    {
        var scalePercentage = zoomingIn ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        var center = new Point(canvasWidth / 2, canvasHeight / 2);
        StartZoomAnimation(scaleTo, center);
    }

    public void ZoomAtPoint(bool zoomingIn, Point mousePosition, int deviceMaximumBitmapSizeInPixels)
    {
        var scalePercentage = zoomingIn ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        // Lower limit of zoom
        if (scaleTo < 0.05) return;
        var newImageWidth = (float)_canvasViewState.ImageRect.Width * scaleTo;
        var newImageHeight = (float)_canvasViewState.ImageRect.Height * scaleTo;

        // Upper limit of zoom
        if (newImageWidth > deviceMaximumBitmapSizeInPixels ||
            newImageHeight > deviceMaximumBitmapSizeInPixels)
        {
            return;
        }
        _canvasViewState.LastScaleTo = scaleTo;
        StartZoomAnimation(scaleTo, mousePosition);
    }

    public void ZoomOutOnExit(double exitAnimationDuration, double d2dCanvasActualWidth, double d2dCanvasActualHeight)
    {
        PanZoomAnimationDurationMs = exitAnimationDuration;
        var targetPosition = new Point(d2dCanvasActualWidth / 2, d2dCanvasActualHeight / 2);
        StartPanAndZoomAnimation(0.001f, targetPosition);
    }

    public void ZoomPanToFit(bool animateChange, double d2dCanvasActualWidth, double d2dCanvasActualHeight)
    {
        if (!animateChange)
        {
            // If not redrawing, set instantly as before
            _canvasViewState.Scale = 1f;
            _canvasViewState.LastScaleTo = 1f;
            _canvasViewState.ImagePos.X = d2dCanvasActualWidth / 2;
            _canvasViewState.ImagePos.Y = d2dCanvasActualHeight / 2;
            return;
        }

        // Define the target state
        const float targetScale = 1.0f;
        var targetPosition = new Point(d2dCanvasActualWidth / 2, d2dCanvasActualHeight / 2);

        // This is important for subsequent mouse-wheel zooms to work correctly
        _canvasViewState.LastScaleTo = targetScale;

        // Start the new pan-and-zoom animation
        StartPanAndZoomAnimation(targetScale, targetPosition);
    }

    public void Pan(double dx, double dy)
    {
        _canvasViewState.ImagePos.X += dx;
        _canvasViewState.ImagePos.Y += dy;
        _canvasViewState.UpdateTransform();
        _invalidateCanvas();
    }

    public void RotateBy(int rotation)
    {
        _canvasViewState.Rotation += rotation;
        _canvasViewState.UpdateTransform();
        _invalidateCanvas();
    }


    private void StartZoomAnimation(float targetScale, Point zoomCenter)
    {
        PanZoomAnimationStartTime = DateTime.UtcNow;
        ZoomStartScale = _canvasViewState.Scale;
        ZoomTargetScale = targetScale;
        ZoomCenter = zoomCenter;

        if (RenderingHandler != null)
            CompositionTarget.Rendering -= RenderingHandler;

        RenderingHandler = (_, _) => AnimateZoom();
        CompositionTarget.Rendering += RenderingHandler;
        PanZoomAnimationOnGoing = true;
    }

    private void StartPanAndZoomAnimation(float targetScale, Point targetPosition)
    {
        // Setup animation state
        PanZoomAnimationStartTime = DateTime.UtcNow;
        ZoomStartScale = _canvasViewState.Scale;
        ZoomTargetScale = targetScale;
        PanStartPosition = _canvasViewState.ImagePos;
        PanTargetPosition = targetPosition;

        // Ensure any previous animation is stopped
        if (RenderingHandler != null)
            CompositionTarget.Rendering -= RenderingHandler;

        // Point the handler to the NEW animation method
        RenderingHandler = (_, _) => AnimatePanAndZoom();
        CompositionTarget.Rendering += RenderingHandler;
        PanZoomAnimationOnGoing = true;
    }

    private void AnimateZoom()
    {
        var elapsed = (DateTime.UtcNow - PanZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / PanZoomAnimationDurationMs, 0.0, 1.0);

        // Ease-out cubic: f(t) = 1 - (1 - t)^3
        float easedT = 1f - (float)Math.Pow(1 - t, 3);

        var newScale = ZoomStartScale + (ZoomTargetScale - ZoomStartScale) * easedT;

        // Maintain zoom center relative to the mouse
        _canvasViewState.ImagePos.X = ZoomCenter.X - (newScale / _canvasViewState.Scale) * (ZoomCenter.X - _canvasViewState.ImagePos.X);
        _canvasViewState.ImagePos.Y = ZoomCenter.Y - (newScale / _canvasViewState.Scale) * (ZoomCenter.Y - _canvasViewState.ImagePos.Y);

        _canvasViewState.Scale = newScale;
        _canvasViewState.UpdateTransform();
        _invalidateCanvas();

        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= RenderingHandler;
            PanZoomAnimationOnGoing = false;
        }
    }

    private void AnimatePanAndZoom()
    {
        var elapsed = (DateTime.UtcNow - PanZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / PanZoomAnimationDurationMs, 0.0, 1.0);

        // Ease-out cubic: f(t) = 1 - (1 - t)^3
        float easedT = 1f - (float)Math.Pow(1 - t, 3);

        // Interpolate scale
        _canvasViewState.Scale = ZoomStartScale + (ZoomTargetScale - ZoomStartScale) * easedT;

        // Interpolate position (a simple linear interpolation)
        var newX = PanStartPosition.X + (PanTargetPosition.X - PanStartPosition.X) * easedT;
        var newY = PanStartPosition.Y + (PanTargetPosition.Y - PanStartPosition.Y) * easedT;
        _canvasViewState.ImagePos = new Point(newX, newY);

        _canvasViewState.UpdateTransform();
        _invalidateCanvas();

        if (t >= 1.0)
        {
            // Animation finished, stop the handler
            CompositionTarget.Rendering -= RenderingHandler;
            PanZoomAnimationOnGoing = false;
        }
    }

    public void Dispose()
    {
        if (RenderingHandler == null) return;
        CompositionTarget.Rendering -= RenderingHandler;
        RenderingHandler = null;
        PanZoomAnimationOnGoing = false;
    }
}