using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Foundation;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Manages the view state (pan, zoom, rotation) of the canvas.
/// Handles user interactions, animations, and state transitions.
/// </summary>
internal class CanvasViewManager
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

    /// <summary>
    /// Fires when any animation (zoom, pan, or shrug) completes.
    /// </summary>
    public event Action AnimationCompleted;

    // --- Animation & State Fields ---

    // SpringZoom / SpringPanAndZoom drive all user-triggered zoom (damped spring).
    // PanAndZoom is the legacy cubic tween, retained ONLY for the launch open-zoom and exit zoom-out.
    private enum AnimationType { None, SpringZoom, SpringPanAndZoom, PanAndZoom, Shrug }
    private AnimationType _currentAnimation = AnimationType.None;
    private readonly Stopwatch _animationStopwatch = new();
    private double _panZoomAnimationDurationMs = Constants.PanZoomAnimationDurationNormal;
    private bool _suppressZoomUpdateForNextAnimation;

    private readonly CanvasViewState _canvasViewState;
    private float _zoomStartScale;      // cubic launch/exit tween only
    private float _zoomTargetScale;
    private Point _zoomCenter;
    private Point _panStartPosition;    // cubic tween + shrug
    private Point _panTargetPosition;
    private int _originalImageRotation;

    // --- Spring state ---
    // Output-only: the spring keeps its own scale/pan and writes them into _canvasViewState each tick,
    // never reading them back. This makes it immune to external mid-flight writes (the way the cubic
    // tween is, by recomputing from a captured start). Scale runs in LOG space for a uniform perceived
    // zoom rate; pan runs in linear (pixel) space.
    private double _lastSpringElapsedMs;
    private float _springCurrentLogScale;
    private float _springLogScaleVelocity;
    private float _springCurrentPanX;
    private float _springCurrentPanY;
    private float _springPanVelocityX;
    private float _springPanVelocityY;

    // --- State Management Properties ---

    /// <summary>
    /// A photo's remembered view, used only by <see cref="PanZoomBehaviourOnNavigation.RememberPerPhoto"/>.
    /// Two fields are stored in a deliberately *relative* form so the view restores correctly even when
    /// conditions differ between caching and restoring:
    /// <list type="bullet">
    ///   <item><see cref="NormalizedPan"/> — pan offset from the canvas centre divided by canvas size
    ///     (not absolute pixels), so it survives window resizing.</item>
    ///   <item><see cref="UserRotation"/> — the user's manual rotation only (total rotation minus the
    ///     image's EXIF baseline), so re-applying it onto a different baseline (e.g. a preview whose
    ///     embedded thumbnail is pre-oriented vs. an HQ image whose EXIF rotation is applied at render)
    ///     never double-counts the EXIF orientation.</item>
    /// </list>
    /// </summary>
    private readonly record struct PerPhotoViewState(
        float Scale, float LastScaleTo, Point NormalizedPan, int UserRotation);

    /// <summary>
    /// Per-session cache of user-modified views, keyed by photo file path.
    /// Populated on navigate-away (<see cref="CacheCurrentViewState"/>), consumed on revisit
    /// (<see cref="SetCachedView"/>), and pruned when a photo returns to its default view
    /// (<see cref="CheckIfViewBackToDefaultState"/>).
    /// </summary>
    private readonly Dictionary<string, PerPhotoViewState> _perPhotoStateCache = [];

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

    // --- Constructor ---

    public CanvasViewManager(CanvasViewState canvasViewState)
    {
        _canvasViewState = canvasViewState;
    }

    // --- Public API Methods ---

    /// <summary>
    /// Saves the current view for <paramref name="photoPath"/> if "RememberPerPhoto" is enabled and the
    /// user has actually modified the view (panned, zoomed, or rotated). Pan is stored normalized to the
    /// canvas size and rotation relative to the EXIF baseline — see <see cref="PerPhotoViewState"/>.
    /// </summary>
    public void CacheCurrentViewState(string photoPath, Size canvasSize)
    {
        if (AppConfig.Settings.PanZoomBehaviourOnNavigation != PanZoomBehaviourOnNavigation.RememberPerPhoto
            || !_isStateModifiedByUser)
            return;

        // Convert the absolute pan to a canvas-size-independent offset from the centre.
        var panOffsetX = _canvasViewState.ImagePos.X - canvasSize.Width / 2.0;
        var panOffsetY = _canvasViewState.ImagePos.Y - canvasSize.Height / 2.0;
        var normalizedPanX = canvasSize.Width > 0 ? panOffsetX / canvasSize.Width : 0; // guard div-by-zero
        var normalizedPanY = canvasSize.Height > 0 ? panOffsetY / canvasSize.Height : 0;

        // Strip the EXIF baseline so only the user's manual rotation is remembered; SetCachedView
        // re-bases it onto whatever baseline the image presents on the next visit.
        var userRotation = _canvasViewState.Rotation - _originalImageRotation;

        _perPhotoStateCache[photoPath] = new PerPhotoViewState(
            _canvasViewState.Scale, _canvasViewState.LastScaleTo,
            new Point(normalizedPanX, normalizedPanY), userRotation);
    }

    /// <summary>
    /// Sets the initial scale and position for a new image.
    /// </summary>
    public void SetScaleAndPosition(string photoPath, Size imageSize, int imageRotation, Size canvasSize,
        bool isFirstPhotoEver, bool isNewPhoto, bool isUpgradeFromPlaceholder)
    {
        _photoPath = photoPath;

        // The very first photo on launch always opens from a clean, fitted view.
        if (isFirstPhotoEver)
        {
            _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
            _canvasViewState.Rotation = imageRotation;
            _originalImageRotation = imageRotation;
            SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver: true);
        }
        // A pure quality upgrade (Preview → HQ of the same already-displayed photo) must preserve
        // the live visual view exactly. Scale is adjusted so the scene-relative zoom (Scale / fitScale)
        // is unchanged; pan stays fixed in canvas coordinates; rotation is re-based onto the new EXIF
        // baseline. Routing this through SetViewFromPrevious would apply an image-size ratio to the
        // pan offset, which is wrong — preview thumbnails can be 10–15× smaller than the HQ image,
        // making a 300 px pan become 3000+ px after the upgrade.
        else if (!isNewPhoto && !isUpgradeFromPlaceholder)
        {
            // Pure quality upgrade (Preview → HQ of the same photo). Must be visually seamless — never
            // reset or jump, even when a zoom/pan animation is still in flight as the HQ buffer arrives.
            var animating = _currentAnimation is AnimationType.SpringZoom
                or AnimationType.SpringPanAndZoom or AnimationType.PanAndZoom;

            var oldImageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
            var oldRotation = _canvasViewState.Rotation;
            _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
            _canvasViewState.Rotation += imageRotation - _originalImageRotation;
            _originalImageRotation = imageRotation;

            var oldFitScale = CalculateScreenFitScale(canvasSize, oldImageSize, oldRotation);
            var newFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
            var scaleRatio = oldFitScale > 0 ? newFitScale / oldFitScale : 1f;

            if (animating)
            {
                // An animation is running: keep it alive. Rescale BOTH the displayed scale and the
                // animation's internal scale/target by the resolution ratio, so the image keeps the same
                // visual size and the spring/tween keeps converging to the resolution-adjusted target.
                // Pan is screen-space (unchanged here), so the animation's pan state stays valid.
                // Snapping to fit (the IsFittedToScreen path below) would visibly reset mid-animation.
                _canvasViewState.Scale *= scaleRatio;
                _canvasViewState.LastScaleTo *= scaleRatio;
                RebaseAnimationScale(scaleRatio);
                IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < 0.001f;
            }
            else if (IsFittedToScreen)
            {
                _canvasViewState.Scale = newFitScale;
                _canvasViewState.LastScaleTo = newFitScale;
                _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
                IsAtOneToOne = Math.Abs(newFitScale - 1.0f) < 0.001f;
            }
            else
            {
                // Preserve scene-relative zoom: multiply scale by (newFitScale / oldFitScale).
                // Pan stays unchanged in canvas coordinates — the image centre remains at the same
                // pixel on screen regardless of the change in image resolution.
                _canvasViewState.Scale *= scaleRatio;
                _canvasViewState.LastScaleTo *= scaleRatio;
                IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < 0.001f;
            }
            _canvasViewState.UpdateTransform();
        }
        else
        {
            // Navigating to a genuinely new photo (or first real content after a placeholder): cancel any
            // in-flight animation so its stale internal state can't clobber the freshly-applied view.
            StopAnimationSnappingToTarget();

            // New photo navigation or upgrade from placeholder: apply the configured behaviour.
            switch (AppConfig.Settings.PanZoomBehaviourOnNavigation)
            {
                case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                    // Reached for a photo's initial display or first real content after a placeholder.
                    // Establish the EXIF baseline before SetCachedView re-bases the stored user-rotation.
                    _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                    _originalImageRotation = imageRotation;

                    if (_perPhotoStateCache.TryGetValue(photoPath, out var cachedState))
                        SetCachedView(imageSize, canvasSize, cachedState);
                    else
                        SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver: false);
                    break;

                case PanZoomBehaviourOnNavigation.Reset:
                    _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                    _canvasViewState.Rotation = imageRotation;
                    _originalImageRotation = imageRotation;
                    SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver: false);
                    break;

                case PanZoomBehaviourOnNavigation.RetainFromLastPhoto:
                    var oldImageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
                    _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                    if (isNewPhoto)
                    {
                        _canvasViewState.Rotation = imageRotation;
                        _originalImageRotation = imageRotation;
                    }
                    else // isUpgradeFromPlaceholder
                    {
                        _canvasViewState.Rotation += imageRotation - _originalImageRotation;
                        _originalImageRotation = imageRotation;
                    }
                    SetViewFromPrevious(oldImageSize, imageSize, canvasSize);
                    break;
            }
        }
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Sets the default view for a photo by fitting it to screen or 100% for smaller photos.
    /// </summary>
    private void SetDefaultView(Size imageSize, int imageRotation, Size canvasSize, bool isFirstPhotoEver)
    {
        var defaultFitScale = CalculateScreenFitScale(canvasSize, imageSize, imageRotation);
        var initialScale = AppConfig.Settings.StretchSmallImages ? defaultFitScale : Math.Min(defaultFitScale, 1.0f);

        _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = initialScale;
        _canvasViewState.Rotation = imageRotation; // Ensure rotation is set correctly for default view.

        if (isFirstPhotoEver && AppConfig.Settings.OpenExitZoom)
        {
            _canvasViewState.Scale = 0.01f;
            _suppressZoomUpdateForNextAnimation = true;
            StartLaunchExitTween(initialScale, new Point(canvasSize.Width / 2, canvasSize.Height / 2));
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
    /// Restores a remembered view (see <see cref="PerPhotoViewState"/>) onto the current photo.
    /// Pan is de-normalized for the current canvas size, and the remembered user-rotation is re-based
    /// onto this image's EXIF baseline (<see cref="_originalImageRotation"/>, which the caller has just
    /// set). Re-basing keeps Rotation and _originalImageRotation consistent, so a subsequent Preview → HQ
    /// upgrade's rotation delta nets to zero error even when the preview and HQ baselines differ.
    /// </summary>
    private void SetCachedView(Size imageSize, Size canvasSize, PerPhotoViewState cachedState)
    {
        _canvasViewState.Scale = cachedState.Scale;
        _canvasViewState.LastScaleTo = cachedState.LastScaleTo;
        _canvasViewState.Rotation = _originalImageRotation + cachedState.UserRotation;

        // Re-hydrate the absolute pan position from the normalized offset for the current canvas size.
        var normalizedPanX = cachedState.NormalizedPan.X;
        var normalizedPanY = cachedState.NormalizedPan.Y;
        var panOffsetX = normalizedPanX * canvasSize.Width;
        var panOffsetY = normalizedPanY * canvasSize.Height;
        _canvasViewState.ImagePos = new Point(canvasSize.Width / 2.0 + panOffsetX, canvasSize.Height / 2.0 + panOffsetY);

        // Recalculate fitted/1:1 flags. Must check both scale AND pan: a panned-at-fit-scale photo is not "fitted".
        var fitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(_canvasViewState.Scale - fitScale) < 0.001f
                           && Math.Abs(normalizedPanX) < 0.001
                           && Math.Abs(normalizedPanY) < 0.001;
        IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < 0.001f;
        _canvasViewState.UpdateTransform();
        _isStateModifiedByUser = true; // A cached state is by definition user-modified.
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

    private static readonly float[] ZoomSnapPoints = [0.5f, 1.0f, 2.0f, 5.0f, 10.0f];

    private static float ApplyZoomSnap(float newScale, float oldScale, ZoomDirection direction)
    {
        if (direction == ZoomDirection.In)
        {
            float? snap = ZoomSnapPoints
                .Where(s => s > oldScale && s <= newScale)
                .Cast<float?>()
                .FirstOrDefault();
            if (snap.HasValue) return snap.Value;
        }
        else
        {
            float? snap = ZoomSnapPoints
                .Where(s => s < oldScale && s >= newScale)
                .Cast<float?>()
                .LastOrDefault();
            if (snap.HasValue) return snap.Value;
        }
        return newScale;
    }

    /// <summary>
    /// Performs a standard zoom operation centered on the canvas.
    /// </summary>
    public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var rawScaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        var scaleTo = AppConfig.Settings.StickyZoomLevels
            ? ApplyZoomSnap(rawScaleTo, _canvasViewState.LastScaleTo, zoomDirection)
            : rawScaleTo;
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
        var rawScaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        var scaleTo = AppConfig.Settings.StickyZoomLevels
            ? ApplyZoomSnap(rawScaleTo, _canvasViewState.LastScaleTo, zoomDirection)
            : rawScaleTo;
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
            StartSpringPanAndZoomAnimation(targetScale, anchor);

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
        StartLaunchExitTween(0.001f, targetPosition);
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
        StartSpringPanAndZoomAnimation(scaleFactor, targetPosition);

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
        StartSpringPanAndZoomAnimation(1.0f, targetPosition);

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
            StartSpringPanAndZoomAnimation(targetScale, anchor);
        }
        else
        {
            var newPosX = anchor.X - (targetScale / oldScale) * (anchor.X - _canvasViewState.ImagePos.X);
            var newPosY = anchor.Y - (targetScale / oldScale) * (anchor.Y - _canvasViewState.ImagePos.Y);
            var targetPos = new Point(newPosX, newPosY);
            StartSpringPanAndZoomAnimation(targetScale, targetPos);
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
        var defaultInitialScale = AppConfig.Settings.StretchSmallImages ? defaultFitScale : Math.Min(defaultFitScale, 1.0f);

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
    /// Called by CanvasController on the Win2D background thread each Update tick.
    /// Advances whichever pan/zoom/shrug animation is currently active.
    /// </summary>
    public void OnUpdate()
    {
        if (!PanZoomAnimationOnGoing) return; // Fast exit — no animation running

        switch (_currentAnimation)
        {
            case AnimationType.SpringZoom:
                AnimateSpringZoom();
                break;
            case AnimationType.SpringPanAndZoom:
                AnimateSpringPanAndZoom();
                break;
            case AnimationType.PanAndZoom:
                AnimatePanAndZoom();
                break;
            case AnimationType.Shrug:
                AnimateShrug();
                break;
        }
    }

    /// <summary>
    /// Starts (or re-targets) a pure spring zoom, keeping the anchor point stationary on screen.
    /// If a spring is already running, only the target/anchor change — scale velocity carries forward,
    /// so rapid wheel notches redirect smoothly without a restart lurch.
    /// </summary>
    private void StartZoomAnimation(float targetScale, Point zoomAnchor)
    {
        SeedScaleSpringIfFresh();
        _zoomTargetScale = targetScale;
        _zoomCenter = zoomAnchor;

        _currentAnimation = AnimationType.SpringZoom;
        PanZoomAnimationOnGoing = true;
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Starts (or re-targets) a spring that drives scale and pan together (fit / 100% / step).
    /// Velocity carries forward across re-targets, as with <see cref="StartZoomAnimation"/>.
    /// </summary>
    private void StartSpringPanAndZoomAnimation(float targetScale, Point targetPosition)
    {
        SeedScaleSpringIfFresh();
        // Pan state must be seeded whenever the previous animation wasn't already a pan spring
        // (e.g. coming from a scale-only SpringZoom, which doesn't track pan velocity).
        if (_currentAnimation != AnimationType.SpringPanAndZoom)
        {
            _springCurrentPanX = (float)_canvasViewState.ImagePos.X;
            _springCurrentPanY = (float)_canvasViewState.ImagePos.Y;
            _springPanVelocityX = 0f;
            _springPanVelocityY = 0f;
        }
        _zoomTargetScale = targetScale;
        _panTargetPosition = targetPosition;

        _currentAnimation = AnimationType.SpringPanAndZoom;
        PanZoomAnimationOnGoing = true;
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Seeds the scale spring's internal state from the live view, but only when no spring is already
    /// running — when one is, scale position/velocity are preserved for seamless re-targeting.
    /// </summary>
    private void SeedScaleSpringIfFresh()
    {
        if (_currentAnimation is AnimationType.SpringZoom or AnimationType.SpringPanAndZoom) return;
        _springCurrentLogScale = (float)Math.Log(_canvasViewState.Scale);
        _springLogScaleVelocity = 0f;
        _animationStopwatch.Restart();
        _lastSpringElapsedMs = 0;
    }

    /// <summary>
    /// Starts the legacy cubic pan+zoom tween. Used ONLY for the launch open-zoom and exit zoom-out,
    /// whose deterministic, fixed-duration completion is depended on elsewhere.
    /// </summary>
    private void StartLaunchExitTween(float targetScale, Point targetPosition)
    {
        _zoomStartScale = _canvasViewState.Scale;
        _zoomTargetScale = targetScale;
        _panStartPosition = _canvasViewState.ImagePos;
        _panTargetPosition = targetPosition;

        _currentAnimation = AnimationType.PanAndZoom;
        PanZoomAnimationOnGoing = true;
        _animationStopwatch.Restart();
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Starts the shrug/shake animation.
    /// </summary>
    private void StartShrugAnimation()
    {
        _panStartPosition = _canvasViewState.ImagePos;
        _currentAnimation = AnimationType.Shrug;
        PanZoomAnimationOnGoing = true;
        _animationStopwatch.Restart();
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Computes the per-frame delta time (seconds) from the stopwatch, clamped so a long gap (e.g. the
    /// frame after the Win2D control un-pauses) can't make the spring take one giant, overshooting step.
    /// Returns false when no time has elapsed (a tick fired on the same instant the spring started).
    /// </summary>
    private bool TryGetSpringDt(out float dt)
    {
        var elapsed = _animationStopwatch.Elapsed.TotalMilliseconds;
        dt = (float)Math.Min((elapsed - _lastSpringElapsedMs) / 1000.0, Constants.SpringMaxDtSeconds);
        _lastSpringElapsedMs = elapsed;
        return dt > 0f;
    }

    /// <summary>
    /// Advances one axis of a critically/over-damped harmonic oscillator one step toward <paramref name="target"/>.
    /// </summary>
    private static void StepSpringAxis(ref float current, ref float velocity, float target, float dt)
    {
        var displacement = current - target;
        var acceleration = -Constants.SpringStiffness * displacement - Constants.SpringDamping * velocity;
        velocity += acceleration * dt;
        current += velocity * dt;
    }

    /// <summary>
    /// Finalizes any spring animation: stops the clock, clears state, and fires AnimationCompleted.
    /// </summary>
    private void FinishSpringAnimation()
    {
        _animationStopwatch.Stop();
        _currentAnimation = AnimationType.None;
        PanZoomAnimationOnGoing = false;
        _suppressZoomUpdateForNextAnimation = false;
        AnimationCompleted?.Invoke();
    }

    /// <summary>
    /// Immediately terminates any in-flight animation, snapping the view to that animation's intended
    /// target first (so a subsequent RetainFromLastPhoto navigation inherits the settled view rather than
    /// a mid-flight frame). Used when navigating to a genuinely new photo — see <see cref="SetScaleAndPosition"/>.
    /// Does NOT fire AnimationCompleted: the caller is about to overwrite the view, and the new renderer's
    /// off-screen redraw is driven separately by InstallRenderer.
    /// </summary>
    private void StopAnimationSnappingToTarget()
    {
        switch (_currentAnimation)
        {
            case AnimationType.SpringZoom:
                // Final anchor-preserving step to the exact target scale.
                var oldScale = _canvasViewState.Scale;
                if (oldScale > 0)
                {
                    _canvasViewState.ImagePos.X = _zoomCenter.X - (_zoomTargetScale / oldScale) * (_zoomCenter.X - _canvasViewState.ImagePos.X);
                    _canvasViewState.ImagePos.Y = _zoomCenter.Y - (_zoomTargetScale / oldScale) * (_zoomCenter.Y - _canvasViewState.ImagePos.Y);
                }
                _canvasViewState.Scale = _zoomTargetScale;
                _canvasViewState.UpdateTransform();
                break;
            case AnimationType.SpringPanAndZoom:
            case AnimationType.PanAndZoom:
                _canvasViewState.Scale = _zoomTargetScale;
                _canvasViewState.ImagePos = _panTargetPosition;
                _canvasViewState.UpdateTransform();
                break;
            case AnimationType.None:
            case AnimationType.Shrug:
                break;
        }

        _animationStopwatch.Stop();
        _currentAnimation = AnimationType.None;
        PanZoomAnimationOnGoing = false;
        _suppressZoomUpdateForNextAnimation = false;
    }

    /// <summary>
    /// Re-bases an in-flight animation's scale by <paramref name="scaleFactor"/> after a resolution change
    /// (Preview → HQ). The displayed scale is rescaled by the caller; this keeps the animation's internal
    /// scale state and target consistent so it keeps converging to the same *visual* zoom without a jump.
    /// Pan is in screen space and so is unaffected.
    /// </summary>
    private void RebaseAnimationScale(float scaleFactor)
    {
        if (scaleFactor <= 0f) return;
        _zoomTargetScale *= scaleFactor;
        switch (_currentAnimation)
        {
            case AnimationType.SpringZoom:
            case AnimationType.SpringPanAndZoom:
                _springCurrentLogScale += (float)Math.Log(scaleFactor);
                break;
            case AnimationType.PanAndZoom: // cubic launch/exit tween interpolates start → target
                _zoomStartScale *= scaleFactor;
                break;
        }
    }

    /// <summary>
    /// The rendering loop logic for pure spring zoom. Scale springs in log space; pan is derived each
    /// tick to keep <see cref="_zoomCenter"/> stationary on screen.
    /// </summary>
    private void AnimateSpringZoom()
    {
        if (!TryGetSpringDt(out var dt)) return;

        var targetLog = (float)Math.Log(_zoomTargetScale);
        StepSpringAxis(ref _springCurrentLogScale, ref _springLogScaleVelocity, targetLog, dt);

        var settled = Math.Abs(_springCurrentLogScale - targetLog) < Constants.SpringScaleSettleEpsilon
                      && Math.Abs(_springLogScaleVelocity) < Constants.SpringScaleVelocitySettle;

        float newScale;
        if (settled)
        {
            _springCurrentLogScale = targetLog;
            _springLogScaleVelocity = 0f;
            newScale = _zoomTargetScale;
        }
        else
        {
            newScale = (float)Math.Exp(_springCurrentLogScale);
        }

        // Keep the zoom anchor stationary as the scale changes (ratio of new to previous scale).
        var oldScale = _canvasViewState.Scale;
        _canvasViewState.ImagePos.X = _zoomCenter.X - (newScale / oldScale) * (_zoomCenter.X - _canvasViewState.ImagePos.X);
        _canvasViewState.ImagePos.Y = _zoomCenter.Y - (newScale / oldScale) * (_zoomCenter.Y - _canvasViewState.ImagePos.Y);
        _canvasViewState.Scale = newScale;
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
        if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();

        if (settled) FinishSpringAnimation();
    }

    /// <summary>
    /// The rendering loop logic for the combined spring pan+zoom (fit / 100% / step). Scale springs in
    /// log space; pan X and pan Y spring independently in pixel space.
    /// </summary>
    private void AnimateSpringPanAndZoom()
    {
        if (!TryGetSpringDt(out var dt)) return;

        var targetLog = (float)Math.Log(_zoomTargetScale);
        StepSpringAxis(ref _springCurrentLogScale, ref _springLogScaleVelocity, targetLog, dt);
        StepSpringAxis(ref _springCurrentPanX, ref _springPanVelocityX, (float)_panTargetPosition.X, dt);
        StepSpringAxis(ref _springCurrentPanY, ref _springPanVelocityY, (float)_panTargetPosition.Y, dt);

        var scaleSettled = Math.Abs(_springCurrentLogScale - targetLog) < Constants.SpringScaleSettleEpsilon
                           && Math.Abs(_springLogScaleVelocity) < Constants.SpringScaleVelocitySettle;
        var panSettled = Math.Abs(_springCurrentPanX - _panTargetPosition.X) < Constants.SpringPanSettleEpsilon
                         && Math.Abs(_springPanVelocityX) < Constants.SpringPanVelocitySettle
                         && Math.Abs(_springCurrentPanY - _panTargetPosition.Y) < Constants.SpringPanSettleEpsilon
                         && Math.Abs(_springPanVelocityY) < Constants.SpringPanVelocitySettle;
        var settled = scaleSettled && panSettled;

        if (settled)
        {
            _springCurrentLogScale = targetLog;
            _springLogScaleVelocity = 0f;
            _springCurrentPanX = (float)_panTargetPosition.X;
            _springCurrentPanY = (float)_panTargetPosition.Y;
            _springPanVelocityX = 0f;
            _springPanVelocityY = 0f;
            _canvasViewState.Scale = _zoomTargetScale;
        }
        else
        {
            _canvasViewState.Scale = (float)Math.Exp(_springCurrentLogScale);
        }
        _canvasViewState.ImagePos.X = _springCurrentPanX;
        _canvasViewState.ImagePos.Y = _springCurrentPanY;
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
        if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();

        if (settled)
        {
            // Final "truth-setter" for the fitted / 1:1 flags (mirrors the cubic tween).
            var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
            var canvasSize = new Size(_panTargetPosition.X * 2, _panTargetPosition.Y * 2);
            if (canvasSize.Width > 0 && canvasSize.Height > 0)
            {
                var screenFitScale = CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
                IsFittedToScreen = Math.Abs(_zoomTargetScale - screenFitScale) < 0.001f;
                IsAtOneToOne = Math.Abs(_zoomTargetScale - 1.0f) < 0.001f;
            }
            FinishSpringAnimation();
        }
    }

    /// <summary>
    /// Legacy ease-out cubic pan+zoom tween. Retained ONLY for the launch open-zoom and exit zoom-out
    /// (see <see cref="StartLaunchExitTween"/>); all user-triggered zoom uses the spring path instead.
    /// </summary>
    private void AnimatePanAndZoom()
    {
        var elapsed = _animationStopwatch.Elapsed.TotalMilliseconds;
        var t = Math.Clamp(elapsed / _panZoomAnimationDurationMs, 0.0, 1.0);
        float easedT = 1f - (float)Math.Pow(1 - t, 3); // Ease-out cubic: f(t) = 1 - (1 - t)^3

        // Interpolate scale and position.
        _canvasViewState.Scale = _zoomStartScale + (_zoomTargetScale - _zoomStartScale) * easedT;
        // Interpolate position (a simple linear interpolation)
        _canvasViewState.ImagePos.X = _panStartPosition.X + (_panTargetPosition.X - _panStartPosition.X) * easedT;
        _canvasViewState.ImagePos.Y = _panStartPosition.Y + (_panTargetPosition.Y - _panStartPosition.Y) * easedT;

        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
        if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();

        // Stop the animation when finished.
        if (t >= 1.0)
        {
            _animationStopwatch.Stop();
            _currentAnimation = AnimationType.None;
            PanZoomAnimationOnGoing = false;
            _suppressZoomUpdateForNextAnimation = false;
            AnimationCompleted?.Invoke();
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
        var elapsed = _animationStopwatch.Elapsed.TotalMilliseconds;
        var t = Math.Clamp(elapsed / Constants.ShrugAnimationDurationMs, 0.0, 1.0);

        if (t >= 1.0)
        {
            _animationStopwatch.Stop();
            // Animation finished. Ensure the image is back to its exact starting position.
            _canvasViewState.ImagePos = _panStartPosition;
            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();

            _currentAnimation = AnimationType.None;
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
    /// Cleans up resources.
    /// </summary>
    public void Dispose()
    {
        PanZoomAnimationOnGoing = false;
        _currentAnimation = AnimationType.None;
        _animationStopwatch.Stop();
    }
}