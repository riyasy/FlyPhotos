﻿using FlyPhotos.AppSettings;
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
    public event Action<bool> FitToScreenStateChanged;

    private EventHandler<object> _renderingHandler;
    private DateTime _panZoomAnimationStartTime;
    private double _panZoomAnimationDurationMs = Constants.PanZoomAnimationDurationNormal;

    private float _zoomStartScale;
    private float _zoomTargetScale;
    private Point _zoomCenter;
    private Point _panStartPosition;
    private Point _panTargetPosition;

    private bool _isFittedToScreen;
    private bool IsFittedToScreen
    {
        get => _isFittedToScreen;
        set
        {
            if (_isFittedToScreen == value) return;
            _isFittedToScreen = value;
            FitToScreenStateChanged?.Invoke(_isFittedToScreen);
        }
    }

    // A callback to trigger a redraw
    private bool _suppressZoomUpdateForNextAnimation;
    private readonly Action _callbackZoomUpdate = callbackZoomUpdate;
    private readonly Action _callbackCanvasRedraw = callbackCanvasRedraw;
    private readonly CanvasViewState _canvasViewState = canvasViewState;

    public void SetScaleAndPosition(Size imageSize, int imageRotation, Size canvasSize, bool isFirstPhotoEver)
    {
        // 1. Calculate the true "fit-to-screen" scale, which may be > 1.0 for small images.
        var fitScale = CalculateScreenFitScale(canvasSize, imageSize, imageRotation);

        // 2. Determine the initial display scale. By default, we don't scale past 100% (1.0f).
        var initialScale = Math.Min(fitScale, 1.0f);

        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
        _canvasViewState.Rotation = imageRotation;
        _canvasViewState.ImagePos.X = canvasSize.Width / 2;
        _canvasViewState.ImagePos.Y = canvasSize.Height / 2;

        _canvasViewState.LastScaleTo = initialScale;

        if (isFirstPhotoEver && AppConfig.Settings.OpenExitZoom)
        {
            var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            _canvasViewState.Scale = 0.01f;
            _suppressZoomUpdateForNextAnimation = true;
            StartPanAndZoomAnimation(initialScale, targetPosition);
        }
        else
        {
            _canvasViewState.Scale = initialScale;
        }

        _canvasViewState.UpdateTransform();

        // 3. The view is only "fitted" if the initial scale we are using IS the calculated fit scale.
        // For small images, initialScale will be 1.0f while fitScale is > 1.0f, so this will be false. Correct.
        // For large images, initialScale and fitScale will both be < 1.0f and equal, so this will be true. Correct.
        IsFittedToScreen = Math.Abs(initialScale - fitScale) < 0.001f;
    }

    public void UpdateImageMetrics(Size imageSize, Size canvasSize)
    {
        var oldImageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);

        if (IsFittedToScreen)
        {
            // The view was fitted. To maintain this state, we must re-calculate the fit for the new image dimensions.
            // This is crucial for HQ upgrades where the preview's aspect ratio might differ slightly.
            var newScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            _callbackZoomUpdate(); // The zoom percentage might have changed.
        }
        else // The view was not fitted (user has custom pan/zoom).
        {
            // Preserve the custom view by adjusting the pan position proportionally to the change in image size.
            if (AppConfig.Settings.PreserveZoomAndPan && oldImageSize.Width > 0 && oldImageSize.Height > 0 && oldImageSize != imageSize)
            {
                var panOffsetX = _canvasViewState.ImagePos.X - canvasSize.Width / 2;
                var panOffsetY = _canvasViewState.ImagePos.Y - canvasSize.Height / 2;

                var widthRatio = imageSize.Width / oldImageSize.Width;
                var heightRatio = imageSize.Height / oldImageSize.Height;

                _canvasViewState.ImagePos.X = (panOffsetX * widthRatio) + canvasSize.Width / 2;
                _canvasViewState.ImagePos.Y = (panOffsetY * heightRatio) + canvasSize.Height / 2;
            }
        }

        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
    }

    public void HandleSizeChange(Size newSize, Size previousSize)
    {
        if (IsFittedToScreen)
        {
            var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
            var newScale = CalculateScreenFitScale(newSize, imageSize, _canvasViewState.Rotation);

            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = new Point(newSize.Width / 2, newSize.Height / 2);

            _canvasViewState.UpdateTransform();
            _callbackCanvasRedraw();
            _callbackZoomUpdate();
        }
        else
        {
            var xChangeRatio = newSize.Width / previousSize.Width;
            var yChangeRatio = newSize.Height / previousSize.Height;
            _canvasViewState.ImagePos.X *= xChangeRatio;
            _canvasViewState.ImagePos.Y *= yChangeRatio;
            _canvasViewState.UpdateTransform();
            _callbackCanvasRedraw();
        }
    }

    public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        var center = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        StartZoomAnimation(scaleTo, center);
        IsFittedToScreen = false;
    }

    public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        StartZoomAnimation(scaleTo, zoomAnchor);
        IsFittedToScreen = false;
    }

    public void ZoomAtPointPrecision(int delta, Point zoomAnchor)
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
        var newPosX = zoomAnchor.X - (newScale / oldScale) * (zoomAnchor.X - _canvasViewState.ImagePos.X);
        var newPosY = zoomAnchor.Y - (newScale / oldScale) * (zoomAnchor.Y - _canvasViewState.ImagePos.Y);
        // 3. Now, update the state with the new values.
        _canvasViewState.Scale = newScale;
        _canvasViewState.LastScaleTo = newScale; // Keep LastScaleTo in sync
        _canvasViewState.ImagePos = new Point(newPosX, newPosY);
        // 4. Update transform and notify for redraw.
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
        _callbackZoomUpdate();
        IsFittedToScreen = false;
    }

    /// <summary>
    /// Zooms in or out to predefined steps: Screen Fit, 100%, and 400%.
    /// When zooming in, it moves to the next highest step.
    /// When zooming out, it moves to the next lowest step.
    /// The zoom is animated and centered on the canvas.
    /// </summary>
    public void StepZoom(ZoomDirection zoomDirection, Size canvasSize, Point? zoomAnchor = null)
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

        var anchor = zoomAnchor ?? new Point(canvasSize.Width / 2, canvasSize.Height / 2);

        if (zoomAnchor.HasValue)
            StartZoomAnimation(targetScale, anchor);
        else
            StartPanAndZoomAnimation(targetScale, anchor);

        IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < 0.001f;
    }

    public void ZoomOutOnExit(double exitAnimationDuration, Size canvasSize)
    {
        _panZoomAnimationDurationMs = exitAnimationDuration;
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _suppressZoomUpdateForNextAnimation = true;
        StartPanAndZoomAnimation(0.001f, targetPosition);
        IsFittedToScreen = false;
    }

    public void ZoomPanToFit(bool animateChange, Size imageSize, Size canvasSize)
    {
        // This method is for the EXPLICIT user action, so it DOES upscale small images.
        // It does not use the Math.Min(..., 1.0f) logic.
        var scaleFactor = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);

        if (!animateChange)
        {
            _canvasViewState.Scale = (float)scaleFactor;
            _canvasViewState.LastScaleTo = (float)scaleFactor;
            _canvasViewState.ImagePos.X = canvasSize.Width / 2;
            _canvasViewState.ImagePos.Y = canvasSize.Height / 2;
            _canvasViewState.UpdateTransform();
            _callbackCanvasRedraw();
            _callbackZoomUpdate();
            IsFittedToScreen = true;
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
        IsFittedToScreen = false;
    }


    public void Pan(double dx, double dy)
    {
        _canvasViewState.ImagePos.X += dx;
        _canvasViewState.ImagePos.Y += dy;
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
        IsFittedToScreen = false;
    }

    public void RotateBy(int rotation)
    {
        _canvasViewState.Rotation += rotation;
        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
        IsFittedToScreen = false;
    }

    /// <summary>
    /// Triggers a "shrug" or "shake" animation on the photo.
    /// This is used to provide visual feedback that an operation was not accepted.
    /// The animation will not play if another animation is already in progress.
    /// </summary>
    public void Shrug()
    {
        // Don't start a shrug if another animation is already running.
        if (PanZoomAnimationOnGoing)
        {
            return;
        }
        StartShrugAnimation();
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
    private static float CalculateScreenFitScale(Size canvasSize, Size imageSize, int imageRotation)
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

    private void StartZoomAnimation(float targetScale, Point zoomAnchor)
    {
        _panZoomAnimationStartTime = DateTime.UtcNow;
        _zoomStartScale = _canvasViewState.Scale;
        _zoomTargetScale = targetScale;
        _zoomCenter = zoomAnchor;

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

    // A private method to set up and start the shrug animation.
    private void StartShrugAnimation()
    {
        // Store the original position to return to
        _panStartPosition = _canvasViewState.ImagePos;
        _panZoomAnimationStartTime = DateTime.UtcNow;

        if (_renderingHandler != null)
        {
            CompositionTarget.Rendering -= _renderingHandler;
        }

        _renderingHandler = (_, _) => AnimateShrug();
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

            var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
            var canvasSize = new Size(_panTargetPosition.X * 2, _panTargetPosition.Y * 2);
            if (canvasSize.Width > 0 && canvasSize.Height > 0)
            {
                var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
                if (Math.Abs(_zoomTargetScale - screenFitScale) < 0.001)
                {
                    IsFittedToScreen = true;
                }
            }
        }
    }

    private void AnimateShrug()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / Constants.ShrugAnimationDurationMs, 0.0, 1.0);

        if (t >= 1.0)
        {
            // Animation finished. Ensure the image is back to its exact starting position.
            _canvasViewState.ImagePos = _panStartPosition;
            _canvasViewState.UpdateTransform();
            _callbackCanvasRedraw();

            CompositionTarget.Rendering -= _renderingHandler;
            PanZoomAnimationOnGoing = false;
            return;
        }

        // --- The Shrug Logic ---
        // 1. Damping factor: (1 - t) makes the shake fade out over time.
        var damping = 1 - t;
        // 2. Sine wave: Creates the oscillating (back and forth) motion.
        var wave = Math.Sin(t * Constants.ShrugFrequency * 2 * Math.PI);
        // 3. Combine them to get the final offset from the original position.
        var xOffset = Constants.ShrugAmplitude * wave * damping;

        // Apply the offset to the original X position. Y position remains unchanged.
        _canvasViewState.ImagePos.X = _panStartPosition.X + xOffset;

        _canvasViewState.UpdateTransform();
        _callbackCanvasRedraw();
    }


    public void Dispose()
    {
        if (_renderingHandler == null) return;
        CompositionTarget.Rendering -= _renderingHandler;
        _renderingHandler = null;
        PanZoomAnimationOnGoing = false;
    }
}