using System;
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
/// How newly installed content relates to what was on screen. Worked out once per install, on the UI thread and in
/// install order, by <see cref="CanvasController.ClassifyArrival"/>; it picks the view rule in
/// <see cref="CanvasViewManager.SetScaleAndPosition"/>.
/// </summary>
internal enum ViewArrival
{
    /// <summary>The very first photo since launch: a clean default view (with the open-zoom, if it is on).</summary>
    FirstPhoto,

    /// <summary>Another photo than the one on screen: the navigation setting decides the view.</summary>
    NewPhoto,

    /// <summary>The first real image of the photo on screen, after its loading placeholder: also navigation.</summary>
    FirstRealImage,

    /// <summary>
    /// A better image of the photo on screen (preview → HQ, a reload, an animator after its first frame): the view
    /// on screen is kept.
    /// </summary>
    SameImageUpgrade
}

/// <summary>
/// Owns the canvas view — scale (zoom), pan position and rotation, in <see cref="CanvasViewState"/> — and animates
/// changes to it. Every public method turns a user action (zoom at point, fit, 100%, pan, rotate, …) or a lifecycle
/// event (photo installed, resize, page change, exit) into an update of the view, and fires events so the canvas
/// redraws and the UI (zoom %, fit / 1:1 buttons) follows. The math lives in <see cref="ZoomGeometry"/>; this class
/// holds the state and decides which rule applies when.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every method runs on the Win2D render ("W2D") thread — UI-thread callers go through
/// <c>CanvasController</c>'s action queue — and <see cref="OnUpdate"/> is ticked once per frame there. No locking:
/// the class is single-threaded.</para>
///
/// <para><b>What happens when content is installed</b> (<see cref="SetScaleAndPosition"/>, by
/// <see cref="ViewArrival"/>):
/// <list type="table">
///   <item><term>FirstPhoto</term><description>Default view (fit, never above 100% unless stretching), plus the
///     open-zoom if it is on.</description></item>
///   <item><term>NewPhoto, FirstRealImage</term><description>Any animation is first jumped to its end, then the
///     <see cref="PanZoomBehaviourOnNavigation"/> setting: <b>Reset</b> → default view. <b>RememberPerPhoto</b> →
///     the stored view (<see cref="PerPhotoViewStore"/>) or the default. <b>RetainFromLastPhoto</b> → fitted stays
///     fitted; otherwise the absolute scale is kept and the pan offset scales with the image size
///     (<see cref="ZoomGeometry.RetainedPan"/>); rotation is the new photo's EXIF rotation, or after a placeholder
///     the user's rotation carried over.</description></item>
///   <item><term>SameImageUpgrade</term><description>The view on screen is kept: the pan stays, the rotation moves
///     onto the new EXIF baseline, and the scale is multiplied by <see cref="ZoomGeometry.UpgradeScaleRatio"/>
///     (fitted stays fitted; otherwise the on-screen height is kept). A running zoom keeps running, rescaled, so the
///     HQ never makes it jump.</description></item>
/// </list></para>
///
/// <para><b>The flags.</b> <see cref="IsFittedToScreen"/> is an <i>intent</i>, not a computed check: "this view
/// follows the fit". Resize, upgrade and Retain re-fit while it is set; a pan, a rotation or an anchored zoom clears
/// it even where the geometry still happens to match. <see cref="IsAtOneToOne"/> follows the target scale (pan doesn't
/// affect it). Both drive the toolbar buttons through their events.</para>
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
/// the shared spring bank, frame clock, and all event / snapping / completion plumbing; the target scale is
/// <see cref="CanvasViewState.TargetScale"/>. Pan, rotate, and precision-touchpad zoom are applied directly.</para>
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
///     WRITES scale/pan into <see cref="CanvasViewState"/> each tick and never reads them back, so an external
///     mid-flight write is overwritten on the next frame. Anything that changes the view while an animation runs
///     must therefore fold into it (<see cref="IViewAnimation.NudgePan"/>, re-aiming, rebasing the scale spring) or
///     jump it to its end first (<see cref="StopAnimationSnappingToTarget"/>). State is seeded from the live view
///     only when starting fresh (<see cref="SeedScaleSpringIfFresh"/>); a running spring keeps its velocity so
///     re-targets (e.g. rapid wheel notches) stay continuous instead of restarting with a lurch.</item>
/// </list></para>
///
/// <para><b>Launch open-zoom</b> force-reseeds the spring from 0.01 (a deliberate reset, guarded on a valid fit
/// scale so it never feeds <c>log(0)</c>). <b>Exit zoom-out</b> springs toward ~0; the caller closes the window on a
/// short wait (see <see cref="Constants.PanZoomAnimationDurationForExit"/>), since full spring-settle (~0.6 s) is
/// far later than when the image visually vanishes.</para>
///
/// <para><b>Tuning</b> lives entirely in <see cref="Constants"/> (each spring constant documents the effect
/// of raising/lowering it). A fuller write-up is in <c>docs/02_study/20260609_pan_zoom_physics.md</c>.</para>
/// </remarks>
internal class CanvasViewManager : IAnimationHost
{
    // --- Fields ---

    private readonly CanvasViewState _view;
    /// <summary>The photo on screen; its view is remembered under this path when the next one arrives.</summary>
    private string _photoPath = string.Empty;

    /// <summary>The size of <see cref="CanvasViewState.ImageRect"/>, kept so zoom input doesn't allocate one per event.</summary>
    private Size _imageSize = new(0, 0);

    /// <summary>The EXIF rotation of the image on screen; <see cref="CanvasViewState.Rotation"/> minus it is the user's.</summary>
    private int _exifRotation;

    /// <summary>
    /// Per-session memory of user-modified views for "RememberPerPhoto" mode (see <see cref="PerPhotoViewStore"/>).
    /// Written on navigate-away (<see cref="RememberViewOnScreen"/>), read on revisit (<see cref="ApplyRestoredView"/>).
    /// </summary>
    private readonly PerPhotoViewStore _viewStore = new();

    /// <summary>The user changed the view (pan, zoom, rotate) since it was last set to a default; worth remembering.</summary>
    private bool _viewChangedByUser;

    /// <summary>
    /// True while a multi-page image shows a page whose pixel size differs from page 1's. A revisit always
    /// reopens at page 1, and the remembered scale is absolute, so a view made here must not be cached —
    /// a 10x zoom on an ICO's 16x16 frame would come back as a 10x zoom on its 256x256 frame.
    /// </summary>
    private bool _isOnResizedPage;

    // Animation substrate. At most one animation is active (null at rest); its concrete type says which kind. The
    // animation owns its type-specific geometry; the shared spring bank and clock stay here so velocity and clock
    // carry continuously across re-targets AND across animation types.
    private IViewAnimation _activeAnimation;
    private readonly Func<double> _nowMs;   // the animation clock, in ms
    private double _animationStartMs;       // _nowMs() when the current animation's clock was (re)started
    private double _lastSpringElapsedMs;
    private readonly SpringAxis _scaleSpring = new();   // springs LOG(scale)
    private readonly SpringAxis _panXSpring = new();    // springs ImagePos.X (pixels)
    private readonly SpringAxis _panYSpring = new();    // springs ImagePos.Y (pixels)

    private const float OpenZoomStartScale = 0.01f;
    private const float ExitZoomTargetScale = 0.001f;

    // --- Events ---

    /// <summary>Fires when the view's "fitted to screen" state changes.</summary>
    public event Action<bool> FitToScreenStateChanged;

    /// <summary>Fires when the view's "100% zoom" state changes.</summary>
    public event Action<bool> OneToOneStateChanged;

    /// <summary>
    /// Fires when the zoom readout should show the zoom: the user zoomed, a resize re-fitted, a page changed size.
    /// Not on navigation or a quality upgrade (the readout pops up when it fires).
    /// </summary>
    public event Action ZoomChanged;

    /// <summary>Fires when the canvas needs a redraw.</summary>
    public event Action ViewChanged;

    /// <summary>Fires when a pan/zoom animation settles (not when one is cut short).</summary>
    public event Action AnimationCompleted;

    // --- Properties ---

    /// <summary>
    /// Indicates if a pan or zoom animation is currently in progress (i.e. an <see cref="IViewAnimation"/> is
    /// active). Read by <c>CanvasController</c> to decide whether to keep ticking the render loop.
    /// </summary>
    /// <remarks>
    /// Pixel snapping is the inverse of this: <see cref="BeginAnimation"/> turns
    /// <see cref="CanvasViewState.SnapTranslation"/> off for the duration and <see cref="ClearActiveAnimation"/>
    /// turns it back on, so snapping is on exactly when no animation is running. Stop paths must rebuild the
    /// transform (<c>UpdateTransform()</c>) AFTER clearing so the resting frame is rebuilt with rounding applied.
    /// </remarks>
    public bool PanZoomAnimationOnGoing => _activeAnimation != null;

    /// <summary>"The view follows the fit" (see the remarks). Fires <see cref="FitToScreenStateChanged"/> on a change.</summary>
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

    /// <summary>The (target) scale is 100%. Fires <see cref="OneToOneStateChanged"/> on a change.</summary>
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

    /// <param name="view">The view this class owns and changes.</param>
    /// <param name="nowMs">The animation clock in milliseconds; the high-resolution timer by default. Tests step
    /// it by hand so animations run frame by frame, independent of real time.</param>
    public CanvasViewManager(CanvasViewState view, Func<double> nowMs = null)
    {
        _view = view;
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    // --- Small helpers ---

    private float FitScale(Size canvasSize) =>
        ZoomGeometry.CalculateScreenFitScale(canvasSize, _imageSize, _view.Rotation);

    private void SetImageSize(Size imageSize)
    {
        _imageSize = imageSize;
        _view.ImageRect = new Rect(0, 0, imageSize.Width, imageSize.Height);
    }

    /// <summary>1:1 follows the target scale; call after every write of <see cref="CanvasViewState.TargetScale"/>.</summary>
    private void UpdateOneToOne() => IsAtOneToOne = ZoomGeometry.SameScale(_view.TargetScale, 1f);

    /// <summary>Puts the view at rest at the fit for <paramref name="canvasSize"/>, centred, and marks it fitted.</summary>
    private void ApplyFitted(Size canvasSize)
    {
        _view.Scale = _view.TargetScale = FitScale(canvasSize);
        _view.ImagePos = ZoomGeometry.Centre(canvasSize);
        _view.UpdateTransform();
        IsFittedToScreen = true;
        UpdateOneToOne();
    }

    /// <summary>Jumps any animation to its end and re-fits at once, showing the zoom readout.</summary>
    private void RefitNow(Size canvasSize)
    {
        StopAnimationSnappingToTarget(); // a running animation would overwrite the fit on its next frame
        ApplyFitted(canvasSize);
        ViewChanged?.Invoke();
        ZoomChanged?.Invoke();
    }

    /// <summary>Animates to the fit for <paramref name="canvasSize"/>, centred (the caller sets the fitted flag).</summary>
    private void AnimateToFit(Size canvasSize)
    {
        StartPanZoomAnimation(FitScale(canvasSize), ZoomGeometry.Centre(canvasSize), canvasSize);
        UpdateOneToOne();
    }

    // --- Content installed ---

    /// <summary>
    /// Applies the view for newly installed content (<paramref name="imageSize"/> in layout pixels, its
    /// <paramref name="exifRotation"/>), by how it <paramref name="arrival"/>d — see the remarks.
    /// </summary>
    public void SetScaleAndPosition(string photoPath, Size imageSize, int exifRotation, Size canvasSize,
        ViewArrival arrival)
    {
        // The outgoing photo's view first, while the state still describes it.
        if (arrival == ViewArrival.NewPhoto) RememberViewOnScreen(canvasSize);

        var exifDelta = exifRotation - _exifRotation;
        _exifRotation = exifRotation;
        _photoPath = photoPath;
        _isOnResizedPage = false; // new content always installs at page 1

        switch (arrival)
        {
            case ViewArrival.FirstPhoto:
                SetImageSize(imageSize);
                ApplyDefaultView(exifRotation, canvasSize, openZoom: true);
                break;
            case ViewArrival.SameImageUpgrade:
                ApplyQualityUpgrade(imageSize, exifDelta, canvasSize);
                break;
            default:
                ApplyNavigationView(photoPath, imageSize, exifRotation, exifDelta, canvasSize, arrival);
                break;
        }

        ViewChanged?.Invoke();
    }

    /// <summary>
    /// Re-fits the view for a new page of the current multi-page image. Only called when the page
    /// actually changed size (ICO frames run 256 px down to 16 px; TIFF pages are normally uniform and
    /// keep the user's zoom/pan instead). Rotation is carried over from the live view rather than the
    /// EXIF baseline, so a manual rotation survives the page change.
    /// </summary>
    public void SetPageSize(Size imageSize, Size canvasSize, bool isFirstPage)
    {
        _isOnResizedPage = !isFirstPage;
        StopAnimationSnappingToTarget();
        SetImageSize(imageSize);
        ApplyDefaultView(_view.Rotation, canvasSize, openZoom: false);
        ViewChanged?.Invoke();
        ZoomChanged?.Invoke(); // 256 px -> 16 px moves the scale by up to 16x; the readout must follow
    }

    /// <summary>
    /// A better image of the photo on screen (Preview → HQ, a reload) must keep the live view exactly — never reset
    /// or jump, even when a zoom/pan animation is still in flight as the HQ arrives. The pan stays fixed in canvas
    /// coordinates, the rotation is re-based onto the new EXIF baseline, and the scale is multiplied by
    /// <see cref="ZoomGeometry.UpgradeScaleRatio"/>. (The image-size ratio of <see cref="ZoomGeometry.RetainedPan"/>
    /// must not be applied here: a preview can be 10–15× smaller than the HQ, turning a 300 px pan into 3000+ px.)
    /// </summary>
    private void ApplyQualityUpgrade(Size imageSize, int exifDelta, Size canvasSize)
    {
        var animating = IsSpringRunning;
        var oldImageSize = _imageSize;
        var oldRotation = _view.Rotation;

        SetImageSize(imageSize);
        _view.Rotation += exifDelta;
        if (!animating && IsFittedToScreen)
        {
            ApplyFitted(canvasSize);
            return;
        }

        // Rescale the shown scale and the target by the same ratio, so the image keeps its size on screen. A running
        // spring is rebased too, so it keeps converging (re-fitting here would visibly reset mid-animation); pan is in
        // screen space, so the animation's pan state stays valid.
        var ratio = ZoomGeometry.UpgradeScaleRatio(canvasSize, oldImageSize, oldRotation, imageSize, _view.Rotation,
            IsFittedToScreen);
        _view.Scale *= ratio;
        _view.TargetScale *= ratio;
        if (animating && ratio > 0f) _scaleSpring.Position += (float)Math.Log(ratio);
        _view.UpdateTransform();
        UpdateOneToOne(); // a zoom heading to 100% is 1:1
    }

    /// <summary>
    /// Navigation to genuinely new content (a new photo, or the first real image after a placeholder): jumps any
    /// in-flight animation to its end, then applies the configured <see cref="PanZoomBehaviourOnNavigation"/>.
    /// </summary>
    private void ApplyNavigationView(string photoPath, Size imageSize, int exifRotation, int exifDelta,
        Size canvasSize, ViewArrival arrival)
    {
        // Jump the animation to its end, so its stale state can't clobber the new view and Retain inherits the
        // view the user was heading to.
        StopAnimationSnappingToTarget();
        var oldImageSize = _imageSize;
        SetImageSize(imageSize);

        switch (AppConfig.Settings.PanZoomBehaviourOnNavigation)
        {
            case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                if (_viewStore.TryRestore(photoPath, exifRotation, canvasSize, out var restored))
                    ApplyRestoredView(restored, canvasSize);
                else
                    ApplyDefaultView(exifRotation, canvasSize, openZoom: false);
                break;

            case PanZoomBehaviourOnNavigation.Reset:
                ApplyDefaultView(exifRotation, canvasSize, openZoom: false);
                break;

            case PanZoomBehaviourOnNavigation.RetainFromLastPhoto:
                // A new photo takes its own EXIF rotation; after a placeholder the user's rotation carries over.
                _view.Rotation = arrival == ViewArrival.NewPhoto ? exifRotation : _view.Rotation + exifDelta;
                ApplyRetainedView(oldImageSize, canvasSize);
                break;
        }
    }

    /// <summary>
    /// The default view of the image laid out in <see cref="CanvasViewState.ImageRect"/>: centred at
    /// <see cref="ZoomGeometry.DefaultScale"/> with <paramref name="rotation"/>. With <paramref name="openZoom"/> (the
    /// first photo) and the setting on, it grows there from tiny instead.
    /// </summary>
    private void ApplyDefaultView(int rotation, Size canvasSize, bool openZoom)
    {
        _view.Rotation = rotation;
        var fitScale = FitScale(canvasSize);
        var scale = ZoomGeometry.DefaultScale(canvasSize, _imageSize, rotation);
        var centre = ZoomGeometry.Centre(canvasSize);
        _view.ImagePos = centre;
        _view.TargetScale = scale;

        // Guard: the spring drives log(scale), so a zero fit scale (canvas not yet sized) would feed log(0) to it.
        if (openZoom && AppConfig.Settings.OpenExitZoom && scale > 0)
        {
            _view.Scale = OpenZoomStartScale;
            // forceReseed: a hard reset to 0.01 — it must seed from that scale even if a spring is mid-flight,
            // otherwise it inherits stale state and springs from ~100% down to fit instead of growing from tiny.
            StartPanZoomAnimation(scale, centre, canvasSize, forceReseed: true).SuppressZoomReadout = true;
        }
        else
        {
            _view.Scale = scale;
        }
        _view.UpdateTransform();
        IsFittedToScreen = ZoomGeometry.SameScale(scale, fitScale);
        UpdateOneToOne();
        _viewChangedByUser = false;
    }

    /// <summary>
    /// Applies a remembered view (de-normalized by <see cref="PerPhotoViewStore"/> for the current canvas and EXIF
    /// baseline, which <see cref="_exifRotation"/> already holds, so a later Preview → HQ rotation delta nets to zero).
    /// </summary>
    private void ApplyRestoredView(PerPhotoViewStore.RestoredView restored, Size canvasSize)
    {
        _view.Scale = _view.TargetScale = restored.Scale;
        _view.Rotation = restored.Rotation;
        _view.ImagePos = restored.ImagePos;
        _view.UpdateTransform();
        // Fitted needs both the fit scale AND a centred pan: a panned-at-fit-scale photo is not "fitted".
        IsFittedToScreen = ZoomGeometry.SameScale(restored.Scale, FitScale(canvasSize)) && restored.PanIsCentered;
        UpdateOneToOne();
        _viewChangedByUser = true; // a remembered view is by definition one the user made
    }

    /// <summary>RetainFromLastPhoto: fitted stays fitted; otherwise the scale stays and the pan follows the image size.</summary>
    private void ApplyRetainedView(Size oldImageSize, Size canvasSize)
    {
        if (IsFittedToScreen)
        {
            ApplyFitted(canvasSize);
            return;
        }
        _view.ImagePos = ZoomGeometry.RetainedPan(_view.ImagePos, canvasSize, oldImageSize, _imageSize);
        _view.UpdateTransform();
    }

    // --- Window ---

    /// <summary>
    /// The canvas was resized. Fitted: re-fit to the new size. Otherwise the image keeps its place relative to the
    /// canvas (<see cref="ZoomGeometry.ResizedPan"/>).
    /// </summary>
    public void HandleSizeChange(Size newSize, Size previousSize)
    {
        // A pan/zoom spring is re-aimed at the new canvas and keeps going (the launch open-zoom still plays); any
        // other animation is jumped to its end first, as it would overwrite the resize on its next frame.
        if (_activeAnimation is PanZoomAnimation panZoom)
        {
            RetargetPanZoomForResize(panZoom, newSize, previousSize);
            return;
        }
        if (IsFittedToScreen)
        {
            RefitNow(newSize);
            return;
        }
        StopAnimationSnappingToTarget();
        _view.ImagePos = ZoomGeometry.ResizedPan(_view.ImagePos, previousSize, newSize);
        _view.UpdateTransform();
        ViewChanged?.Invoke();
    }

    /// <summary>
    /// The resize rules of <see cref="HandleSizeChange"/>, applied to a running pan/zoom spring's target instead of the
    /// view: a fitted target becomes the new canvas's fit and centre; any other keeps its scale and has its pan target
    /// moved with the canvas. The spring keeps its velocity and glides to the new target.
    /// </summary>
    private void RetargetPanZoomForResize(PanZoomAnimation panZoom, Size newSize, Size previousSize)
    {
        if (IsFittedToScreen)
            AnimateToFit(newSize);
        else
            StartPanZoomAnimation(_view.TargetScale, ZoomGeometry.ResizedPan(panZoom.PanTarget, previousSize, newSize),
                newSize);
    }

    /// <summary>
    /// Keeps the image at its screen position after the window was resized around it. The canvas
    /// origin moved by <paramref name="canvasOriginShift"/> physical pixels. If the window had to be
    /// clamped to the work area and a fitted image no longer fits, it is re-fitted instead.
    /// </summary>
    public void HandleImageSizedWindowResize(Point canvasOriginShift, Size newSize)
    {
        // Jump any animation to its end rather than freezing it mid-frame.
        StopAnimationSnappingToTarget();
        if (IsFittedToScreen && FitScale(newSize) < _view.Scale - ZoomGeometry.ScaleTolerance)
        {
            RefitNow(newSize);
            return;
        }

        _view.ImagePos.X -= canvasOriginShift.X;
        _view.ImagePos.Y -= canvasOriginShift.Y;
        _view.UpdateTransform();
        ViewChanged?.Invoke();
    }

    // --- Remembered views ---

    /// <summary>W2D thread. A photo was renamed: its remembered view, and the path of the one on screen, follow it.</summary>
    public void RenamePhoto(string oldPath, string newPath)
    {
        _viewStore.Rename(oldPath, newPath);
        if (_photoPath == oldPath) _photoPath = newPath;
    }

    /// <summary>
    /// Navigating away from the photo on screen: in "RememberPerPhoto" mode, saves its view if the user changed it
    /// and it is not back at the default, else forgets it. The view saved is the settled one, not a mid-animation
    /// frame. Pan is stored normalized to the canvas size and rotation relative to the EXIF baseline — see
    /// <see cref="PerPhotoViewStore"/>.
    /// </summary>
    private void RememberViewOnScreen(Size canvasSize)
    {
        if (AppConfig.Settings.PanZoomBehaviourOnNavigation != PanZoomBehaviourOnNavigation.RememberPerPhoto
            || _isOnResizedPage)
            return;

        // The navigation that follows jumps the animation to its end anyway; do it first so that is what's saved.
        StopAnimationSnappingToTarget();
        if (!_viewChangedByUser || IsDefaultView(canvasSize))
        {
            _viewStore.Remove(_photoPath);
            return;
        }
        _viewStore.Save(_photoPath, _view.Scale, _view.ImagePos, _view.Rotation, _exifRotation, canvasSize);
    }

    /// <summary>The view at rest is the photo's default: default scale, EXIF rotation, centred.</summary>
    private bool IsDefaultView(Size canvasSize) =>
        ZoomGeometry.SameScale(_view.Scale, ZoomGeometry.DefaultScale(canvasSize, _imageSize, _exifRotation))
        && ZoomGeometry.NormalizeDegrees(_view.Rotation) == ZoomGeometry.NormalizeDegrees(_exifRotation)
        && ZoomGeometry.IsCentred(_view.ImagePos, canvasSize);

    // --- Zoom & pan ---

    /// <summary>One wheel/keyboard zoom step, animated, keeping <paramref name="zoomAnchor"/> still on screen.</summary>
    public void ZoomAtPoint(ZoomDirection zoomDirection, Point zoomAnchor)
    {
        if (ZoomGeometry.WheelStepTarget(_view.TargetScale, zoomDirection, _imageSize) is not { } target) return;
        StartAnchoredZoom(target, zoomAnchor);

        // An anchored zoom is never "fitted" (the pan isn't centred); it is 1:1 when it lands on 100% (sticky zoom
        // stops there).
        IsFittedToScreen = false;
        UpdateOneToOne();
        _viewChangedByUser = true;
    }

    /// <summary>
    /// Immediately (without animation) multiplies the scale by <paramref name="scaleFactor"/>, keeping
    /// <paramref name="zoomAnchor"/> fixed on screen. Used by touchpad precision zoom, Shift + wheel fine
    /// zoom and touchscreen pinch. <paramref name="stopAtOneToOne"/> clamps a step that would cross 100% onto it.
    /// </summary>
    public void ZoomAtPointByFactor(float scaleFactor, Point zoomAnchor, bool stopAtOneToOne = false)
    {
        // A wheel zoom still animating would keep writing scale and pan over this step.
        StopAnimationSnappingToTarget();
        var from = _view.TargetScale;
        var scale = from * scaleFactor;
        if (stopAtOneToOne) scale = ZoomGeometry.StopOnCrossing(scale, from, 1f);
        if (scaleFactor < 1f && scale < ZoomGeometry.MinZoomOutScale(_imageSize)) return;

        _view.ImagePos = ZoomGeometry.AnchorPreservingPan(zoomAnchor, _view.ImagePos, _view.Scale, scale);
        _view.Scale = _view.TargetScale = scale;
        _view.UpdateTransform();
        ViewChanged?.Invoke();
        ZoomChanged?.Invoke();

        // An anchored zoom is never "fitted"; it is 1:1 when it lands on 100% (stopAtOneToOne stops there).
        IsFittedToScreen = false;
        UpdateOneToOne();
        _viewChangedByUser = true;
    }

    public void ZoomAtCenter(ZoomDirection zoomDirection, Size canvasSize) =>
        ZoomAtPoint(zoomDirection, ZoomGeometry.Centre(canvasSize));

    /// <summary>
    /// Animated zoom to the next stop (fit, 100%, 400%) in <paramref name="zoomDirection"/>. Centred, or keeping
    /// <paramref name="zoomAnchor"/> still when one is given.
    /// </summary>
    public void StepZoom(ZoomDirection zoomDirection, Size canvasSize, Point? zoomAnchor = null)
    {
        var fitScale = FitScale(canvasSize);
        if (ZoomGeometry.NextZoomStop(_view.TargetScale, fitScale, zoomDirection) is not { } target) return;

        if (zoomAnchor is { } anchor)
            StartAnchoredZoom(target, anchor);
        else
            StartPanZoomAnimation(target, ZoomGeometry.Centre(canvasSize), canvasSize);

        // Set state immediately for responsive UI. An anchored step keeps the cursor point still, so the pan
        // isn't centred and the view isn't "fitted" even at the fit scale.
        IsFittedToScreen = !zoomAnchor.HasValue && ZoomGeometry.SameScale(target, fitScale);
        UpdateOneToOne();
        _viewChangedByUser = true;
    }

    /// <summary>Explicit user action: zoom to 100%, centred.</summary>
    public void ZoomToHundred(Size canvasSize) => ZoomToHundredAt(ZoomGeometry.Centre(canvasSize), canvasSize);

    /// <summary>Explicit user action: zoom to 100%, keeping <paramref name="anchor"/> (screen pixels) still.</summary>
    public void ZoomToHundred(Size canvasSize, Point anchor) =>
        ZoomToHundredAt(ZoomGeometry.AnchorPreservingPan(anchor, _view.ImagePos, _view.Scale, 1f), canvasSize);

    private void ZoomToHundredAt(Point targetPos, Size canvasSize)
    {
        StartPanZoomAnimation(1f, targetPos, canvasSize);
        // Fitted only if 100% happens to be the fit AND the target is centred.
        IsFittedToScreen = ZoomGeometry.SameScale(1f, FitScale(canvasSize)) && ZoomGeometry.IsCentred(targetPos, canvasSize);
        UpdateOneToOne();
        _viewChangedByUser = true;
    }

    /// <summary>
    /// The zoom-out when closing the application. The spring settles on its own, reaching a sub-pixel dot well within
    /// the caller's close deadline (PanZoomAnimationDurationForExit * 2).
    /// </summary>
    public void ZoomOutOnExit(Size canvasSize)
    {
        StartPanZoomAnimation(ExitZoomTargetScale, ZoomGeometry.Centre(canvasSize), canvasSize).SuppressZoomReadout = true;
        IsFittedToScreen = false;
        UpdateOneToOne();
    }

    /// <summary>
    /// Explicit user action: fit the image to the screen, upscaling a small image too. Fits the image as laid out
    /// (<see cref="CanvasViewState.ImageRect"/>), which for a preview whose aspect ratio differs from the HQ's is not
    /// the photo's full size.
    /// </summary>
    public void ZoomPanToFit(bool animateChange, Size canvasSize)
    {
        if (!animateChange)
        {
            RefitNow(canvasSize);
            return;
        }

        AnimateToFit(canvasSize);
        IsFittedToScreen = true; // set at once for a responsive UI, even while animating
        _viewChangedByUser = true;
    }

    /// <summary>Moves the image by (<paramref name="dx"/>, <paramref name="dy"/>) screen pixels.</summary>
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
            _view.ImagePos.X += dx;
            _view.ImagePos.Y += dy;
            _view.UpdateTransform();
        }
        ViewChanged?.Invoke();

        IsFittedToScreen = false; // a pan breaks the fit; it doesn't change 1:1
        _viewChangedByUser = true;
    }

    /// <summary>Rotates the view by <paramref name="rotation"/> degrees (a multiple of 90).</summary>
    public void RotateBy(int rotation)
    {
        _view.Rotation += rotation;
        _view.UpdateTransform();
        ViewChanged?.Invoke();

        IsFittedToScreen = false; // the effective size changes; the scale (and so 1:1) does not
        _viewChangedByUser = true;
    }

    /// <summary>
    /// A small shake of the photo: an action was refused. Not played while another animation runs.
    /// </summary>
    public void Shrug()
    {
        if (PanZoomAnimationOnGoing) return;
        var shrug = new ShrugAnimation(this);
        shrug.Start();
        _animationStartMs = _nowMs();
        BeginAnimation(shrug);
        ViewChanged?.Invoke();
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
        _view.SnapTranslation = false; // smooth sub-pixel motion while animating (no 1px shiver)
    }

    /// <summary>Clears the active animation and re-enables pixel snapping (callers rebuild the transform after).</summary>
    private void ClearActiveAnimation()
    {
        _activeAnimation = null;
        _view.SnapTranslation = true;
    }

    /// <summary>
    /// Starts (or re-targets) a pure spring zoom, keeping the anchor point stationary on screen.
    /// If a spring is already running, only the target/anchor change — scale velocity carries forward,
    /// so rapid wheel notches redirect smoothly without a restart lurch.
    /// </summary>
    private void StartAnchoredZoom(float targetScale, Point zoomAnchor)
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
    /// Starts (or re-targets) a spring that drives scale and pan together (fit / 100% / step / open / exit).
    /// Velocity carries forward across re-targets, as with <see cref="StartAnchoredZoom"/>.
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
    /// <returns>
    /// The running animation; a re-target keeps the same one but turns its
    /// <see cref="PanZoomAnimation.SuppressZoomReadout"/> back off, so a Fit or 100% during the open-zoom shows its readout.
    /// </returns>
    private PanZoomAnimation StartPanZoomAnimation(float targetScale, Point targetPosition, Size targetCanvasSize,
        bool forceReseed = false)
    {
        SeedScaleSpringIfFresh(forceReseed);
        // Pan state must be seeded whenever the previous animation wasn't already a pan spring (e.g. coming
        // from an anchored zoom, which doesn't track pan velocity) — or always on a forced reseed.
        var reseedPan = forceReseed || _activeAnimation is not PanZoomAnimation;
        var panZoom = _activeAnimation as PanZoomAnimation ?? new PanZoomAnimation(this);
        panZoom.Aim(targetScale, targetPosition, targetCanvasSize, reseedPan);
        panZoom.SuppressZoomReadout = false;
        BeginAnimation(panZoom);
        ViewChanged?.Invoke();
        return panZoom;
    }

    /// <summary>A scale spring (anchored zoom or pan/zoom) is running; a shrug is not one.</summary>
    private bool IsSpringRunning => _activeAnimation is AnchoredZoomAnimation or PanZoomAnimation;

    /// <summary>
    /// Seeds the scale spring's internal state from the live view. When a spring is already running the
    /// scale position/velocity are normally preserved for seamless re-targeting; pass
    /// <paramref name="force"/> = true to override that (a deliberate reset, e.g. the launch open-zoom).
    /// </summary>
    private void SeedScaleSpringIfFresh(bool force = false)
    {
        if (!force && IsSpringRunning) return;
        _scaleSpring.Reset((float)Math.Log(_view.Scale));
        _animationStartMs = _nowMs();
        _lastSpringElapsedMs = 0;
    }

    private double ElapsedMs => _nowMs() - _animationStartMs;

    /// <summary>
    /// Computes the per-frame delta time (seconds) from the animation clock, clamped so a long gap (e.g. the
    /// frame after the Win2D control un-pauses) can't make the spring take one giant, overshooting step.
    /// Returns false when no time has elapsed (a tick fired on the same instant the spring started).
    /// </summary>
    private bool TryGetSpringDt(out float dt)
    {
        var elapsed = ElapsedMs;
        dt = (float)Math.Min((elapsed - _lastSpringElapsedMs) / 1000.0, Constants.SpringMaxDtSeconds);
        _lastSpringElapsedMs = elapsed;
        return dt > 0f;
    }

    /// <summary>
    /// Immediately terminates any in-flight animation, snapping the view to that animation's intended
    /// target first (so the view saved or inherited is the settled one rather than a mid-flight frame). Does NOT fire
    /// AnimationCompleted: the caller is about to change the view.
    /// </summary>
    private void StopAnimationSnappingToTarget()
    {
        // Re-enable snapping FIRST so CompleteImmediately's UpdateTransform builds a snapped, device-pixel
        // aligned resting frame. For a shrug, CompleteImmediately puts the image back at the wiggle's start.
        _view.SnapTranslation = true;
        _activeAnimation?.CompleteImmediately();
        _activeAnimation = null;
    }

    // --- IAnimationHost ---
    // The slice of this class the IViewAnimation strategies drive each frame (see IViewAnimation).

    CanvasViewState IAnimationHost.View => _view;
    SpringAxis IAnimationHost.ScaleSpring => _scaleSpring;
    SpringAxis IAnimationHost.PanXSpring => _panXSpring;
    SpringAxis IAnimationHost.PanYSpring => _panYSpring;
    double IAnimationHost.ElapsedMs => ElapsedMs;

    bool IAnimationHost.TryGetDt(out float dt) => TryGetSpringDt(out dt);

    void IAnimationHost.RaiseViewChanged() => ViewChanged?.Invoke();

    void IAnimationHost.RaiseZoomChanged() => ZoomChanged?.Invoke();

    void IAnimationHost.ReportSettledFit(Size canvasSize)
    {
        // Final "truth-setter" for the fitted / 1:1 flags from the settled scale, using the stored canvas size.
        // It may only CLEAR the fitted flag: a pan during the animation (or an off-centre target) cleared it,
        // and the scale alone can't tell that the image is no longer centred.
        if (canvasSize.Width <= 0 || canvasSize.Height <= 0) return;
        IsFittedToScreen = IsFittedToScreen && ZoomGeometry.SameScale(_view.TargetScale, FitScale(canvasSize));
        UpdateOneToOne();
    }

    void IAnimationHost.FinishSpring()
    {
        // Re-enable snapping, then rebuild the transform so the final resting frame lands on the device-pixel
        // grid (crisp 1:1 / NVIDIA fix). The animation already wrote the exact target Scale/ImagePos.
        ClearActiveAnimation();
        _view.UpdateTransform();
        AnimationCompleted?.Invoke();
    }

    void IAnimationHost.FinishShrug()
    {
        // Re-enable snapping BEFORE the rebuild; the animation already restored the exact start position.
        ClearActiveAnimation();
        _view.UpdateTransform();
        ViewChanged?.Invoke();
    }

    // --- Cleanup ---

    /// <summary>Drops the active animation and re-enables snapping.</summary>
    public void Dispose() => ClearActiveAnimation();
}
