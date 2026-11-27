using System;
using System.Diagnostics;
using Windows.Foundation;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>How newly installed content relates to what is on screen; it picks the view rule.</summary>
internal enum ViewArrival
{
    /// <summary>The first photo since launch: the default view, with the open-zoom if it is on.</summary>
    FirstPhoto,
    /// <summary>Another photo: the navigation setting decides the view.</summary>
    NewPhoto,
    /// <summary>The first real image of the photo on screen after its placeholder: navigation too.</summary>
    FirstRealImage,
    /// <summary>A better image of the photo on screen (preview → HQ, a reload, an animation's frames): the view stays.</summary>
    SameImageUpgrade
}

/// <summary>
/// The view on screen and every rule that changes it: user zoom, pan and rotation, a new photo, a better image, a
/// resize. W2D thread only, no locks, no Win2D, so the tests drive it directly with a hand-stepped clock.
/// <para>
/// <b>Fitted</b> is an intent, "this view follows the fit": resize, upgrade and Retain re-fit while it is set, and
/// a pan, rotation or anchored zoom clears it. <b>1:1</b> is the target scale being 100%. Both are published as
/// events after every change.
/// </para>
/// <para>
/// The photo-size rules (#195): a preview is laid out at the photo's size by its metadata, an HQ at its own decoded
/// size, which can differ (a cropped RAW, a small embedded JPEG). <see cref="_imagePixelsPerPhotoPixel"/> relates the
/// two, so an upgrade, Retain and Remember keep the photo's size on screen rather than the raw scale.
/// </para>
/// </summary>
internal sealed class ViewController
{
    /// <summary>The open-zoom grows the first photo from this scale to its default.</summary>
    private const float OpenZoomStartScale = 0.01f;

    /// <summary>The exit zoom shrinks the image to this scale as the window closes.</summary>
    private const float ExitZoomTargetScale = 0.001f;

    /// <summary>The animation clock in ms.</summary>
    private readonly Func<double> _nowMs;

    /// <summary>The one animation; it steps <see cref="_view"/> but holds no view of its own.</summary>
    private readonly ViewMotion _motion;

    /// <summary>The views remembered per photo (RememberPerPhoto).</summary>
    private readonly PerPhotoViewStore _store = new();

    /// <summary>The view on screen. Replaced, never mutated; see <see cref="View"/>.</summary>
    private View _view = new(default, 1f, default, 0);

    /// <summary>The photo on screen, so its size can be learned when it is left (<see cref="Install"/>).</summary>
    private Photo _shownPhoto;

    /// <summary>The path of the photo on screen; empty before the first install.</summary>
    private string _photoPath = string.Empty;

    /// <summary>A preview or HQ of the photo on screen was installed, not only its placeholder.</summary>
    private bool _realImageShown;

    /// <summary>The EXIF rotation of the image on screen; the view's rotation is this plus the user's turns.</summary>
    private int _exifRotation;

    /// <summary>
    /// The image on screen's pixels per photo pixel (see <see cref="ViewRules.ImagePixelsPerPhotoPixel"/>); null when
    /// the photo's size by metadata isn't known (an HQ shown before its preview loaded). A placeholder keeps the last
    /// real image's value, so the image after it is measured against the photo left.
    /// </summary>
    private float? _imagePixelsPerPhotoPixel;

    /// <summary>The view follows the fit (see the class summary).</summary>
    private bool _fitted;

    /// <summary>The user changed this photo's view since it arrived: worth remembering (RememberPerPhoto).</summary>
    private bool _userChangedView;

    /// <summary>A page of another size than page 1 is up (ICO frames): its absolute zoom can't be remembered.</summary>
    private bool _onPageOfOtherSize;

    /// <summary>Only the loading placeholder is up: nothing of the photo's own to remember.</summary>
    private bool _onPlaceholder;

    /// <summary>
    /// The remembered view restored for the photo on screen, or (Retain) the view on the last image left. Kept until
    /// the photo's HQ arrives, which gets its exact scale if it is an image of that size, or until the user changes the view.
    /// </summary>
    private PerPhotoViewStore.RestoredView? _remembered;

    /// <summary>When the shrug started, while it runs.</summary>
    private double? _shrugStartMs;

    /// <summary>The fitted and 1:1 states as last raised, so the events fire only on a change.</summary>
    private bool _publishedFitted, _publishedOneToOne;

    /// <summary>The view started or stopped following the fit.</summary>
    public event Action<bool> FitToScreenStateChanged;

    /// <summary>
    /// A slideshow's view: each new photo opens at its default view, whatever the navigation setting, and the fit goes
    /// edge to edge (no ImageFitPercentage padding). A fitted photo on screen follows the change.
    /// </summary>
    public bool SlideshowFit
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (_fitted && HasPhoto) AnimateToFit();
        }
    }

    /// <summary>The navigation rule in effect: a slideshow's view opens every photo fresh.</summary>
    private PanZoomBehaviourOnNavigation NavigationRule =>
        SlideshowFit ? PanZoomBehaviourOnNavigation.Reset : AppConfig.Settings.PanZoomBehaviourOnNavigation;

    /// <summary>The target scale became, or stopped being, 100%.</summary>
    public event Action<bool> OneToOneStateChanged;

    /// <summary>Show the zoom readout (the scale): the user zoomed, or a resize or page change re-fitted. Not on navigation.</summary>
    public event Action<float> ZoomChanged;

    /// <summary>An animation arrived (not when one is cut short).</summary>
    public event Action AnimationCompleted;

    /// <param name="nowMs">The animation clock in ms; tests step it by hand.</param>
    public ViewController(Func<double> nowMs = null)
    {
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        _motion = new ViewMotion(_nowMs);
    }

    /// <summary>The canvas size in physical pixels. The engine sets it on every resize.</summary>
    public Size CanvasSize { get; set; } = new(0, 0);

    /// <summary>The view on screen.</summary>
    public View View => _view;

    /// <summary>The path of the photo on screen.</summary>
    public string PhotoPath => _photoPath;

    /// <summary>A photo was shown. Before the first there is nothing to zoom or pan, and a resize only records the size.</summary>
    public bool HasPhoto => !string.IsNullOrEmpty(_photoPath);

    /// <summary>A view animation or the shrug is running: the render loop must keep ticking.</summary>
    public bool IsAnimating => _motion.Running || _shrugStartMs != null;

    /// <summary>Where the scale is heading; zoom steps build on it, so quick notches compound.</summary>
    public float TargetScale => _motion.Running ? _motion.TargetScale : _view.Scale;

    /// <summary>The frame to draw: snapped at rest, and with the shrug's wiggle.</summary>
    public ViewFrame Frame => _view.Frame(snap: !IsAnimating, ShrugOffset());

    /// <summary>How the next install relates to the one on screen.</summary>
    /// <param name="shownPath">The path on screen; empty before the first install.</param>
    /// <param name="realImageShown">A preview or HQ of that photo is up, not only its placeholder.</param>
    /// <param name="photoPath">The path of the photo being installed.</param>
    /// <param name="level">What is being installed: placeholder, preview or HQ.</param>
    public static ViewArrival ClassifyArrival(string shownPath, bool realImageShown, string photoPath, DisplayLevel level)
    {
        if (string.IsNullOrEmpty(shownPath)) return ViewArrival.FirstPhoto;
        if (photoPath != shownPath) return ViewArrival.NewPhoto;
        if (!realImageShown && level > DisplayLevel.PlaceHolder) return ViewArrival.FirstRealImage;
        return ViewArrival.SameImageUpgrade;
    }

    // ---------------------------------------------------------------- content

    /// <summary>
    /// An install, as the engine makes it: works out how it relates to what is on screen, learns the size of the photo
    /// leaving (its preview may have loaded after its HQ went up), and shows it. <paramref name="layout"/> is
    /// <see cref="ViewRules.LayoutSize"/>, taken when the install was asked for.
    /// </summary>
    public void Install(Photo photo, string path, Size layout, int rotation, DisplayLevel level)
    {
        var arrival = ClassifyArrival(_photoPath, _realImageShown, path, level);
        if (arrival is ViewArrival.FirstPhoto or ViewArrival.NewPhoto)
        {
            if (_shownPhoto != null && ViewRules.PhotoSizeOnScreen(_shownPhoto) is { } leavingSize)
                LearnPhotoSize(leavingSize);
            _shownPhoto = photo;
            _realImageShown = false;
        }
        if (level > DisplayLevel.PlaceHolder) _realImageShown = true;
        Show(path, layout, rotation, arrival, ViewRules.PhotoSizeOnScreen(photo), level);
    }

    /// <summary>
    /// The view rules for new content whose <paramref name="arrival"/> and <paramref name="photoSize"/> are already
    /// known. <see cref="Install"/> works those out from the photo; the tests pass them directly, so they need no
    /// photo objects.
    /// </summary>
    /// <param name="photoPath">The path of the photo the content belongs to.</param>
    /// <param name="imageSize">The image's layout size (<see cref="ViewRules.LayoutSize"/>), unrotated.</param>
    /// <param name="exifRotation">The image's EXIF rotation in degrees.</param>
    /// <param name="arrival">How the content relates to what is on screen; picks the view rule.</param>
    /// <param name="photoSize">The photo's size on screen by its metadata, null when unknown.</param>
    /// <param name="level">
    /// The loading placeholder keeps the last real image's pixels per photo pixel, so the image after it measures
    /// Retain by the photo left, not by the placeholder (a far jump would otherwise land a RAW's HQ at 370%).
    /// </param>
    public void Show(string photoPath, Size imageSize, int exifRotation, ViewArrival arrival, Size photoSize,
        DisplayLevel level = DisplayLevel.Hq)
    {
        var placeholder = level == DisplayLevel.PlaceHolder;
        if (arrival == ViewArrival.NewPhoto) RememberViewOnScreen(); // while the view still shows the old photo

        var perPhoto = ViewRules.ImagePixelsPerPhotoPixel(imageSize, exifRotation, photoSize);
        var exifDelta = exifRotation - _exifRotation;
        _exifRotation = exifRotation;
        _photoPath = photoPath;
        _onPageOfOtherSize = false;

        switch (arrival)
        {
            case ViewArrival.FirstPhoto:
                _view = _view with { ImageRect = Layout(imageSize) };
                ShowDefault(exifRotation, openZoom: true);
                break;
            case ViewArrival.SameImageUpgrade:
                Upgrade(imageSize, exifDelta, perPhoto);
                break;
            default:
                Navigate(imageSize, exifRotation, exifDelta, arrival, perPhoto, level);
                break;
        }
        if (!placeholder) _imagePixelsPerPhotoPixel = perPhoto; // after Upgrade and Retain, which measure by the image before
        _onPlaceholder = placeholder;
        PublishStates();
    }

    /// <summary>The photo's size by its metadata, known only now (its preview loaded after its HQ). A known ratio stays.</summary>
    public void LearnPhotoSize(Size photoSize) =>
        _imagePixelsPerPhotoPixel ??= ViewRules.ImagePixelsPerPhotoPixel(_view.ImageSize, _exifRotation, photoSize);

    /// <summary>
    /// A page landed. One of another size (ICO frames shrink to 16 px) gets a fresh default view, keeping the user's
    /// rotation; pages of one size (TIFF) keep the zoom and pan.
    /// </summary>
    public void ShowPage(Size pageSize, bool isFirstPage)
    {
        if (pageSize.Width == _view.ImageRect.Width && pageSize.Height == _view.ImageRect.Height) return;
        _onPageOfOtherSize = !isFirstPage;
        StopMotion();
        _view = _view with { ImageRect = Layout(pageSize) };
        ShowDefault(_view.Rotation, openZoom: false);
        ZoomChanged?.Invoke(_view.Scale);
        PublishStates();
    }

    /// <summary>A photo was renamed: the remembered views and the photo on screen follow the new path.</summary>
    public void Rename(string oldPath, string newPath)
    {
        _store.Rename(oldPath, newPath);
        if (_photoPath == oldPath) _photoPath = newPath;
    }

    /// <summary>
    /// A better image of the photo on screen keeps the view: fitted stays fitted, otherwise the photo keeps its size on
    /// screen and the same part stays at the centre. A running animation keeps running, rescaled.
    /// </summary>
    /// <param name="imageSize">The new image's layout size.</param>
    /// <param name="exifDelta">The new image's EXIF rotation less the old one's (a preview may come pre-rotated).</param>
    /// <param name="perPhoto">The new image's pixels per photo pixel, null when unknown.</param>
    private void Upgrade(Size imageSize, int exifDelta, float? perPhoto)
    {
        var old = _view;
        var centre = CentreOnImage();
        _view = _view with { ImageRect = Layout(imageSize), Rotation = old.Rotation + exifDelta };
        if (!_motion.Running && _fitted)
        {
            PlaceFitted();
            return;
        }

        var ratio = ViewRules.UpgradeScaleRatio(CanvasSize, old.ImageSize, old.Rotation, _imagePixelsPerPhotoPixel,
            imageSize, _view.Rotation, perPhoto, _fitted);
        _view = _view with { Scale = _view.Scale * ratio };
        if (_motion.Running)
        {
            _motion.Rebase(_view, ratio); // the animation owns the pan and lands where it aimed
            return;
        }
        // A remembered view's own image is back (its HQ after the preview): its saved scale is the exact one.
        if (_remembered?.ScaleOn(imageSize) is { } rememberedScale) _view = _view with { Scale = rememberedScale };
        _remembered = null;
        _view = _view with { Pos = ViewRules.ImagePosFor(centre, _view.Scale, imageSize, _view.Rotation, CanvasSize) };
    }

    /// <summary>Another photo, or the first real image after a placeholder: the navigation setting decides.</summary>
    /// <param name="imageSize">The new image's layout size.</param>
    /// <param name="exifRotation">The new image's EXIF rotation: the base for a default, retained or restored rotation.</param>
    /// <param name="exifDelta">The new image's EXIF rotation less the old one's.</param>
    /// <param name="arrival">NewPhoto or FirstRealImage: after a placeholder, Retain keeps the user's rotation.</param>
    /// <param name="perPhoto">The new image's pixels per photo pixel, null when unknown.</param>
    /// <param name="level">An HQ takes the retained view at once; a preview or placeholder keeps it for its HQ.</param>
    private void Navigate(Size imageSize, int exifRotation, int exifDelta, ViewArrival arrival, float? perPhoto,
        DisplayLevel level)
    {
        StopMotion(); // so Retain inherits where the user was heading, not a mid-flight frame
        var centre = CentreOnImage();
        var retainedScale = SamePhotoSizeScale(perPhoto);
        // The view on the last real image left, kept through a placeholder or an untouched preview: an image of its
        // size gets its exact scale back, as the photos' sizes by metadata can disagree (or be unknown).
        var left = _remembered ?? (_onPlaceholder ? null
            : new PerPhotoViewStore.RestoredView(PhotoZoom, centre, _view.Rotation, _view.ImageSize, _view.Scale));
        _view = _view with { ImageRect = Layout(imageSize) };
        _remembered = null;

        switch (NavigationRule)
        {
            case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                if (_store.TryRestore(_photoPath, exifRotation, out var restored))
                {
                    // On the image it was saved on, its scale; on another (the preview), the zoom on the photo.
                    Restore(restored.ScaleOn(imageSize) ?? restored.PhotoZoom / (perPhoto ?? 1f), restored);
                    _remembered = restored;
                }
                else
                    ShowDefault(exifRotation, openZoom: false);
                break;
            case PanZoomBehaviourOnNavigation.Reset:
                ShowDefault(exifRotation, openZoom: false);
                break;
            case PanZoomBehaviourOnNavigation.RetainFromLastPhoto:
                // A new photo takes its own EXIF rotation; after a placeholder the user's rotation carries over.
                _view = _view with { Rotation = arrival == ViewArrival.NewPhoto ? exifRotation : _view.Rotation + exifDelta };
                if (_fitted) PlaceFitted();
                else
                {
                    PlaceAt(left?.ScaleOn(imageSize) ?? retainedScale, centre);
                    if (level != DisplayLevel.Hq) _remembered = left; // Upgrade gives its HQ the exact scale
                }
                break;
        }
    }

    /// <summary>Centred at the default scale (the fit, not above 100% unless stretching); the first photo grows there from tiny.</summary>
    private void ShowDefault(int rotation, bool openZoom)
    {
        _view = _view with { Rotation = rotation };
        var scale = ViewRules.DefaultScale(CanvasSize, _view.ImageSize, rotation, edgeToEdge: SlideshowFit);
        var centre = ViewRules.Centre(CanvasSize);
        _view = _view with { Scale = scale, Pos = centre };

        // scale > 0: an unsized canvas would feed log(0) to the spring.
        if (openZoom && AppConfig.Settings.OpenExitZoom && scale > 0)
        {
            _view = _view with { Scale = OpenZoomStartScale };
            MoveTo(scale, centre, restart: true, silentReadout: true);
        }
        _fitted = ViewRules.SameScale(scale, FitScale());
        _userChangedView = false;
    }

    /// <summary>Puts a remembered view back at <paramref name="scale"/> (its scale on this image).</summary>
    private void Restore(float scale, PerPhotoViewStore.RestoredView restored)
    {
        _view = _view with { Rotation = restored.Rotation };
        PlaceAt(scale, restored.CentreOnImage);
        // Fitted needs the fit scale and a centred pan.
        _fitted = ViewRules.SameScale(scale, FitScale()) && ViewRules.IsCentred(_view.Pos, CanvasSize);
        _userChangedView = true;
    }

    /// <summary>
    /// RememberPerPhoto, leaving a photo: stores its view if the user changed it, or forgets it if they put it back to
    /// the default.
    /// </summary>
    private void RememberViewOnScreen()
    {
        if (AppConfig.Settings.PanZoomBehaviourOnNavigation != PanZoomBehaviourOnNavigation.RememberPerPhoto
            || !_userChangedView || _onPageOfOtherSize || _onPlaceholder) // untouched: a slideshow's reset keeps the saved one
            return;
        StopMotion(); // save where the view was heading
        if (IsDefaultView())
            _store.Remove(_photoPath);
        else
            _store.Save(_photoPath, PhotoZoom, CentreOnImage(), _view.Rotation, _exifRotation, _view.ImageSize,
                _view.Scale);
    }

    /// <summary>The view is the one the photo opens at: default scale, EXIF rotation, centred.</summary>
    private bool IsDefaultView() =>
        ViewRules.SameScale(_view.Scale, ViewRules.DefaultScale(CanvasSize, _view.ImageSize, _exifRotation))
        && ViewRules.NormalizeDegrees(_view.Rotation) == ViewRules.NormalizeDegrees(_exifRotation)
        && ViewRules.IsCentred(_view.Pos, CanvasSize);

    // ---------------------------------------------------------------- window

    /// <summary>
    /// The canvas was resized: fitted re-fits, otherwise the image keeps its place relative to the canvas. The old size
    /// is the last one applied, not the control's PreviousSize, which converts at the new DPI after a monitor change.
    /// </summary>
    public void Resize(Size newSize)
    {
        var oldSize = CanvasSize;
        CanvasSize = newSize;
        if (!HasPhoto) return;
        if (_motion.Running && _motion.PinsCentre)
        {
            // Re-aim a pan + zoom at the new canvas, so the launch open-zoom still plays.
            if (_fitted) AnimateToFit();
            else MoveTo(_motion.TargetScale,
                ViewRules.ResizedPan(_motion.CentreTarget, oldSize, newSize));
        }
        else if (_fitted) RefitNow();
        else
        {
            StopMotion();
            _view = _view with { Pos = ViewRules.ResizedPan(_view.Pos, oldSize, newSize) };
        }
        PublishStates();
    }

    /// <summary>
    /// The window was resized around the image (to fit it), moving the canvas origin by <paramref name="shift"/>: the
    /// image stays where it is on screen, unless it is fitted and no longer fits (the window was clamped).
    /// </summary>
    public void ImageSizedResize(Point shift, Size newSize)
    {
        CanvasSize = newSize;
        if (!HasPhoto) return;
        StopMotion();
        if (_fitted && FitScale() < _view.Scale - ViewRules.ScaleTolerance)
            RefitNow();
        else
            _view = _view with { Pos = new Point(_view.Pos.X - shift.X, _view.Pos.Y - shift.Y) };
        PublishStates();
    }

    // ---------------------------------------------------------------- user

    /// <summary>One wheel or key zoom step, animated, keeping <paramref name="anchor"/> (default: the centre) still.</summary>
    public void WheelZoom(ZoomDirection direction, Point? anchor = null)
    {
        if (ViewRules.WheelStepTarget(TargetScale, direction, _view.ImageSize) is not { } target) return;
        ZoomAround(target, anchor ?? ViewRules.Centre(CanvasSize));
        UserChanged(fitted: false);
    }

    /// <summary>Scales at once around <paramref name="anchor"/> (touchpad, pinch, Shift+wheel), stopping at 100% if asked.</summary>
    public void ZoomBy(float factor, Point anchor, bool stopAtOneToOne = false)
    {
        StopMotion(); // a running wheel zoom would overwrite this step
        var from = _view.Scale;
        var scale = from * factor;
        if (stopAtOneToOne) scale = ViewRules.StopOnCrossing(scale, from, 1f);
        if (factor < 1f && scale < ViewRules.MinZoomOutScale(_view.ImageSize)) return;

        _view = _view with { Scale = scale, Pos = ViewRules.AnchorPreservingPan(anchor, _view.Pos, from, scale) };
        ZoomChanged?.Invoke(_view.Scale);
        UserChanged(fitted: false);
    }

    /// <summary>The next stop (fit, 100%, 400%), animated: centred, or keeping <paramref name="anchor"/> still.</summary>
    public void StepZoom(ZoomDirection direction, Point? anchor = null)
    {
        var fit = FitScale();
        if (ViewRules.NextZoomStop(TargetScale, fit, direction) is not { } target) return;
        if (anchor is { } a) ZoomAround(target, a);
        else MoveTo(target, ViewRules.Centre(CanvasSize));
        // An anchored step isn't centred, so not fitted even at the fit scale.
        UserChanged(fitted: anchor == null && ViewRules.SameScale(target, fit));
    }

    /// <summary>100%, centred, or keeping <paramref name="anchor"/> still.</summary>
    public void ZoomToHundred(Point? anchor = null)
    {
        var target = anchor is { } a ? ViewRules.AnchorPreservingPan(a, _view.Pos, _view.Scale, 1f) : ViewRules.Centre(CanvasSize);
        MoveTo(1f, target);
        UserChanged(fitted: ViewRules.SameScale(1f, FitScale()) && ViewRules.IsCentred(target, CanvasSize));
    }

    /// <summary>Fits the image to the window (upscaling a small one), animated or at once.</summary>
    public void Fit(bool animate)
    {
        if (!animate)
        {
            RefitNow();
            PublishStates();
            return;
        }
        AnimateToFit();
        UserChanged(fitted: true);
    }

    /// <summary>The closing animation: the image shrinks to a dot.</summary>
    public void ExitZoom()
    {
        MoveTo(ExitZoomTargetScale, ViewRules.Centre(CanvasSize), silentReadout: true);
        _fitted = false;
        PublishStates();
    }

    /// <summary>Moves the image by (<paramref name="dx"/>, <paramref name="dy"/>) canvas pixels.</summary>
    public void Pan(double dx, double dy)
    {
        if (_motion.Running) _motion.Nudge(dx, dy); // a drag mid-animation must not be undone by the next frame
        _view = _view with { Pos = new Point(_view.Pos.X + dx, _view.Pos.Y + dy) };
        UserChanged(fitted: false);
    }

    /// <summary>Turns the image by <paramref name="degrees"/> about its centre; a fitted image re-fits the turned shape.</summary>
    public void Rotate(int degrees)
    {
        _view = _view with { Rotation = _view.Rotation + degrees };
        if (_motion.Running) _motion.Rebase(_view, 1f);
        if (_fitted) RefitNow();
        UserChanged(fitted: _fitted);
    }

    /// <summary>A small sideways shake: an action was refused. Not while another animation runs.</summary>
    public void Shrug()
    {
        if (IsAnimating) return;
        _shrugStartMs = _nowMs();
    }

    // ---------------------------------------------------------------- frames

    /// <summary>Once per frame: advances the animation and the shrug.</summary>
    public void Tick()
    {
        if (_motion.Running)
        {
            var (view, moved, settled) = _motion.Step(_view);
            _view = view;
            if (moved && !_motion.SilentReadout) ZoomChanged?.Invoke(_view.Scale);
            if (settled)
            {
                // A pan during the animation, or an off-centre target, has already cleared fitted; the settled
                // scale can only confirm or clear it.
                if (CanvasSize.Width > 0 && CanvasSize.Height > 0)
                    _fitted = _fitted && ViewRules.SameScale(_view.Scale, FitScale());
                AnimationCompleted?.Invoke();
            }
        }
        if (_shrugStartMs is { } start && _nowMs() - start >= Constants.ShrugAnimationDurationMs)
            _shrugStartMs = null;
        PublishStates();
    }

    /// <summary>The shrug's sideways offset now: a sine that fades out over the shrug's duration; 0 when none runs.</summary>
    private double ShrugOffset()
    {
        if (_shrugStartMs is not { } start) return 0;
        var t = Math.Clamp((_nowMs() - start) / Constants.ShrugAnimationDurationMs, 0.0, 1.0);
        return Constants.ShrugAmplitude * Math.Sin(t * Constants.ShrugFrequency * 2 * Math.PI) * (1 - t);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The image rect (0, 0, W, H) for an image of <paramref name="imageSize"/>.</summary>
    private static Rect Layout(Size imageSize) => new(0, 0, imageSize.Width, imageSize.Height);

    /// <summary>The fit scale of the image on screen, at its current rotation.</summary>
    private float FitScale() => ViewRules.FitScale(CanvasSize, _view.ImageSize, _view.Rotation, edgeToEdge: SlideshowFit);

    /// <summary>The part of the image at the canvas centre (<see cref="ViewRules.CentreOnImage"/>).</summary>
    private Point CentreOnImage() =>
        ViewRules.CentreOnImage(_view.Pos, _view.Scale, _view.ImageSize, _view.Rotation, CanvasSize);

    /// <summary>The zoom on the photo itself (screen pixels per photo pixel), which Remember keeps.</summary>
    private float PhotoZoom => _view.Scale * (_imagePixelsPerPhotoPixel ?? 1f);

    /// <summary>The scale that shows the photo at its size on screen on an image of <paramref name="perPhoto"/>; the same scale if unknown.</summary>
    private float SamePhotoSizeScale(float? perPhoto) =>
        _imagePixelsPerPhotoPixel is { } onScreen && perPhoto is { } other ? _view.Scale * onScreen / other : _view.Scale;

    /// <summary>Sets <paramref name="scale"/> and puts <paramref name="centreOnImage"/> at the canvas centre.</summary>
    private void PlaceAt(float scale, Point centreOnImage) =>
        _view = _view with { Scale = scale, Pos = ViewRules.ImagePosFor(centreOnImage, scale, _view.ImageSize, _view.Rotation, CanvasSize) };

    /// <summary>At the fit scale, centred, and fitted.</summary>
    private void PlaceFitted()
    {
        _view = _view with { Scale = FitScale(), Pos = ViewRules.Centre(CanvasSize) };
        _fitted = true;
    }

    /// <summary>Fits at once, cutting any animation short, and shows the readout.</summary>
    private void RefitNow()
    {
        StopMotion();
        PlaceFitted();
        ZoomChanged?.Invoke(_view.Scale);
    }

    /// <summary>Animates to the fit scale, centred. The caller decides what fitted becomes.</summary>
    private void AnimateToFit() => MoveTo(FitScale(), ViewRules.Centre(CanvasSize));

    /// <summary>Starts or retargets a pan + zoom; a shrug gives way to it.</summary>
    private void MoveTo(float scale, Point target, bool restart = false, bool silentReadout = false)
    {
        _shrugStartMs = null;
        _motion.MoveTo(_view, scale, target, restart, silentReadout);
    }

    /// <summary>Starts or retargets an anchored zoom; a shrug gives way to it.</summary>
    private void ZoomAround(float scale, Point anchor)
    {
        _shrugStartMs = null;
        _motion.ZoomAround(_view, scale, anchor);
    }

    /// <summary>Jumps any animation to its end (and stops a shrug), so what follows works from the settled view.</summary>
    private void StopMotion()
    {
        _shrugStartMs = null;
        if (_motion.Running) _view = _motion.Finish(_view);
    }

    /// <summary>The user changed the view: records whether it now follows the fit, and publishes.</summary>
    private void UserChanged(bool fitted)
    {
        _fitted = fitted;
        _userChangedView = true;
        _remembered = null; // the user's own view now, not the remembered one
        PublishStates();
    }

    /// <summary>Raises the fitted and 1:1 events when they changed.</summary>
    private void PublishStates()
    {
        if (_fitted != _publishedFitted)
        {
            _publishedFitted = _fitted;
            FitToScreenStateChanged?.Invoke(_fitted);
        }
        var oneToOne = ViewRules.SameScale(TargetScale, 1f);
        if (oneToOne != _publishedOneToOne)
        {
            _publishedOneToOne = oneToOne;
            OneToOneStateChanged?.Invoke(oneToOne);
        }
    }
}
