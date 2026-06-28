using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.Foundation;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers.Animation;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Owns the canvas view transform — scale (zoom), pan position, and rotation — and animates changes to it.
/// All public methods translate a user action (zoom at point, fit, 100%, pan, rotate, …) or a lifecycle
/// event (new photo, preview→HQ upgrade, launch, exit, resize) into updates of <see cref="CanvasViewState"/>,
/// firing events so the canvas redraws and the UI (zoom %, fit/1:1 toggles) stays in sync.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every method here runs on the Win2D render ("W2D") thread — callers on the UI
/// thread funnel through <c>CanvasController</c>'s action queue. <see cref="OnUpdate"/> is ticked once per
/// frame from that thread and advances whatever animation is active. So there is no locking here; treat the
/// whole class as single-threaded.</para>
///
/// <para><b>Animation models.</b> There are three <see cref="IViewAnimation"/> strategies; at most one is
/// active at a time (<see cref="_activeAnimation"/>), advanced each frame via <see cref="OnUpdate"/>:
/// <list type="bullet">
///   <item><see cref="AnchoredZoomAnimation"/> — scale-only damped spring; pan is recomputed each tick to keep
///     a zoom anchor point stationary (wheel/keyboard/anchored-step zoom).</item>
///   <item><see cref="PanZoomAnimation"/> — three independent springs (scale + panX + panY) toward a target
///     (Fit, 100%, centred step, double-click, AND launch open-zoom / exit zoom-out).</item>
///   <item><see cref="ShrugAnimation"/> — a decaying sine wiggle for "action rejected" feedback (not a spring).</item>
/// </list>
/// The animations own their type-specific geometry; this class is their <see cref="IAnimationHost"/>, holding
/// the shared spring bank, frame clock, target scale, and all event / snapping / completion plumbing.
/// Pan, rotate, and precision-touchpad zoom are applied directly (no animation).</para>
///
/// <para><b>Spring physics.</b> Each axis is a damped harmonic oscillator (a <see cref="SpringAxis"/>)
/// integrated per frame in <see cref="SpringAxis.Step"/> (<c>accel = -k·displacement - c·velocity</c>), with
/// <c>k</c>/<c>c</c> from <see cref="Constants.SpringStiffness"/>/<see cref="Constants.SpringDamping"/>.
/// Scale is one <see cref="SpringAxis"/> (in log space), pan X/Y two more. Key choices:
/// <list type="bullet">
///   <item><b>Scale springs in LOG space</b> (and is rebuilt via <c>exp</c>): zoom is perceived
///     multiplicatively, so log space gives a uniform perceived rate and can never reach ≤ 0. Pan springs
///     in linear pixels.</item>
///   <item><b>Sub-stepping</b>: a frame's <c>dt</c> (clamped to <see cref="Constants.SpringMaxDtSeconds"/>)
///     is integrated in fixed sub-steps (<see cref="Constants.SpringMaxSubStepSeconds"/>) so a large frame
///     — e.g. the first frame after launch — can't make one Euler step overshoot. This is pure math; the
///     canvas still draws once per frame. It also makes the motion frame-rate-independent (50 Hz vs 180 Hz
///     differ only in smoothness, not duration/path).</item>
///   <item><b>Output-only spring state</b> (each <see cref="SpringAxis"/>'s Position/Velocity): the spring
///     WRITES scale/pan into <see cref="CanvasViewState"/> each tick and never reads them back, so an external mid-flight
///     write can't perturb it. State is seeded from the live view only when starting fresh
///     (<see cref="SeedScaleSpringIfFresh"/>); a running spring keeps its velocity so re-targets (e.g. rapid
///     wheel notches) stay continuous instead of restarting with a lurch.</item>
/// </list></para>
///
/// <para><b>Lifecycle transitions</b> (<see cref="SetScaleAndPosition"/>):
/// <list type="bullet">
///   <item><b>New photo / placeholder upgrade</b> → <see cref="StopAnimationSnappingToTarget"/> cancels any
///     in-flight animation (snapping to its target first, so Retain-mode inherits the settled view).</item>
///   <item><b>Preview→HQ upgrade of the same photo</b> → must be seamless. If an animation is in flight it
///     is kept alive and rescaled by the resolution ratio (<see cref="RebaseAnimationScale"/>) so the image
///     keeps its on-screen size and the spring keeps converging; otherwise scale is adjusted to preserve the
///     scene-relative zoom.</item>
///   <item><b>Launch open-zoom</b> force-reseeds the spring from 0.01 (a deliberate reset, guarded on a
///     valid fit scale so it never feeds <c>log(0)</c>). <b>Exit zoom-out</b> springs toward ~0; the caller
///     closes the window on a short wait (see <see cref="Constants.PanZoomAnimationDurationForExit"/>),
///     since full spring-settle (~0.6 s) is far later than when the image visually vanishes.</item>
/// </list></para>
///
/// <para><b>Per-photo view cache</b> (<c>RememberPerPhoto</c> mode): a navigated-away photo's view is stored
/// normalized (pan relative to canvas size, rotation relative to the EXIF baseline) and restored on revisit
/// — see <see cref="PerPhotoViewStore"/>.</para>
///
/// <para><b>Tuning</b> lives entirely in <see cref="Constants"/> (each spring constant documents the effect
/// of raising/lowering it). A fuller write-up is in <c>docs/pan-zoom-physics.md</c>.</para>
/// </remarks>
internal class CanvasViewManager : IAnimationHost
{
    // --- Fields ---

    // Collaborators & current photo.
    private readonly CanvasViewState _canvasViewState;
    private string _photoPath = string.Empty;
    private int _originalImageRotation;

    /// <summary>
    /// Per-session memory of user-modified views for "RememberPerPhoto" mode (see <see cref="PerPhotoViewStore"/>).
    /// Populated on navigate-away (<see cref="CacheCurrentViewState"/>), consumed on revisit
    /// (<see cref="ApplyRestoredView"/>), and pruned when a photo returns to its default view
    /// (<see cref="CheckIfViewBackToDefaultState"/>).
    /// </summary>
    private readonly PerPhotoViewStore _viewStore = new();

    /// <summary>
    /// Tracks if the view state has been modified by user input (pan, zoom, rotate).
    /// Used to determine if the state is worth caching for "RememberPerPhoto" mode.
    /// </summary>
    private bool _isStateModifiedByUser;

    // Animation substrate. At most one animation is active (null at rest); its concrete type encodes "which
    // kind", replacing the old AnimationType enum + switch. The animation owns its type-specific geometry; the
    // shared spring bank / clock / target scale below stay here so velocity and clock carry continuously across
    // re-targets AND across animation types. The springs are output-only: they write scale/pan into
    // _canvasViewState each tick and never read it back, so an external mid-flight write can't perturb them.
    private IViewAnimation _activeAnimation;
    private readonly Stopwatch _animationStopwatch = new();
    private bool _suppressZoomUpdateForNextAnimation;
    private float _zoomTargetScale;
    private double _lastSpringElapsedMs;
    private readonly SpringAxis _scaleSpring = new();   // springs LOG(scale)
    private readonly SpringAxis _panXSpring = new();    // springs ImagePos.X (pixels)
    private readonly SpringAxis _panYSpring = new();    // springs ImagePos.Y (pixels)

    /// <summary>
    /// Tolerance for treating two scale values as equal (fit / 1:1 / default-state checks). Scales are
    /// floats derived from divisions and log/exp round-trips, so exact equality never holds; 0.001 = 0.1%
    /// of scale is well below a visible difference yet far above float noise.
    /// </summary>
    private const float ScaleTolerance = 0.001f;

    // --- Events ---

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

    // --- Properties ---

    /// <summary>
    /// Indicates if a pan or zoom animation is currently in progress (i.e. an <see cref="IViewAnimation"/> is
    /// active). Read by <c>CanvasController</c> to decide whether to keep ticking the render loop.
    /// </summary>
    /// <remarks>
    /// Pixel snapping is the inverse of this: <see cref="BeginAnimation"/> turns
    /// <see cref="CanvasViewState.SnapTranslation"/> off for the duration and <see cref="ClearActiveAnimation"/>
    /// turns it back on, so snapping is on exactly when no animation is running. That makes it structurally
    /// impossible to leave snapping off at rest — every path that ends an animation goes through
    /// <see cref="ClearActiveAnimation"/>. Stop paths must rebuild the transform (<c>UpdateTransform()</c>)
    /// AFTER clearing so the resting frame is rebuilt with rounding applied.
    /// </remarks>
    public bool PanZoomAnimationOnGoing => _activeAnimation != null;

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

    public CanvasViewManager(CanvasViewState canvasViewState) { _canvasViewState = canvasViewState; }

    // --- Lifecycle ---

    /// <summary>
    /// Applies the initial scale and position for a newly displayed image, dispatching on how it arrived:
    /// the first launch photo, a pure quality upgrade of the current photo, or navigation to new content.
    /// </summary>
    public void SetScaleAndPosition(string photoPath, Size imageSize, int imageRotation, Size canvasSize,
        bool isFirstPhotoEver, bool isNewPhoto, bool isUpgradeFromPlaceholder)
    {
        _photoPath = photoPath;

        if (isFirstPhotoEver)
            ApplyFirstPhotoView(imageSize, imageRotation, canvasSize);
        else if (!isNewPhoto && !isUpgradeFromPlaceholder)
            ApplyQualityUpgrade(imageSize, imageRotation, canvasSize);
        else
            ApplyNavigationView(photoPath, imageSize, imageRotation, canvasSize, isNewPhoto);

        ViewChanged?.Invoke();
    }

    /// <summary>The very first photo on launch always opens from a clean, fitted view.</summary>
    private void ApplyFirstPhotoView(Size imageSize, int imageRotation, Size canvasSize)
    {
        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
        _canvasViewState.Rotation = imageRotation;
        _originalImageRotation = imageRotation;
        SetDefaultView(imageSize, imageRotation, canvasSize, isFirstPhotoEver: true);
    }

    /// <summary>
    /// A pure quality upgrade (Preview → HQ of the same already-displayed photo) must preserve the live visual
    /// view exactly — never reset or jump, even when a zoom/pan animation is still in flight as the HQ buffer
    /// arrives. Scale is adjusted so the scene-relative zoom (Scale / fitScale) is unchanged; pan stays fixed in
    /// canvas coordinates; rotation is re-based onto the new EXIF baseline. (Routing this through
    /// <see cref="SetViewFromPrevious"/> would wrongly apply an image-size ratio to the pan offset — preview
    /// thumbnails can be 10–15× smaller than the HQ image, turning a 300 px pan into 3000+ px after the upgrade.)
    /// </summary>
    private void ApplyQualityUpgrade(Size imageSize, int imageRotation, Size canvasSize)
    {
        var animating = _activeAnimation is AnchoredZoomAnimation or PanZoomAnimation;

        var oldImageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var oldRotation = _canvasViewState.Rotation;
        _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
        _canvasViewState.Rotation += imageRotation - _originalImageRotation;
        _originalImageRotation = imageRotation;

        var oldFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, oldImageSize, oldRotation);
        var newFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        var scaleRatio = oldFitScale > 0 ? newFitScale / oldFitScale : 1f;

        if (animating)
        {
            // Keep the animation alive: rescale BOTH the displayed scale and the animation's internal
            // scale/target by the resolution ratio, so the image keeps the same visual size and the spring
            // keeps converging. Pan is screen-space (unchanged), so the animation's pan state stays valid.
            // Snapping to fit (the IsFittedToScreen path) would visibly reset mid-animation.
            _canvasViewState.Scale *= scaleRatio;
            _canvasViewState.LastScaleTo *= scaleRatio;
            RebaseAnimationScale(scaleRatio);
            IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < ScaleTolerance;
        }
        else if (IsFittedToScreen)
        {
            _canvasViewState.Scale = newFitScale;
            _canvasViewState.LastScaleTo = newFitScale;
            _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            IsAtOneToOne = Math.Abs(newFitScale - 1.0f) < ScaleTolerance;
        }
        else
        {
            // Preserve scene-relative zoom: multiply scale by (newFitScale / oldFitScale). Pan stays unchanged
            // in canvas coordinates — the image centre remains at the same pixel on screen.
            _canvasViewState.Scale *= scaleRatio;
            _canvasViewState.LastScaleTo *= scaleRatio;
            IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < ScaleTolerance;
        }
        _canvasViewState.UpdateTransform();
    }

    /// <summary>
    /// Navigation to genuinely new content (a new photo, or first real content after a placeholder): cancels
    /// any in-flight animation, then applies the configured <see cref="PanZoomBehaviourOnNavigation"/>.
    /// </summary>
    private void ApplyNavigationView(string photoPath, Size imageSize, int imageRotation, Size canvasSize, bool isNewPhoto)
    {
        // Cancel any in-flight animation so its stale internal state can't clobber the freshly-applied view.
        StopAnimationSnappingToTarget();

        switch (AppConfig.Settings.PanZoomBehaviourOnNavigation)
        {
            case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                // Establish the EXIF baseline before TryRestore re-bases the stored user-rotation.
                _canvasViewState.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
                _originalImageRotation = imageRotation;

                if (_viewStore.TryRestore(photoPath, _originalImageRotation, canvasSize, out var restored))
                    ApplyRestoredView(imageSize, canvasSize, restored);
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

    /// <summary>
    /// Sets the default view for a photo by fitting it to screen or 100% for smaller photos.
    /// </summary>
    private void SetDefaultView(Size imageSize, int imageRotation, Size canvasSize, bool isFirstPhotoEver)
    {
        var defaultFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, imageRotation);
        var initialScale = AppConfig.Settings.StretchSmallImages ? defaultFitScale : Math.Min(defaultFitScale, 1.0f);

        _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = initialScale;
        _canvasViewState.Rotation = imageRotation; // Ensure rotation is set correctly for default view.

        // Guard: the spring drives log(scale), so a zero/negative fit scale (canvas not yet sized)
        // would feed log(0) = -infinity into the integrator. Only animate when the target is valid.
        if (isFirstPhotoEver && AppConfig.Settings.OpenExitZoom && initialScale > 0)
        {
            _canvasViewState.Scale = 0.01f;
            _suppressZoomUpdateForNextAnimation = true;
            // forceReseed: the open-zoom is a hard reset to 0.01 — it must seed from that scale even if a
            // spring is already mid-flight, otherwise it inherits stale internal state and springs from
            // ~100% down to fit instead of growing up from tiny.
            StartSpringPanAndZoomAnimation(initialScale, new Point(canvasSize.Width / 2, canvasSize.Height / 2),
                canvasSize, forceReseed: true);
        }
        else
        {
            _canvasViewState.Scale = initialScale;
        }
        _canvasViewState.UpdateTransform();
        IsFittedToScreen = Math.Abs(initialScale - defaultFitScale) < ScaleTolerance;
        IsAtOneToOne = Math.Abs(initialScale - 1.0f) < ScaleTolerance;
        _isStateModifiedByUser = false; // This is a default state, not a user-modified one.
    }

    /// <summary>
    /// Applies a remembered view (de-normalized by <see cref="PerPhotoViewStore"/> for the current canvas and
    /// EXIF baseline) onto the current photo. The rotation has already been re-based onto
    /// <see cref="_originalImageRotation"/>, so Rotation and the baseline stay consistent and a subsequent
    /// Preview → HQ upgrade's rotation delta nets to zero even when the preview and HQ baselines differ.
    /// </summary>
    private void ApplyRestoredView(Size imageSize, Size canvasSize, PerPhotoViewStore.RestoredView restored)
    {
        _canvasViewState.Scale = restored.Scale;
        _canvasViewState.LastScaleTo = restored.LastScaleTo;
        _canvasViewState.Rotation = restored.Rotation;
        _canvasViewState.ImagePos = restored.ImagePos;

        // Recalculate fitted/1:1 flags. Must check both scale AND pan: a panned-at-fit-scale photo is not "fitted".
        var fitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(_canvasViewState.Scale - fitScale) < ScaleTolerance && restored.PanIsCentered;
        IsAtOneToOne = Math.Abs(_canvasViewState.Scale - 1.0f) < ScaleTolerance;
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
            var newScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
            IsAtOneToOne = Math.Abs(newScale - 1.0f) < ScaleTolerance;
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
            var newScale = ZoomGeometry.CalculateScreenFitScale(newSize, imageSize, _canvasViewState.Rotation);

            _canvasViewState.Scale = newScale;
            _canvasViewState.LastScaleTo = newScale;
            _canvasViewState.ImagePos = new Point(newSize.Width / 2, newSize.Height / 2);

            _canvasViewState.UpdateTransform();
            ViewChanged?.Invoke();
            ZoomChanged?.Invoke();
            IsAtOneToOne = Math.Abs(newScale - 1.0f) < ScaleTolerance;
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
    /// Saves the current view for <paramref name="photoPath"/> if "RememberPerPhoto" is enabled and the
    /// user has actually modified the view (panned, zoomed, or rotated). Pan is stored normalized to the
    /// canvas size and rotation relative to the EXIF baseline — see <see cref="PerPhotoViewStore"/>.
    /// </summary>
    public void CacheCurrentViewState(string photoPath, Size canvasSize)
    {
        if (AppConfig.Settings.PanZoomBehaviourOnNavigation != PanZoomBehaviourOnNavigation.RememberPerPhoto
            || !_isStateModifiedByUser)
            return;

        _viewStore.Save(photoPath, _canvasViewState.Scale, _canvasViewState.LastScaleTo,
            _canvasViewState.ImagePos, _canvasViewState.Rotation, _originalImageRotation, canvasSize);
    }

    /// <summary>
    /// Checks if the current view state matches the photo's default view. If it does,
    /// it resets the modified flag and removes the photo from the cache.
    /// Otherwise, it marks the state as user-modified.
    /// </summary>
    private void CheckIfViewBackToDefaultState(float finalScale, Size canvasSize, Size imageSize)
    {
        // 1. Calculate the scale of the *actual* default view (which doesn't upscale small images).
        var defaultFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _originalImageRotation);
        var defaultInitialScale = AppConfig.Settings.StretchSmallImages ? defaultFitScale : Math.Min(defaultFitScale, 1.0f);

        // 2. Check if the final scale from the user action matches the default scale.
        var isDefaultScale = Math.Abs(finalScale - defaultInitialScale) < ScaleTolerance;

        // 3. Check if the current rotation matches the original, unmodified rotation.
        // The modulo logic handles cases like 360 vs 0 and -90 vs 270.
        var currentRotationNormalized = ((_canvasViewState.Rotation % 360) + 360) % 360;
        var originalRotationNormalized = ((_originalImageRotation % 360) + 360) % 360;
        var isDefaultRotation = currentRotationNormalized == originalRotationNormalized;

        // 4. Check if the pan position is centered on the canvas. A tolerance of 1 pixel covers float
        // inaccuracy AND the ≤0.5 px pixel-grid alignment applied to the settled fit/centre position
        // (the resting ImagePos can sit just off exact centre so the composed translation lands on the grid).
        var canvasCenterX = canvasSize.Width / 2.0;
        var canvasCenterY = canvasSize.Height / 2.0;
        var isDefaultPan = Math.Abs(_canvasViewState.ImagePos.X - canvasCenterX) < 1.0 &&
                           Math.Abs(_canvasViewState.ImagePos.Y - canvasCenterY) < 1.0;

        // 5. The view is considered "default" only if scale, rotation, AND pan match the default state.
        if (isDefaultScale && isDefaultRotation && isDefaultPan)
        {
            _isStateModifiedByUser = false;
            if (!string.IsNullOrEmpty(_photoPath))
                _viewStore.Remove(_photoPath);
        }
        else { _isStateModifiedByUser = true; }
    }

    // --- Zoom & pan ---

    /// <summary>
    /// Performs a standard zoom operation anchored at a specific point (e.g., mouse cursor).
    /// </summary>
    public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
    {
        var scalePercentage = (zoomDirection == ZoomDirection.In) ? 1.25f : 0.8f;
        var rawScaleTo = _canvasViewState.LastScaleTo * scalePercentage;
        var scaleTo = AppConfig.Settings.StickyZoomLevels
            ? ZoomGeometry.ApplyZoomSnap(rawScaleTo, _canvasViewState.LastScaleTo, zoomDirection)
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
        // 2. Calculate the new position so the cursor anchor stays put as the scale changes.
        var newPos = ZoomGeometry.AnchorPreservingPan(zoomAnchor, _canvasViewState.ImagePos, oldScale, newScale);
        // 3. Now, update the state with the new values.
        _canvasViewState.Scale = newScale;
        _canvasViewState.LastScaleTo = newScale; // Keep LastScaleTo in sync
        _canvasViewState.ImagePos = newPos;
        // 4. Update transform and notify for redraw.
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
        ZoomChanged?.Invoke();

        // Any manual zoom action invalidates both fitted and 1:1 states.
        IsFittedToScreen = false;
        IsAtOneToOne = false;
        _isStateModifiedByUser = true;
    }

    public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize) =>
        ZoomAtPoint(zoomDirection, new Point(canvasSize.Width / 2, canvasSize.Height / 2));

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
        var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        var zoomStops = ZoomGeometry.BuildZoomStops(screenFitScale);

        // 2. Find the index of the next logical stop based on the current scale and direction.
        const float tolerance = ScaleTolerance;
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
            StartSpringPanAndZoomAnimation(targetScale, anchor, canvasSize);

        // Set state immediately for responsive UI.
        IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < ScaleTolerance;
        IsAtOneToOne = Math.Abs(targetScale - 1.0f) < ScaleTolerance;
        _isStateModifiedByUser = true;
    }

    /// <summary>
    /// Explicit user action to set zoom to 100%.
    /// </summary>
    public void ZoomToHundred(Size canvasSize)
    {
        const float targetScale = 1.0f;
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = 1.0f;
        StartSpringPanAndZoomAnimation(1.0f, targetPosition, canvasSize);

        // Set state immediately. The view is fitted only if 100% happens to be the fit scale.
        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(1.0f - screenFitScale) < ScaleTolerance;
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
        // screen coordinate after scaling to 1:1.
        var oldScale = _canvasViewState.Scale;
        // If already at 1:1, just center on anchor
        if (Math.Abs(oldScale - targetScale) < 0.0001f)
        {
            StartSpringPanAndZoomAnimation(targetScale, anchor, canvasSize);
        }
        else
        {
            var targetPos = ZoomGeometry.AnchorPreservingPan(anchor, _canvasViewState.ImagePos, oldScale, targetScale);
            StartSpringPanAndZoomAnimation(targetScale, targetPos, canvasSize);
        }

        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(targetScale - screenFitScale) < ScaleTolerance;
        IsAtOneToOne = true;
        _isStateModifiedByUser = true;
        CheckIfViewBackToDefaultState(targetScale, canvasSize, imageSize);
    }

    /// <summary>
    /// Performs the zoom-out animation when closing the application.
    /// </summary>
    public void ZoomOutOnExit(double exitAnimationDuration, Size canvasSize)
    {
        // exitAnimationDuration is retained for API/caller compatibility but no longer drives a
        // fixed-duration tween — the spring settles on its own. In log space the shrink reaches a
        // sub-pixel dot well within the caller's close deadline (PanZoomAnimationDurationForExit * 2).
        _ = exitAnimationDuration;
        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _suppressZoomUpdateForNextAnimation = true;
        StartSpringPanAndZoomAnimation(0.001f, targetPosition, canvasSize);
        IsFittedToScreen = false;
        IsAtOneToOne = false;
    }

    /// <summary>
    /// Explicit user action to fit the image to the screen. Allows upscaling of small images.
    /// </summary>
    public void ZoomPanToFit(bool animateChange, Size imageSize, Size canvasSize)
    {
        // This is the EXPLICIT user action, so it DOES upscale small images.
        var scaleFactor = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);

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
            IsAtOneToOne = Math.Abs(scaleFactor - 1.0f) < ScaleTolerance;
            return;
        }

        var targetPosition = new Point(canvasSize.Width / 2, canvasSize.Height / 2);
        _canvasViewState.LastScaleTo = scaleFactor;
        StartSpringPanAndZoomAnimation(scaleFactor, targetPosition, canvasSize);

        // Set state immediately for responsive UI, even when animating.
        IsFittedToScreen = true;
        IsAtOneToOne = Math.Abs(scaleFactor - 1.0f) < ScaleTolerance;
        _isStateModifiedByUser = true;
        CheckIfViewBackToDefaultState(scaleFactor, canvasSize, imageSize);
    }

    /// <summary>
    /// Pans the image by the specified delta.
    /// </summary>
    public void Pan(double dx, double dy)
    {
        if (_activeAnimation != null)
        {
            // A drag arriving mid-animation must not be discarded by the next Tick. Fold the delta into the
            // active animation's own pan state so the image follows the cursor while the animation (e.g. a
            // wheel-zoom spring) keeps converging — instead of the pan flickering and snapping back.
            _activeAnimation.NudgePan(dx, dy);
        }
        else
        {
            _canvasViewState.ImagePos.X += dx;
            _canvasViewState.ImagePos.Y += dy;
            _canvasViewState.UpdateTransform();
        }
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

    // --- Animation orchestration ---

    /// <summary>
    /// Called by CanvasController on the Win2D background thread each Update tick.
    /// Advances whichever pan/zoom/shrug animation is currently active.
    /// </summary>
    public void OnUpdate() => _activeAnimation?.Tick();

    /// <summary>Installs <paramref name="animation"/> as the active one and turns off pixel snapping for its duration.</summary>
    private void BeginAnimation(IViewAnimation animation)
    {
        _activeAnimation = animation;
        _canvasViewState.SnapTranslation = false; // smooth sub-pixel motion while animating (no 1px shiver)
    }

    /// <summary>Clears the active animation and re-enables pixel snapping (callers rebuild the transform after).</summary>
    private void ClearActiveAnimation()
    {
        _activeAnimation = null;
        _canvasViewState.SnapTranslation = true;
    }

    /// <summary>
    /// Starts (or re-targets) a pure spring zoom, keeping the anchor point stationary on screen.
    /// If a spring is already running, only the target/anchor change — scale velocity carries forward,
    /// so rapid wheel notches redirect smoothly without a restart lurch.
    /// </summary>
    private void StartZoomAnimation(float targetScale, Point zoomAnchor)
    {
        // Seed the (shared) scale spring only if no spring is running — a re-target keeps its velocity. Detect
        // a fresh anchored zoom (vs. a re-target of one already running) BEFORE swapping _active, so the anchor
        // track is reset only when starting fresh and kept on a re-target (so the grid offset can't feed back).
        SeedScaleSpringIfFresh();
        if (_activeAnimation is AnchoredZoomAnimation zoom)
        {
            zoom.Aim(targetScale, zoomAnchor, resetAnchorTrack: false);
        }
        else
        {
            zoom = new AnchoredZoomAnimation(this);
            zoom.Aim(targetScale, zoomAnchor, resetAnchorTrack: true);
            BeginAnimation(zoom);
        }
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Starts (or re-targets) a spring that drives scale and pan together (fit / 100% / step).
    /// Velocity carries forward across re-targets, as with <see cref="StartZoomAnimation"/>.
    /// </summary>
    /// <param name="targetScale">The scale the spring should settle at.</param>
    /// <param name="targetPosition">The image-centre position the spring should settle at.</param>
    /// <param name="targetCanvasSize">Canvas size used to compute the settled layout.</param>
    /// <param name="forceReseed">
    /// When true, the spring's internal scale AND pan state are re-seeded from the live view even if a
    /// spring is already running, and velocities are zeroed. Used by the launch open-zoom, which is a
    /// deliberate reset to a known start scale (0.01) — NOT a re-target — so it must not inherit a
    /// previous spring's internal state (which would otherwise render from the stale/default scale and
    /// spring from ~100% down to fit instead of growing up from tiny).
    /// </param>
    private void StartSpringPanAndZoomAnimation(float targetScale, Point targetPosition, Size targetCanvasSize, bool forceReseed = false)
    {
        SeedScaleSpringIfFresh(forceReseed);
        // Pan state must be seeded whenever the previous animation wasn't already a pan spring (e.g. coming
        // from an anchored zoom, which doesn't track pan velocity) — or always on a forced reseed.
        var reseedPan = forceReseed || _activeAnimation is not PanZoomAnimation;
        var panZoom = _activeAnimation as PanZoomAnimation ?? new PanZoomAnimation(this);
        panZoom.Aim(targetScale, targetPosition, targetCanvasSize, reseedPan);
        BeginAnimation(panZoom);
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Seeds the scale spring's internal state from the live view. When a spring is already running the
    /// scale position/velocity are normally preserved for seamless re-targeting; pass
    /// <paramref name="force"/> = true to override that (a deliberate reset, e.g. the launch open-zoom).
    /// </summary>
    private void SeedScaleSpringIfFresh(bool force = false)
    {
        if (!force && _activeAnimation is AnchoredZoomAnimation or PanZoomAnimation) return;
        _scaleSpring.Reset((float)Math.Log(_canvasViewState.Scale));
        _animationStopwatch.Restart();
        _lastSpringElapsedMs = 0;
    }

    /// <summary>
    /// Starts the shrug/shake animation.
    /// </summary>
    private void StartShrugAnimation()
    {
        var shrug = new ShrugAnimation(this);
        shrug.Start();
        _animationStopwatch.Restart();
        BeginAnimation(shrug);
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
    /// Re-bases an in-flight spring's scale by <paramref name="scaleFactor"/> after a resolution change
    /// (Preview → HQ). The displayed scale is rescaled by the caller; this keeps the spring's internal scale
    /// state and target consistent so it keeps converging to the same *visual* zoom without a jump. Pan is in
    /// screen space and so is unaffected. Only ever called while a spring is active (guarded by the caller).
    /// </summary>
    private void RebaseAnimationScale(float scaleFactor)
    {
        if (scaleFactor <= 0f) return;
        _zoomTargetScale *= scaleFactor;
        _scaleSpring.Position += (float)Math.Log(scaleFactor);
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
        // Re-enable snapping FIRST so CompleteImmediately's UpdateTransform builds a snapped, device-pixel
        // aligned resting frame. CompleteImmediately is a no-op for a shrug (it has no settled target).
        _canvasViewState.SnapTranslation = true;
        _activeAnimation?.CompleteImmediately();
        _animationStopwatch.Stop();
        _activeAnimation = null;
        _suppressZoomUpdateForNextAnimation = false;
    }

    // --- IAnimationHost ---
    // The slice of this class the IViewAnimation strategies drive each frame (see IViewAnimation).

    CanvasViewState IAnimationHost.View => _canvasViewState;
    SpringAxis IAnimationHost.ScaleSpring => _scaleSpring;
    SpringAxis IAnimationHost.PanXSpring => _panXSpring;
    SpringAxis IAnimationHost.PanYSpring => _panYSpring;
    double IAnimationHost.ElapsedMs => _animationStopwatch.Elapsed.TotalMilliseconds;

    float IAnimationHost.TargetScale
    {
        get => _zoomTargetScale;
        set => _zoomTargetScale = value;
    }

    bool IAnimationHost.TryGetDt(out float dt) => TryGetSpringDt(out dt);

    void IAnimationHost.RaiseViewChanged() => ViewChanged?.Invoke();

    void IAnimationHost.RaiseZoomChanged()
    {
        if (!_suppressZoomUpdateForNextAnimation) ZoomChanged?.Invoke();
    }

    void IAnimationHost.ReportSettledFit(Size canvasSize)
    {
        // Final "truth-setter" for the fitted / 1:1 flags from the settled scale, using the stored canvas size.
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0) return;
        var imageSize = new Size(_canvasViewState.ImageRect.Width, _canvasViewState.ImageRect.Height);
        var screenFitScale = ZoomGeometry.CalculateScreenFitScale(canvasSize, imageSize, _canvasViewState.Rotation);
        IsFittedToScreen = Math.Abs(_zoomTargetScale - screenFitScale) < ScaleTolerance;
        IsAtOneToOne = Math.Abs(_zoomTargetScale - 1.0f) < ScaleTolerance;
    }

    void IAnimationHost.FinishSpring()
    {
        _animationStopwatch.Stop();
        _suppressZoomUpdateForNextAnimation = false;
        // Re-enable snapping, then rebuild the transform so the final resting frame lands on the device-pixel
        // grid (crisp 1:1 / NVIDIA fix). The animation already wrote the exact target Scale/ImagePos.
        ClearActiveAnimation();
        _canvasViewState.UpdateTransform();
        AnimationCompleted?.Invoke();
    }

    void IAnimationHost.FinishShrug()
    {
        _animationStopwatch.Stop();
        // Re-enable snapping BEFORE the rebuild; the animation already restored the exact start position.
        ClearActiveAnimation();
        _canvasViewState.UpdateTransform();
        ViewChanged?.Invoke();
    }

    // --- Cleanup ---

    /// <summary>
    /// Cleans up resources.
    /// </summary>
    public void Dispose()
    {
        ClearActiveAnimation(); // drop the active animation and re-enable snapping
        _animationStopwatch.Stop();
    }
}
