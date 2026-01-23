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
    /// File path of current photo
    /// </summary>
    private string _photoPath = string.Empty;

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
    private int _originalImageRotation;

    // --- State Management Properties ---

    /// <summary>
    /// Volatile cache to store view state per photo for the current session.
    /// Key: Photo file path. Value: A clone of the photo's CanvasViewState.
    /// The ImagePos in the cached state is a *normalized* offset from the center, not absolute pixels.
    /// </summary>
    private readonly Dictionary<string, CanvasViewState> _perPhotoStateCache = new();

    /// <summary>
    /// Tracks if the view state has been modified by user input (pan, zoom, rotate).
    /// Used to determine if the state is worth caching for "RememberPerPhoto" mode.
    /// </summary>
    private bool _isStateModifiedByUser;

    /// <summary>
    /// Tracks if the image is perfectly fitted to the canvas (respecting padding).
    /// Setting this property will fire the FitToScreenStateChanged event if the value changes.
    /// </summary>
    private bool IsFittedToScreen
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            FitToScreenStateChanged?.Invoke(field);
        }
    }

    /// <summary>
    /// Tracks if the image is at exactly 100% scale.
    /// Setting this property will fire the OneToOneStateChanged event if the value changes.
    /// </summary>
    private bool IsAtOneToOne
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            OneToOneStateChanged?.Invoke(field);
        }
    }


    private bool _suppressZoomUpdateForNextAnimation;

    private readonly CanvasViewState _canvasViewState = canvasViewState;

    // --- Public API Methods ---

    /// <summary>
    /// Saves the current view state for a given photo path if the user setting is enabled
    /// and the user has actually modified the view (panned, zoomed, or rotated).
    /// The pan position is saved as a normalized value to be resilient to window resizing.
    /// </summary>
    public void CacheCurrentViewState(string photoPath, Size canvasSize)
    {
        if (AppConfig.Settings.PanZoomBehaviourOnNavigation == PanZoomBehaviourOnNavigation.RememberPerPhoto && _isStateModifiedByUser)
        {
            // Store a clone of the state, not a reference.
            var stateToCache = _canvasViewState.Clone();

            // Convert the absolute pan position to a normalized offset from the center.
            // This makes the cached pan position independent of the canvas size.
            var panOffsetX = _canvasViewState.ImagePos.X - canvasSize.Width / 2.0;
            var panOffsetY = _canvasViewState.ImagePos.Y - canvasSize.Height / 2.0;

            // Avoid division by zero if canvas is not yet sized.
            var normalizedPanX = canvasSize.Width > 0 ? panOffsetX / canvasSize.Width : 0;
            var normalizedPanY = canvasSize.Height > 0 ? panOffsetY / canvasSize.Height : 0;

            stateToCache.ImagePos = new Point(normalizedPanX, normalizedPanY);

            _perPhotoStateCache[photoPath] = stateToCache;
        }
    }

    /// <summary>
    /// Sets the initial scale and position for a new image.
    /// </summary>
    public void SetScaleAndPosition(string photoPath, Size imageSize, int imageRotation, Size canvasSize,
        bool isFirstPhotoEver, bool isNewPhoto, bool isUpgradeFromPlaceholder)
    {
        _photoPath = photoPath;

        var panZoomBehaviourOnNavigation = AppConfig.Settings.PanZoomBehaviourOnNavigation;

        if (isFirstPhotoEver)
            panZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.Reset;
        else if (!isNewPhoto && !isUpgradeFromPlaceholder)
            panZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RetainFromLastPhoto;

        switch (panZoomBehaviourOnNavigation)
        {
            case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                // For a new photo, always start with its native rotation.
                if (isNewPhoto)
                {
                    _canvasViewState.Rotation = imageRotation;
                    _originalImageRotation = imageRotation;
                }

                if (_perPhotoStateCache.TryGetValue(photoPath, out var cachedState))
                    SetCachedView(imageSize, canvasSize, cachedState);
                else
                    SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver);

                break;

            case PanZoomBehaviourOnNavigation.Reset:
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                _canvasViewState.Rotation = imageRotation;
                _originalImageRotation = imageRotation;
                SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver);
                break;

            case PanZoomBehaviourOnNavigation.RetainFromLastPhoto:
                var oldImageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                if (isNewPhoto)
                {
                    _canvasViewState.Rotation = imageRotation;
                    _originalImageRotation = imageRotation;
                }
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
        _canvasViewState.Rotation = imageRotation; // Ensure rotation is set correctly for default view.

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
        _isStateModifiedByUser = false; // This is a default state, not a user-modified one.
    }

    /// <summary>
    /// Sets the view for a photo by retrieving its cached state if it was panned or zoomed earlier in the current session.
    /// </summary>
    private void SetCachedView(Size imageSize, Size canvasSize, CanvasViewState cachedState)
    {
        // Apply scale and rotation directly from the cached state.
        _canvasViewState.Scale = cachedState.Scale;
        _canvasViewState.LastScaleTo = cachedState.LastScaleTo;
        _canvasViewState.Rotation = cachedState.Rotation;

        // Re-hydrate the absolute pan position from the normalized offset stored in the cache.
        // This makes the restored position correct for the current canvas size.
        var normalizedPanX = cachedState.ImagePos.X;
        var normalizedPanY = cachedState.ImagePos.Y;
        var panOffsetX = normalizedPanX * canvasSize.Width;
        var panOffsetY = normalizedPanY * canvasSize.Height;
        _canvasViewState.ImagePos = new Point(canvasSize.Width / 2.0 + panOffsetX, canvasSize.Height / 2.0 + panOffsetY);

        // After applying, recalculate if the restored state is fitted or 1:1.
        var fitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(_canvasViewState.Scale - fitScale) < 0.001f;
        IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < 0.001f;
        _canvasViewState.UpdateTransform();
        _isStateModifiedByUser = true; // A cached state is by definition a user-modified one.
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
            // ZoomChanged?.Invoke();
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
        else if (previousSize.Width > 0 && previousSize.Height > 0)
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
        _isStateModifiedByUser = true;
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
        _isStateModifiedByUser = true;
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
        _isStateModifiedByUser = true;
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
        _isStateModifiedByUser = true;
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
        _isStateModifiedByUser = true;
        CheckIfViewBackToDefaultState(scaleFactor, canvasSize, imageSize);
    }

    /// <summary>
    /// Explicit user action to set zoom to 100%.
    /// </summary>
    public void ZoomToHundred(Size canvasSize)
    {
        const float targetScale = 1.0f;
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = 1.0f;
        StartPanAndZoomAnimation(1.0f, targetPosition);

        // Set state immediately. The view is fitted only if 100% happens to be the fit scale.
        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(1.0f - screenFitScale) < 0.001f;
        IsAtOneToOne = true;
        _isStateModifiedByUser = true;
        CheckIfViewBackToDefaultState(targetScale, canvasSize, imageSize);
    }

    /// <summary>
    /// Zoom to 100% but center the view on a specific anchor point (screen coordinates).
    /// </summary>
    public void ZoomToHundred(Size canvasSize, Point anchor)
    {
        const float targetScale = 1.0f;
        _canvasViewState.LastScaleTo = 1.0f;

        // Compute the target image position so that the provided anchor remains at the same
        // screen coordinate after scaling to 1:1. Use the same formula as in precision zoom.
        var oldScale = _canvasViewState.Scale;
        // If already at 1:1, just center on anchor
        if (Math.Abs(oldScale - targetScale) < 0.0001f)
        {
            StartPanAndZoomAnimation(targetScale, anchor);
        }
        else
        {
            var newPosX = anchor.X - (targetScale / oldScale) * (anchor.X - _canvasViewState.ImagePos.X);
            var newPosY = anchor.Y - (targetScale / oldScale) * (anchor.Y - _canvasViewState.ImagePos.Y);
            var targetPos = new Point(newPosX, newPosY);
            StartPanAndZoomAnimation(targetScale, targetPos);
        }

        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < 0.001f;
        IsAtOneToOne = true;
        _isStateModifiedByUser = true;
        CheckIfViewBackToDefaultState(targetScale, canvasSize, imageSize);
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
        _isStateModifiedByUser = true;
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
        _isStateModifiedByUser = true;
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
    /// Checks if the current view state matches the photo's default view. If it does,
    /// it resets the modified flag and removes the photo from the cache.
    /// Otherwise, it marks the state as user-modified.
    /// </summary>
    private void CheckIfViewBackToDefaultState(float finalScale, Size canvasSize, Size imageSize)
    {
        // 1. Calculate the scale of the *actual* default view (which doesn't upscale small images).
        var defaultFitScale = CalculateScreenFitScale(canvasSize, imageSize, _originalImageRotation);
        var defaultInitialScale = Math.Min(defaultFitScale, 1.0f);

        // 2. Check if the final scale from the user action matches the default scale.
        var isDefaultScale = Math.Abs(finalScale - defaultInitialScale) < 0.001f;

        // 3. Check if the current rotation matches the original, unmodified rotation.
        // The modulo logic handles cases like 360 vs 0 and -90 vs 270.
        var currentRotationNormalized = ((_canvasViewState.Rotation % 360) + 360) % 360;
        var originalRotationNormalized = ((_originalImageRotation % 360) + 360) % 360;
        var isDefaultRotation = currentRotationNormalized == originalRotationNormalized;

        // 4. Check if the pan position is centered on the canvas. A tolerance of 0.5 pixels
        // is used to account for potential floating point inaccuracies.
        var canvasCenterX = canvasSize.Width / 2.0;
        var canvasCenterY = canvasSize.Height / 2.0;
        var isDefaultPan = Math.Abs(_canvasViewState.ImagePos.X - canvasCenterX) < 0.5 &&
                           Math.Abs(_canvasViewState.ImagePos.Y - canvasCenterY) < 0.5;

        // 5. The view is considered "default" only if scale, rotation, AND pan match the default state.
        if (isDefaultScale && isDefaultRotation && isDefaultPan)
        {
            _isStateModifiedByUser = false;
            if (!string.IsNullOrEmpty(_photoPath))
                _perPhotoStateCache.Remove(_photoPath);
        }
        else { _isStateModifiedByUser = true; }
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