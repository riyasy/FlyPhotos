using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Size = FlyPhotos.Data.Size;

namespace FlyPhotos.Controllers;

/// <summary>
/// Manages the view state (pan, zoom, rotation) of the canvas.
/// Handles user interactions, animations, and state transitions.
/// </summary>
internal class CanvasViewManager(CanvasViewState canvasViewState)
{
    // --- Public Properties & Events ---

    /// <summary>
    /// Indicates if a pan or zoom animation is currently in progress.
    /// </summary>
    public bool PanZoomAnimationOnGoing { get; private set; }

    /// <summary>
    /// Fires when the view's "fitted to screen" state changes.
    /// </summary>
    public event Action<bool> FitToScreenStateChanged;

    /// <summary>
    /// Fires when the view's "100% zoom" state changes.
    /// </summary>
    public event Action<bool> OneToOneStateChanged;

    /// <summary>
    /// Fires when the zoom value changes.
    /// </summary>
    public event Action ZoomChanged;

    /// <summary>
    /// Fires when there is a need to redraw the canvas.
    /// </summary>
    public event Action ViewChanged;

    // --- Animation & State Fields ---

    private EventHandler<object> _renderingHandler;
    private DateTime _panZoomAnimationStartTime;
    private double _panZoomAnimationDurationMs = Constants.PanZoomAnimationDurationNormal;

    private float _zoomStartScale;
    private float _zoomTargetScale;
    private Point _zoomCenter;
    private Point _panStartPosition;
    private Point _panTargetPosition;

    // --- State Management Properties ---

    /// <summary>
    /// Volatile cache to store view state per photo for the current session.
    /// Key: Photo file path. Value: A clone of the photo's CanvasViewState.
    /// </summary>
    private readonly Dictionary<string, CanvasViewState> _perPhotoStateCache = new();

    /// <summary>
    /// Tracks if the image is perfectly fitted to the canvas (respecting padding).
    /// Setting this property will fire the FitToScreenStateChanged event if the value changes.
    /// </summary>
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

    /// <summary>
    /// Tracks if the image is at exactly 100% scale.
    /// Setting this property will fire the OneToOneStateChanged event if the value changes.
    /// </summary>
    private bool _isAtOneToOne;
    private bool IsAtOneToOne
    {
        get => _isAtOneToOne;
        set
        {
            if (_isAtOneToOne == value) return;
            _isAtOneToOne = value;
            OneToOneStateChanged?.Invoke(_isAtOneToOne);
        }
    }


    private bool _suppressZoomUpdateForNextAnimation;

    private readonly CanvasViewState _canvasViewState = canvasViewState;

    // --- Public API Methods ---

    /// <summary>
    /// Saves the current view state for a given photo path if the user setting is enabled.
    /// </summary>
    public void CacheCurrentViewState(string photoPath)
    {
        if (AppConfig.Settings.PanZoomBehaviourOnNavigation == PanZoomBehaviourOnNavigation.RememberPerPhoto)
        {
            // Store a clone of the state, not a reference, to prevent it from being modified.
            _perPhotoStateCache[photoPath] = _canvasViewState.Clone();
        }
    }

    /// <summary>
    /// Sets the initial scale and position for a new image.
    /// </summary>
    public void SetScaleAndPosition(string photoPath, Size imageSize, int imageRotation, Size canvasSize,
        bool isFirstPhotoEver, bool isNewPhoto, bool isUpgradeFromPlaceholder)
    {
        var panZoomBehaviourOnNavigation = AppConfig.Settings.PanZoomBehaviourOnNavigation;

        if (isFirstPhotoEver)
            panZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.Reset;
        else if (!isNewPhoto && !isUpgradeFromPlaceholder)
            panZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RetainFromLastPhoto;

        switch (panZoomBehaviourOnNavigation)
        {
            case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                _canvasViewState.Rotation = imageRotation;

                if (_perPhotoStateCache.TryGetValue(photoPath, out var cachedState))
                    SetCachedView(imageSize, imageRotation, canvasSize, cachedState);
                else
                    SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver);

                break;

            case PanZoomBehaviourOnNavigation.Reset:
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                _canvasViewState.Rotation = imageRotation;
                SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver);
                break;

            case PanZoomBehaviourOnNavigation.RetainFromLastPhoto:
                var oldImageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                if (isNewPhoto)
                    _canvasViewState.Rotation = imageRotation;

                SetViewFromPrevious(oldImageSize, imageSize, canvasSize);
                break;
                
        }
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Sets the default view for a photo by fitting it to screen or 100% for smaller photos.
    /// </summary>
    private void SetDefaultView(Size imageSize, int imageRotation, Size canvasSize, bool isFirstPhotoEver)
    {
        var defaultFitScale = CalculateScreenFitScale(canvasSize, imageSize, imageRotation);
        var initialScale = Math.Min(defaultFitScale, 1.0f);

        _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = initialScale;

        if (isFirstPhotoEver && AppConfig.Settings.OpenExitZoom)
        {
            _canvasViewState.Scale = 0.01f;
            _suppressZoomUpdateForNextAnimation = true;
            StartPanAndZoomAnimation(initialScale, new Point(canvasSize.Width / 2, canvasSize.Height / 2));
        }
        else
        {
            _canvasViewState.Scale = initialScale;
        }
        _canvasViewState.UpdateTransform();
        IsFittedToScreen = Math.Abs(initialScale - defaultFitScale) < 0.001f;
        IsAtOneToOne = Math.Abs(initialScale - 1.0f) < 0.001f;
    }

    /// <summary>
    /// Sets the view for a photo by retrieving its cached state if it was panned or zoomed earlier in the current session.
    /// </summary>
    private void SetCachedView(Size imageSize, int imageRotation, Size canvasSize, CanvasViewState cachedState)
    {
        _canvasViewState.Apply(cachedState);
        // After applying, recalculate if the restored state is fitted or 1:1.
        var fitScale = CalculateScreenFitScale(canvasSize, imageSize, imageRotation);
        IsFittedToScreen = Math.Abs(_canvasViewState.Scale - fitScale) < 0.001f;
        IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < 0.001f;
        _canvasViewState.UpdateTransform();
    }

    /// <summary>
    /// Sets the view for the current photo by reusing pan and zoom from previous photo.
    /// </summary>
    private void SetViewFromPrevious(Size oldImageSize, Size imageSize, Size canvasSize)
    {
        if (IsFittedToScreen)
        {
            var newScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            ZoomChanged?.Invoke();
            IsAtOneToOne = Math.Abs(newScale - 1.0f) < 0.001f;
        }
        else if (oldImageSize.Width > 0 && oldImageSize.Height > 0 && oldImageSize != imageSize)
        {
            var panOffsetX = _canvasViewState.ImagePos.X - canvasSize.Width / 2;
            var panOffsetY = _canvasViewState.ImagePos.Y - canvasSize.Height / 2;
            var widthRatio = imageSize.Width / oldImageSize.Width;
            var heightRatio = imageSize.Height / oldImageSize.Height;
            _canvasViewState.ImagePos.X = (panOffsetX * widthRatio) + canvasSize.Width / 2;
            _canvasViewState.ImagePos.Y = (panOffsetY * heightRatio) + canvasSize.Height / 2;
        }
        _canvasViewState.UpdateTransform();
    }

    /// <summary>
    /// Responds to window size changes, keeping the image view consistent.
    /// If fitted, it re-fits. Otherwise, it adjusts the pan position proportionally.
    /// </summary>
    public void HandleSizeChange(Size newSize, Size previousSize)
    {
        if (IsFittedToScreen)
        {
            // If the image is fitted, recalculate the fit for the new window size.
            var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
            var newScale = CalculateScreenFitScale(newSize, imageSize, _canvasViewState.Rotation);

            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = new Point(newSize.Width / 2, newSize.Height / 2);

            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();
            ZoomChanged?.Invoke();
            IsAtOneToOne = Math.Abs(newScale - 1.0f) < 0.001f;
        }
        else
        {
            // If custom pan/zoom, adjust the image position to maintain its relative screen location.
            var xChangeRatio = newSize.Width / previousSize.Width;
            var yChangeRatio = newSize.Height / previousSize.Height;
            _canvasViewState.ImagePos.X *= xChangeRatio;
            _canvasViewState.ImagePos.Y *= yChangeRatio;
            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();
        }
    }

    /// <summary>
    /// Performs a standard zoom operation centered on the canvas.
    /// </summary>
    public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        var center = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        StartZoomAnimation(scaleTo, center);

        // Any manual zoom action invalidates both fitted and 1:1 states.
        IsFittedToScreen = false;
        IsAtOneToOne = false;
    }

    /// <summary>
    /// Performs a standard zoom operation anchored at a specific point (e.g., mouse cursor).
    /// </summary>
    public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var scaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        if (scaleTo < 0.05) return;
        _canvasViewState.LastScaleTo = scaleTo;
        StartZoomAnimation(scaleTo, zoomAnchor);

        // Any manual zoom action invalidates both fitted and 1:1 states.
        IsFittedToScreen = false;
        IsAtOneToOne = false;
    }

    /// <summary>
    /// Performs a precision zoom (e.g., from a touchpad) anchored at a specific point.
    /// </summary>
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
        ViewChanged?.Invoke();
        ZoomChanged?.Invoke();

        // Any manual zoom action invalidates both fitted and 1:1 states.
        IsFittedToScreen = false;
        IsAtOneToOne = false;
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

        // A pure zoom doesn't change pan; a pan/zoom centers the image.
        if (zoomAnchor.HasValue)
            StartZoomAnimation(targetScale, anchor);
        else
            StartPanAndZoomAnimation(targetScale, anchor);

        // Set state immediately for responsive UI.
        IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < 0.001f;
        IsAtOneToOne = Math.Abs(targetScale - 1.0f) < 0.001f;
    }

    /// <summary>
    /// Performs the zoom-out animation when closing the application.
    /// </summary>
    public void ZoomOutOnExit(double exitAnimationDuration, Size canvasSize)
    {
        _panZoomAnimationDurationMs = exitAnimationDuration;
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _suppressZoomUpdateForNextAnimation = true;
        StartPanAndZoomAnimation(0.001f, targetPosition);
        IsFittedToScreen = false;
        IsAtOneToOne = false;
    }

    /// <summary>
    /// Explicit user action to fit the image to the screen. Allows upscaling of small images.
    /// </summary>
    public void ZoomPanToFit(bool animateChange, Size imageSize, Size canvasSize)
    {
        // This is the EXPLICIT user action, so it DOES upscale small images.
        var scaleFactor = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);

        if (!animateChange)
        {
            _canvasViewState.Scale = scaleFactor;
            _canvasViewState.LastScaleTo = scaleFactor;
            _canvasViewState.ImagePos.X = canvasSize.Width / 2;
            _canvasViewState.ImagePos.Y = canvasSize.Height / 2;
            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();
            ZoomChanged?.Invoke();
            IsFittedToScreen = true;
            IsAtOneToOne = Math.Abs(scaleFactor - 1.0f) < 0.001f;
            return;
        }

        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = scaleFactor;
        StartPanAndZoomAnimation(scaleFactor, targetPosition);

        // Set state immediately for responsive UI, even when animating.
        IsFittedToScreen = true;
        IsAtOneToOne = Math.Abs(scaleFactor - 1.0f) < 0.001f;
    }

    /// <summary>
    /// Explicit user action to set zoom to 100%.
    /// </summary>
    public void ZoomToHundred(Size canvasSize)
    {
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = 1.0f;
        StartPanAndZoomAnimation(1.0f, targetPosition);

        // Set state immediately. The view is fitted only if 100% happens to be the fit scale.
        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(1.0f - screenFitScale) < 0.001f;
        IsAtOneToOne = true;
    }

    /// <summary>
    /// Pans the image by the specified delta.
    /// </summary>
    public void Pan(double dx, double dy)
    {
        _canvasViewState.ImagePos.X += dx;
        _canvasViewState.ImagePos.Y += dy;
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();

        // Any manual pan breaks the fitted state.
        IsFittedToScreen = false;
        // Per requirements, panning does not affect the 1:1 state.
    }

    /// <summary>
    /// Rotates the image by 90 degrees.
    /// </summary>
    public void RotateBy(int rotation)
    {
        _canvasViewState.Rotation += rotation;
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();

        // Rotation changes the effective dimensions, breaking the fitted state.
        IsFittedToScreen = false;
        // Rotation does not change scale, so 1:1 state is preserved.
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

    // --- Helper & Animation Methods ---

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

    /// <summary>
    /// Starts a pure zoom animation, keeping the anchor point stationary on screen.
    /// </summary>
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

    /// <summary>
    /// Starts an animation that interpolates both pan and zoom simultaneously.
    /// </summary>
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

    /// <summary>
    /// Starts the shrug/shake animation.
    /// </summary>
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

    /// <summary>
    /// The rendering loop callback for the pure zoom animation.
    /// </summary>
    private void AnimateZoom()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / _panZoomAnimationDurationMs, 0.0, 1.0);
        float easedT = 1f - (float)Math.Pow(1 - t, 3); // Ease-out cubic: f(t) = 1 - (1 - t)^3
        var newScale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;

        // Formula to keep the zoom anchor stationary.
        _canvasViewState.ImagePos.X = _zoomCenter.X - (newScale / _canvasViewState.Scale) * (_zoomCenter.X - _canvasViewState.ImagePos.X);
        _canvasViewState.ImagePos.Y = _zoomCenter.Y - (newScale / _canvasViewState.Scale) * (_zoomCenter.Y - _canvasViewState.ImagePos.Y);

        _canvasViewState.Scale = newScale;
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
        if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();

        // Stop the animation when finished.
        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            PanZoomAnimationOnGoing = false;
            _suppressZoomUpdateForNextAnimation = false;
        }
    }

    /// <summary>
    /// The rendering loop callback for the combined pan and zoom animation.
    /// </summary>
    private void AnimatePanAndZoom()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / _panZoomAnimationDurationMs, 0.0, 1.0);
        float easedT = 1f - (float)Math.Pow(1 - t, 3); // Ease-out cubic: f(t) = 1 - (1 - t)^3

        // Interpolate scale and position.
        _canvasViewState.Scale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;
        // Interpolate position (a simple linear interpolation)
        var newX = _panStartPosition.X + (_panTargetPosition.X - _panStartPosition.X) * easedT;
        var newY = _panStartPosition.Y + (_panTargetPosition.Y - _panStartPosition.Y) * easedT;
        _canvasViewState.ImagePos = new Point(newX, newY);

        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
        if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();

        // Stop the animation when finished.
        if (t >= 1.0)
        {
            CompositionTarget.Rendering -= _renderingHandler;
            PanZoomAnimationOnGoing = false;
            _suppressZoomUpdateForNextAnimation = false;

            // This block remains as a final "truth-setter" after animation,
            // which is harmless and good for robustness.
            var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
            var canvasSize = new Size(_panTargetPosition.X * 2, _panTargetPosition.Y * 2);
            if (canvasSize.Width > 0 && canvasSize.Height > 0)
            {
                var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
                IsFittedToScreen = Math.Abs(_zoomTargetScale - screenFitScale) < 0.001f;
                IsAtOneToOne = Math.Abs(_zoomTargetScale - 1.0f) < 0.001f;
            }
        }
    }

    /// <summary>
    /// The rendering loop callback for the shrug animation.
    /// </summary>
    private void AnimateShrug()
    {
        var elapsed = (DateTime.UtcNow - _panZoomAnimationStartTime).TotalMilliseconds;
        var t = Math.Clamp(elapsed / Constants.ShrugAnimationDurationMs, 0.0, 1.0);

        if (t >= 1.0)
        {
            // Animation finished. Ensure the image is back to its exact starting position.
            _canvasViewState.ImagePos = _panStartPosition;
            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();

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
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Cleans up resources, specifically the rendering event handler.
    /// </summary>
    public void Dispose()
    {
        if (_renderingHandler == null) return;
        CompositionTarget.Rendering -= _renderingHandler;
        _renderingHandler = null;
        PanZoomAnimationOnGoing = false;
    }
}