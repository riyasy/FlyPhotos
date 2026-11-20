using System.Numerics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.Controllers.Animation;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The pan/zoom view on its own: no window, no Win2D. Each test drives a <see cref="Rig"/> — the real
/// <see cref="CanvasViewManager"/> with a hand-stepped clock, so animations run frame by frame (16 ms) and every run is
/// the same. The default canvas is 1000×800 and the default photo 4000×3000, which fits at 0.25 (the width limits it).
/// </summary>
[TestClass]
public sealed class CanvasViewManagerTests
{
    private static readonly Size Canvas = new(1000, 800);
    private static readonly Point Centre = new(500, 400);

    [TestInitialize]
    public void Setup() => AppConfig.Settings = new AppSettings { StickyZoomLevels = false };

    private sealed class Rig
    {
        public readonly CanvasViewState View = new();
        public readonly CanvasViewManager M;
        public double Now;
        public bool Fitted, OneToOne;
        public int ZoomChanges, Completions;
        public Size Canvas = CanvasViewManagerTests.Canvas;
        private string _shown = "";

        public Rig()
        {
            M = new CanvasViewManager(View, () => Now);
            M.FitToScreenStateChanged += v => Fitted = v;
            M.OneToOneStateChanged += v => OneToOne = v;
            M.ZoomChanged += () => ZoomChanges++;
            M.AnimationCompleted += () => Completions++;
        }

        /// <summary>One install, as the canvas controller hands it over.</summary>
        public void Arrive(string path, double w, double h, ViewArrival a, int rotation = 0)
        {
            M.SetScaleAndPosition(path, new Size(w, h), rotation, Canvas, a);
            _shown = path;
        }

        /// <summary>
        /// Shows a photo the way the canvas controller classifies it: the first one ever, a new one, or a better
        /// image of the one on screen.
        /// </summary>
        public void Show(string path, double w, double h, int rotation = 0) =>
            Arrive(path, w, h, _shown == "" ? ViewArrival.FirstPhoto
                : path != _shown ? ViewArrival.NewPhoto : ViewArrival.SameImageUpgrade, rotation);

        public void Tick(int frames = 1)
        {
            for (var i = 0; i < frames; i++)
            {
                Now += 16;
                M.OnUpdate();
            }
        }

        public void Settle()
        {
            for (var i = 0; i < 2000 && M.PanZoomAnimationOnGoing; i++) Tick();
            Assert.IsFalse(M.PanZoomAnimationOnGoing, "animation never settled");
        }

        public void ResizeTo(double w, double h)
        {
            var old = Canvas;
            Canvas = new Size(w, h);
            M.HandleSizeChange(Canvas, old);
        }

        /// <summary>The image pixel under screen point <paramref name="p"/>.</summary>
        public Vector2 ImagePointAt(Point p) => Vector2.Transform(new Vector2((float)p.X, (float)p.Y), View.MatInv);

        /// <summary>Where image pixel <paramref name="ip"/> is on screen.</summary>
        public Vector2 ScreenOf(Vector2 ip) => Vector2.Transform(ip, View.Mat);

        public float Scale => View.Scale;
        public Point Pos => View.ImagePos;
    }

    private static void Near(double expected, double actual, double tolerance, string what) =>
        Assert.IsTrue(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    private static void NearPoint(Point expected, Point actual, double tolerance, string what)
    {
        Near(expected.X, actual.X, tolerance, what + " X");
        Near(expected.Y, actual.Y, tolerance, what + " Y");
    }

    private static void SetMode(PanZoomBehaviourOnNavigation mode) => AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;

    // ---------------------------------------------------------------- default view / first photo

    [TestMethod]
    public void FirstPhoto_Large_FitsAndCentres()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        Near(0.25, r.Scale, 1e-6, "scale");
        NearPoint(Centre, r.Pos, 1e-6, "pos");
        Assert.IsTrue(r.Fitted);
        Assert.IsFalse(r.OneToOne);
        Assert.AreEqual(0, r.View.Rotation);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing);
    }

    [TestMethod]
    public void FirstPhoto_Small_ShowsAt100_UnlessStretching()
    {
        var r = new Rig();
        r.Show("A", 200, 100);
        Near(1, r.Scale, 1e-6, "no stretch: 100%");
        Assert.IsFalse(r.Fitted, "100% is not the fit (5x) here");
        Assert.IsTrue(r.OneToOne);

        AppConfig.Settings.StretchSmallImages = true;
        var s = new Rig();
        s.Show("A", 200, 100);
        Near(5, s.Scale, 1e-6, "stretched to fit");
        Assert.IsTrue(s.Fitted);
        Assert.IsFalse(s.OneToOne);
    }

    [TestMethod]
    public void FirstPhoto_ExifRotation_FitsTheRotatedImage()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000, rotation: 90);
        Near(0.2, r.Scale, 1e-6, "rotated: 3000 wide, 4000 tall");
        Assert.AreEqual(90, r.View.Rotation);
    }

    [TestMethod]
    public void FirstPhoto_FitPadding()
    {
        AppConfig.Settings.ImageFitPercentage = 90;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        Near(0.225, r.Scale, 1e-6, "90% of the fit");
    }

    [TestMethod]
    public void OpenZoom_GrowsFromTinyToFit_WithoutZoomReadout()
    {
        AppConfig.Settings.OpenExitZoom = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        Near(0.01, r.Scale, 1e-6, "starts tiny");
        Assert.IsTrue(r.M.PanZoomAnimationOnGoing);
        Assert.IsTrue(r.Fitted, "fitted is known up front");

        r.Settle();
        Near(0.25, r.Scale, 1e-6, "settles at fit");
        NearPoint(Centre, r.Pos, 0.5, "centred");
        Assert.AreEqual(1, r.Completions);
        Assert.AreEqual(0, r.ZoomChanges, "the open-zoom never flashes the zoom readout");
    }

    [TestMethod]
    public void OpenZoom_InterruptedByAWheelZoom_ShowsThatZoomsReadout()
    {
        AppConfig.Settings.OpenExitZoom = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Tick(3);
        r.M.ZoomAtPoint(ZoomDirection.In, Centre);
        r.Tick();
        Assert.IsTrue(r.ZoomChanges > 0, "only the open-zoom itself is silent");
    }

    [TestMethod]
    public void OpenZoom_ReaimedByFit_ShowsTheFitsReadout()
    {
        AppConfig.Settings.OpenExitZoom = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Tick(3);
        var before = r.ZoomChanges;
        r.M.ZoomPanToFit(animateChange: true, Canvas);
        r.Tick();
        Assert.IsTrue(r.ZoomChanges > before, "Fit re-aims the open-zoom's animation, which must not stay silent");
    }

    [TestMethod]
    public void OpenZoom_NotStartedWhenTheCanvasHasNoSize()
    {
        AppConfig.Settings.OpenExitZoom = true;
        var r = new Rig { Canvas = new Size(0, 0) };
        r.Show("A", 4000, 3000);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing, "a zero fit scale would feed log(0) to the spring");
    }

    // ---------------------------------------------------------------- navigation: Reset

    [TestMethod]
    public void Reset_NewPhoto_GoesBackToDefault()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, new Point(300, 300));
        r.M.Pan(40, 40);
        r.M.RotateBy(90);

        r.Show("B", 3000, 2000, rotation: 270);

        Near(Math.Min(1000.0 / 2000, 800.0 / 3000), r.Scale, 1e-6, "B's fit (rotated 270)");
        NearPoint(Centre, r.Pos, 1e-6, "centred");
        Assert.AreEqual(270, r.View.Rotation, "B's EXIF rotation");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Reset_AfterPlaceholder_FirstRealImageGetsItsOwnDefault()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Arrive("B", 100, 100, ViewArrival.NewPhoto); // the loading placeholder
        Near(1, r.Scale, 1e-6, "placeholder at 100% (no stretch)");

        r.Arrive("B", 4000, 3000, ViewArrival.FirstRealImage);
        Near(0.25, r.Scale, 1e-6, "B's fit");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Navigation_MidZoom_JumpsTheZoomToItsEnd()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPoint(ZoomDirection.In, Centre);
        r.Tick(2);

        r.Show("B", 4000, 3000);

        Assert.IsFalse(r.M.PanZoomAnimationOnGoing);
        Near(0.3125, r.Scale, 1e-6, "B inherits the zoom target, not a mid-flight frame");
    }

    // ---------------------------------------------------------------- navigation: RememberPerPhoto

    private static Rig ZoomedRotatedA(PanZoomBehaviourOnNavigation mode)
    {
        SetMode(mode);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, new Point(300, 300)); // scale 0.5, centre moves to (700, 500)
        r.M.RotateBy(90);
        return r;
    }

    [TestMethod]
    public void Remember_RestoresScalePanAndRotation()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.Show("B", 4000, 3000);
        Near(0.25, r.Scale, 1e-6, "B at its default");

        r.Show("A", 4000, 3000);
        Near(0.5, r.Scale, 1e-6, "scale");
        NearPoint(new Point(700, 500), r.Pos, 1e-6, "pan");
        Assert.AreEqual(90, r.View.Rotation);
        Assert.IsFalse(r.Fitted);
        Assert.IsFalse(r.OneToOne);
    }

    [TestMethod]
    public void Remember_PanFollowsTheWindowSize()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.Show("B", 4000, 3000);
        r.ResizeTo(2000, 1600);

        r.Show("A", 4000, 3000);
        Near(0.5, r.Scale, 1e-6, "scale is absolute");
        NearPoint(new Point(1400, 1000), r.Pos, 1e-6, "the offset from the centre doubles with the window");
    }

    [TestMethod]
    public void Remember_UserRotationIsReappliedOntoANewExifBaseline()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.Show("B", 4000, 3000);
        r.Show("A", 4000, 3000, rotation: 90); // this time the image carries a 90° EXIF rotation
        Assert.AreEqual(180, r.View.Rotation, "EXIF 90 + the user's 90");
    }

    [TestMethod]
    public void Remember_ViewPutBackToFit_ComesBackFitted()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.M.RotateBy(-90);
        r.M.ZoomPanToFit(animateChange: true, Canvas);
        r.Settle();
        r.Show("B", 4000, 3000);

        r.Show("A", 4000, 3000);
        Near(0.25, r.Scale, 1e-6, "fit");
        NearPoint(Centre, r.Pos, 0.5, "centred");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Remember_ViewPutBackToFit_ThenWindowResized_ComesBackAtTheNewFit()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.M.RotateBy(-90);
        r.M.ZoomPanToFit(animateChange: true, Canvas); // starts off centre, settles at the default
        r.Settle();
        r.Show("B", 4000, 3000);
        r.ResizeTo(1600, 900);

        r.Show("A", 4000, 3000);
        Near(0.3, r.Scale, 1e-6, "the default for the new window, not the old absolute scale");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Fit_WithoutAnimation_StopsARunningZoom()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPoint(ZoomDirection.In, new Point(200, 200));
        r.Tick(2);
        r.M.ZoomPanToFit(animateChange: false, Canvas);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing, "nothing left to overwrite the fit");
        r.Tick(5);
        Near(0.25, r.Scale, 1e-6, "fit");
        NearPoint(Centre, r.Pos, 1e-6, "centred");
    }

    [TestMethod]
    public void Remember_ViewPutBackTo100ForASmallImage_IsForgotten()
    {
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        var r = new Rig();
        r.Show("A", 200, 100); // default is 100%
        r.M.ZoomAtPointByFactor(2f, Centre);
        r.M.ZoomToHundred(Canvas); // back to the default, and centred
        r.Settle();
        r.Show("B", 4000, 3000);
        r.ResizeTo(1600, 900);

        r.Show("A", 200, 100);
        Near(1, r.Scale, 1e-6, "default 100%");
        NearPoint(new Point(800, 450), r.Pos, 1e-6, "centred in the new window: the default, not a stored view");
    }

    [TestMethod]
    public void Remember_UntouchedPhotoIsNotStored()
    {
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Show("B", 4000, 3000);
        r.ResizeTo(1600, 900);
        r.Show("A", 4000, 3000);
        Near(0.3, r.Scale, 1e-6, "A's fit for the new window");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Remember_OnlyInThatMode()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.Reset);
        r.Show("B", 4000, 3000);
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.Show("A", 4000, 3000);
        Near(0.25, r.Scale, 1e-6, "nothing was stored in Reset mode");
    }

    [TestMethod]
    public void Remember_ViewOnAResizedPageIsNotStored()
    {
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        var r = new Rig();
        r.Show("ico", 256, 256);
        r.M.SetPageSize(new Size(16, 16), Canvas, isFirstPage: false);
        r.M.ZoomAtPointByFactor(10f, Centre);
        r.Show("B", 4000, 3000);

        r.Show("ico", 256, 256);
        Near(1, r.Scale, 1e-6, "default view of page 1");
    }

    [TestMethod]
    public void Remember_RenamedPhotoKeepsItsView()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.M.RenamePhoto("A", "A2"); // renamed while on screen: its view is saved under the new name
        r.Arrive("B", 4000, 3000, ViewArrival.NewPhoto);
        r.Arrive("A2", 4000, 3000, ViewArrival.NewPhoto);
        Near(0.5, r.Scale, 1e-6, "the view moved with the rename");

        r.Arrive("C", 4000, 3000, ViewArrival.NewPhoto);
        r.M.RenamePhoto("A2", "A3"); // renamed after it was saved: the stored view follows
        r.Arrive("A3", 4000, 3000, ViewArrival.NewPhoto);
        Near(0.5, r.Scale, 1e-6, "the stored view moved with the rename");
    }

    // ---------------------------------------------------------------- navigation: RetainFromLastPhoto

    [TestMethod]
    public void Retain_FittedStaysFitted()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Show("B", 3000, 4000);
        Near(0.2, r.Scale, 1e-6, "B's fit");
        NearPoint(Centre, r.Pos, 1e-6, "centred");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Retain_ZoomedKeepsScale_AndPanInProportionToTheImage()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, Centre);
        r.M.Pan(100, 50);

        r.Show("B", 8000, 6000);
        Near(0.5, r.Scale, 1e-6, "absolute scale kept");
        NearPoint(new Point(700, 500), r.Pos, 1e-6, "offset doubled with the image");
        Assert.IsFalse(r.Fitted);
    }

    [TestMethod]
    public void Retain_NewPhotoTakesItsOwnExifRotation()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        r.Show("B", 4000, 3000, rotation: 180);
        Assert.AreEqual(180, r.View.Rotation);
    }

    [TestMethod]
    public void Retain_PlaceholderHop_KeepsPanAndUserRotation()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, Centre);
        r.M.Pan(100, 50);

        r.Arrive("B", 100, 100, ViewArrival.NewPhoto);
        r.M.RotateBy(90); // the user rotates while the placeholder is up
        r.Arrive("B", 4000, 3000, ViewArrival.FirstRealImage, rotation: 0);

        Near(0.5, r.Scale, 1e-6, "scale");
        NearPoint(new Point(600, 450), r.Pos, 1e-3, "pan via the placeholder equals the direct one"); // Point is float
        Assert.AreEqual(90, r.View.Rotation, "the user's rotation survives");
    }

    // ---------------------------------------------------------------- preview -> HQ

    [TestMethod]
    public void Upgrade_Fitted_StaysFitted()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Show("A", 4000, 3000);
        Near(0.25, r.Scale, 1e-6, "fit");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Upgrade_Zoomed_SameSize_ChangesNothing()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, new Point(300, 300));
        r.M.Pan(10, 20);
        var (scale, pos, zoomChanges) = (r.Scale, r.Pos, r.ZoomChanges);

        r.Show("A", 4000, 3000);
        Near(scale, r.Scale, 1e-6, "scale");
        NearPoint(pos, r.Pos, 1e-9, "pan");
        Assert.AreEqual(zoomChanges, r.ZoomChanges, "an upgrade never flashes the zoom readout");
    }

    [TestMethod]
    public void Upgrade_NewExifBaseline_KeepsTheUserRotation()
    {
        var r = new Rig();
        r.Show("A", 3000, 4000); // a preview already turned upright
        r.M.RotateBy(90);
        r.Show("A", 4000, 3000, rotation: 90); // the HQ carries the EXIF rotation
        Assert.AreEqual(180, r.View.Rotation);
    }

    [TestMethod]
    public void Upgrade_UprightCachedPreviewOfARotatedRaw_StaysAtTrue100()
    {
        // A portrait RAW: the HQ is 6000×4000 in sensor orientation with a 90° rotation; the disk-cached preview is
        // already upright, and its size may have been stored in sensor orientation.
        var r = new Rig();
        var layout = ZoomGeometry.PreviewLayoutSize(new Size(6000, 4000), 400, 600);
        r.Show("A", layout.Width, layout.Height);
        r.M.ZoomToHundred(Canvas);
        r.Settle();
        Near(6000, r.View.ImageRect.Height, 1e-6, "the preview is laid out at the photo's upright height");

        r.Show("A", 6000, 4000, rotation: 90);
        Near(1, r.Scale, 1e-6, "100% stays 100%");
        Assert.IsTrue(r.OneToOne);
    }

    [TestMethod]
    public void Upgrade_MidWheelZoom_KeepsOnScreenSizeAndConverges()
    {
        var r = new Rig();
        r.Show("A", 400, 300); // a preview with no full-size metadata, laid out at its own size; at 100%
        r.M.ZoomAtPoint(ZoomDirection.In, Centre); // heading to 1.25
        r.Tick(3);
        var heightOnScreen = r.Scale * 300;

        r.Show("A", 4000, 3000);
        Assert.IsTrue(r.M.PanZoomAnimationOnGoing, "the zoom keeps going");
        Near(heightOnScreen, r.Scale * 3000, 1e-3, "no jump on screen");

        r.Settle();
        Near(0.125, r.Scale, 1e-6, "the same visual target (1.25 of the preview)");
        NearPoint(Centre, r.Pos, 0.5, "centre kept");
    }

    [TestMethod]
    public void Upgrade_MidFitAnimation_SettlesFittedToTheHq()
    {
        var r = new Rig { Canvas = new Size(1000, 1000) };
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred(r.Canvas);
        r.Settle();
        r.M.ZoomPanToFit(animateChange: true, r.Canvas);
        r.Tick(3);

        r.Show("A", 4500, 3000);
        r.Settle();
        Near(1000.0 / 4500, r.Scale, 1e-5, "HQ fit");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void Upgrade_DuringOpenZoom_StillEndsFitted()
    {
        AppConfig.Settings.OpenExitZoom = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.Tick(3);
        r.Show("A", 4000, 3000);
        r.Settle();
        Near(0.25, r.Scale, 1e-6, "fit");
        Assert.AreEqual(1, r.Completions);
        Assert.AreEqual(0, r.ZoomChanges);
    }

    // ---------------------------------------------------------------- resize

    [TestMethod]
    public void Resize_Fitted_Refits()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.ResizeTo(1600, 900);
        Near(0.3, r.Scale, 1e-6, "new fit");
        NearPoint(new Point(800, 450), r.Pos, 1e-6, "new centre");
        Assert.AreEqual(1, r.ZoomChanges, "a re-fit shows the zoom readout");
    }

    [TestMethod]
    public void Resize_Zoomed_MovesPanWithTheWindow()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, Centre);
        r.M.Pan(100, 100);
        r.ResizeTo(1600, 900);
        Near(0.5, r.Scale, 1e-6, "scale kept");
        NearPoint(new Point(960, 562.5), r.Pos, 1e-6, "pan scaled with the window");
    }

    [TestMethod]
    public void Resize_FromZeroSize_LeavesAZoomedViewAlone()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, Centre);
        r.M.HandleSizeChange(new Size(1600, 900), new Size(0, 0));
        NearPoint(Centre, r.Pos, 1e-9, "no ratio to apply");
    }

    [TestMethod]
    public void ImageSizedResize_ShiftsTheImage_OrRefitsWhenItNoLongerFits()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.HandleImageSizedWindowResize(new Point(10, 20), new Size(2000, 1600));
        NearPoint(new Point(490, 380), r.Pos, 1e-9, "shifted with the canvas origin");
        Near(0.25, r.Scale, 1e-6, "scale kept: it still fits");

        var s = new Rig();
        s.Show("A", 4000, 3000);
        s.M.HandleImageSizedWindowResize(new Point(10, 20), new Size(500, 400));
        Near(0.125, s.Scale, 1e-6, "clamped window: re-fitted");
        NearPoint(new Point(250, 200), s.Pos, 1e-6, "centred in it");
    }

    // ---------------------------------------------------------------- zoom commands

    [TestMethod]
    public void WheelZoom_KeepsThePointUnderTheCursorStill()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        var anchor = new Point(300, 250);
        var ip = r.ImagePointAt(anchor);

        r.M.ZoomAtPoint(ZoomDirection.In, anchor);
        r.Tick(4);
        var mid = r.ScreenOf(ip);
        Near(anchor.X, mid.X, 0.75, "mid-zoom X");
        Near(anchor.Y, mid.Y, 0.75, "mid-zoom Y");

        r.Settle();
        var end = r.ScreenOf(ip);
        Near(anchor.X, end.X, 0.75, "end X");
        Near(anchor.Y, end.Y, 0.75, "end Y");
    }

    [TestMethod]
    public void WheelZoom_Steps_AndRapidNotchesBuildOnTheTarget()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPoint(ZoomDirection.In, Centre);
        r.M.ZoomAtPoint(ZoomDirection.In, Centre);
        r.Settle();
        Near(0.25 * 1.25 * 1.25, r.Scale, 1e-6, "two notches in");

        r.M.ZoomAtPoint(ZoomDirection.Out, Centre);
        r.Settle();
        Near(0.25 * 1.25 * 1.25 * 0.8, r.Scale, 1e-6, "one notch out");
        Assert.IsFalse(r.Fitted);
        Assert.IsTrue(r.ZoomChanges > 0);
    }

    [TestMethod]
    public void ZoomOut_StopsAtTheFloor()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000); // floor: min(5%, 50 px long side) = 0.0125
        r.M.ZoomAtPointByFactor(0.052f, Centre); // 0.013
        Near(0.013, r.Scale, 1e-6, "just above the floor");

        r.M.ZoomAtPoint(ZoomDirection.Out, Centre);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing, "a wheel step below the floor is refused");
        r.M.ZoomAtPointByFactor(0.9f, Centre);
        Near(0.013, r.Scale, 1e-6, "a pinch below the floor is refused");

        r.M.ZoomAtPointByFactor(2f, Centre);
        Near(0.026, r.Scale, 1e-6, "zooming in is never floored");
    }

    [TestMethod]
    public void StickyZoom_SnapsOntoTheStops()
    {
        AppConfig.Settings.StickyZoomLevels = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(1.8f, Centre); // 0.45
        r.M.ZoomAtPoint(ZoomDirection.In, Centre); // 0.5625 crosses 0.5
        r.Settle();
        Near(0.5, r.Scale, 1e-6, "snapped to 50%");
    }

    [TestMethod]
    public void PinchZoom_IsImmediate_AndKeepsTheAnchor()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        var anchor = new Point(300, 250);
        var ip = r.ImagePointAt(anchor);
        r.M.ZoomAtPointByFactor(1.5f, anchor);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing);
        Near(0.375, r.Scale, 1e-6, "scale");
        var s = r.ScreenOf(ip);
        Near(anchor.X, s.X, 0.6, "anchor X");
        Near(anchor.Y, s.Y, 0.6, "anchor Y");
        Assert.AreEqual(1, r.ZoomChanges);
    }

    [TestMethod]
    public void StepZoom_WalksFit_100_400_AndStopsAtTheEnds()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);

        r.M.StepZoom(ZoomDirection.In, Canvas);
        Assert.IsTrue(r.OneToOne, "1:1 known up front");
        Assert.IsFalse(r.Fitted);
        r.Settle();
        Near(1, r.Scale, 1e-6, "100%");
        NearPoint(Centre, r.Pos, 0.5, "centred");

        r.M.StepZoom(ZoomDirection.In, Canvas);
        r.Settle();
        Near(4, r.Scale, 1e-6, "400%");
        r.M.StepZoom(ZoomDirection.In, Canvas);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing, "no stop above 400%");

        r.M.StepZoom(ZoomDirection.Out, Canvas);
        r.Settle();
        r.M.StepZoom(ZoomDirection.Out, Canvas);
        r.Settle();
        Near(0.25, r.Scale, 1e-6, "back at fit");
        Assert.IsTrue(r.Fitted);
        r.M.StepZoom(ZoomDirection.Out, Canvas);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing, "no stop below fit");
    }

    [TestMethod]
    public void ZoomToHundred_CentresAt100()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.Pan(30, 30);
        r.M.ZoomToHundred(Canvas);
        Assert.IsTrue(r.OneToOne);
        Assert.IsFalse(r.Fitted);
        r.Settle();
        Near(1, r.Scale, 1e-6, "100%");
        NearPoint(Centre, r.Pos, 0.5, "centred");
    }

    [TestMethod]
    public void ZoomToHundredAtPoint_KeepsThePointStill()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        var anchor = new Point(300, 250);
        var ip = r.ImagePointAt(anchor);
        r.M.ZoomToHundred(Canvas, anchor);
        r.Settle();
        var s = r.ScreenOf(ip);
        Near(anchor.X, s.X, 0.6, "X");
        Near(anchor.Y, s.Y, 0.6, "Y");
        Assert.IsTrue(r.OneToOne);
    }

    [TestMethod]
    public void Fit_IsExplicit_SoItUpscalesASmallImage()
    {
        var r = new Rig();
        r.Show("A", 200, 100);
        r.M.ZoomPanToFit(animateChange: false, Canvas);
        Near(5, r.Scale, 1e-6, "fit even though StretchSmallImages is off");
        Assert.IsTrue(r.Fitted);
        Assert.IsFalse(r.OneToOne);
        Assert.AreEqual(1, r.ZoomChanges);
    }

    [TestMethod]
    public void Pan_ClearsFitButNot1To1()
    {
        var r = new Rig();
        r.Show("A", 200, 100);
        r.M.Pan(5, 5);
        Assert.IsFalse(r.Fitted);
        Assert.IsTrue(r.OneToOne);
        NearPoint(new Point(505, 405), r.Pos, 1e-9, "moved");
    }

    [TestMethod]
    public void Pan_DuringAnAnimation_IsKept()
    {
        var plain = new Rig();
        var panned = new Rig();
        foreach (var r in new[] { plain, panned })
        {
            r.Show("A", 4000, 3000);
            r.M.ZoomAtPoint(ZoomDirection.In, new Point(300, 250));
            r.Tick(3);
        }
        panned.M.Pan(50, -20);
        plain.Settle();
        panned.Settle();
        Near(plain.Pos.X + 50, panned.Pos.X, 0.01, "X");
        Near(plain.Pos.Y - 20, panned.Pos.Y, 0.01, "Y");
        Near(plain.Scale, panned.Scale, 1e-6, "same zoom");
    }

    [TestMethod]
    public void Rotate_AddsDegrees_ClearsFit_Keeps1To1()
    {
        var r = new Rig();
        r.Show("A", 200, 100);
        r.M.RotateBy(90);
        r.M.RotateBy(90);
        Assert.AreEqual(180, r.View.Rotation);
        Assert.IsFalse(r.Fitted);
        Assert.IsTrue(r.OneToOne);
        Near(1, r.Scale, 1e-6, "scale unchanged");
    }

    [TestMethod]
    public void Shrug_WigglesAndReturnsExactly()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.Pan(7, 3);
        var start = r.Pos;
        r.M.Shrug();
        r.Tick(3);
        Assert.AreNotEqual(start.X, r.Pos.X, "moving");
        Near(start.Y, r.Pos.Y, 1e-9, "only sideways");
        r.Settle();
        NearPoint(start, r.Pos, 1e-9, "back exactly");
    }

    [TestMethod]
    public void Shrug_IsIgnoredWhileAnotherAnimationRuns()
    {
        var plain = new Rig();
        var shrugged = new Rig();
        foreach (var r in new[] { plain, shrugged })
        {
            r.Show("A", 4000, 3000);
            r.M.ZoomAtPoint(ZoomDirection.In, new Point(300, 250));
        }
        shrugged.M.Shrug();
        plain.Settle();
        shrugged.Settle();
        NearPoint(plain.Pos, shrugged.Pos, 1e-9, "the zoom ran undisturbed");
    }

    [TestMethod]
    public void ExitZoom_ShrinksAway_WithoutZoomReadout()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomOutOnExit(Canvas);
        Assert.IsFalse(r.Fitted);
        r.Settle();
        Near(0.001, r.Scale, 1e-6, "a dot");
        Assert.AreEqual(1, r.Completions);
        Assert.AreEqual(0, r.ZoomChanges);
    }

    [TestMethod]
    public void PageOfAnotherSize_RefitsKeepingTheRotation()
    {
        var r = new Rig();
        r.Show("ico", 256, 256);
        r.M.RotateBy(90);
        r.M.SetPageSize(new Size(16, 16), Canvas, isFirstPage: false);
        Near(1, r.Scale, 1e-6, "16 px page at 100% (no stretch)");
        Assert.AreEqual(90, r.View.Rotation, "manual rotation kept");
        Assert.AreEqual(16, r.View.ImageRect.Width);
        Assert.AreEqual(1, r.ZoomChanges, "the readout follows the page");
    }

    [TestMethod]
    public void RestingFrame_LandsOnWholePixels_AndAnimationsAreSmooth()
    {
        var r = new Rig();
        r.Show("A", 4001, 2999);
        r.M.ZoomAtPoint(ZoomDirection.In, new Point(333.3, 277.7));
        r.Tick(2);
        Assert.IsFalse(r.View.SnapTranslation, "no rounding mid-animation");
        r.Settle();
        Assert.IsTrue(r.View.SnapTranslation);
        Near(Math.Round(r.View.Mat.M31), r.View.Mat.M31, 1e-3, "M31 whole");
        Near(Math.Round(r.View.Mat.M32), r.View.Mat.M32, 1e-3, "M32 whole");

        r.M.ZoomPanToFit(animateChange: true, Canvas);
        r.Settle();
        Near(Math.Round(r.View.Mat.M31), r.View.Mat.M31, 1e-3, "fit M31 whole");
        Near(Math.Round(r.View.Mat.M32), r.View.Mat.M32, 1e-3, "fit M32 whole");
    }

    // ---------------------------------------------------------------- fixed defects

    [TestMethod]
    public void RememberPerPhoto_LeavingMidZoom_SavesTheZoomTarget()
    {
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPoint(ZoomDirection.In, Centre);
        r.Tick(2);
        r.Show("B", 4000, 3000);
        r.Show("A", 4000, 3000);
        Near(0.3125, r.Scale, 1e-6, "restored at the zoom target");
    }

    [TestMethod]
    public void Fit_OnPreviewWithOtherAspect_FitsWhatIsShown_AndHqStaysFitted()
    {
        var r = new Rig { Canvas = new Size(1000, 1000) }; // the width limits the fit
        r.Show("A", 4000, 3000); // 4:3 preview layout
        r.M.ZoomToHundred(r.Canvas);
        r.Settle();

        r.M.ZoomPanToFit(animateChange: true, r.Canvas);
        r.Settle();
        Near(0.25, r.Scale, 1e-6, "fitted to the preview as laid out");
        Assert.IsTrue(r.Fitted);

        r.Show("A", 4500, 3000); // 3:2 HQ
        Near(1000.0 / 4500, r.Scale, 1e-6, "HQ re-fitted");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void DragDuringFitAnimation_EndsNotFitted()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred(Canvas);
        r.Settle();
        r.M.ZoomPanToFit(animateChange: true, Canvas);
        r.Tick(2);
        r.M.Pan(30, 0);
        r.Settle();
        Assert.IsFalse(r.Fitted, "panned off centre: not fitted");
    }

    [TestMethod]
    public void AnchoredStepZoomToFit_IsNotFitted()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred(Canvas);
        r.Settle();
        r.M.StepZoom(ZoomDirection.Out, Canvas, new Point(100, 100));
        r.Settle();
        Near(0.25, r.Scale, 1e-6, "at the fit scale");
        Assert.IsFalse(r.Fitted, "off centre: not fitted");
    }

    [TestMethod]
    public void ResizeDuringFitAnimation_SettlesFittedToTheNewCanvas()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred(Canvas);
        r.Settle();
        r.M.ZoomPanToFit(animateChange: true, Canvas);
        r.Tick(2);
        r.ResizeTo(1600, 900);
        Assert.IsTrue(r.M.PanZoomAnimationOnGoing, "the animation keeps going");
        r.Settle();
        Near(0.3, r.Scale, 1e-6, "fit of the new canvas");
        NearPoint(new Point(800, 450), r.Pos, 0.5, "centred");
        Assert.IsTrue(r.Fitted);
    }

    [TestMethod]
    public void ResizeDuringStepZoomTo100_KeepsGoingWithThePanInProportion()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred(Canvas);
        r.Tick(2);
        r.ResizeTo(2000, 1600);
        r.Settle();
        Near(1, r.Scale, 1e-6, "100%");
        NearPoint(new Point(1000, 800), r.Pos, 0.5, "the centre target moved with the window");
    }

    [TestMethod]
    public void ResizeDuringWheelZoom_JumpsTheZoomToItsEnd()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPoint(ZoomDirection.In, new Point(200, 200));
        r.Tick(2);
        r.ResizeTo(1600, 900);
        Assert.IsFalse(r.M.PanZoomAnimationOnGoing, "no animation left to overwrite the resize");
        Near(0.3125, r.Scale, 1e-6, "at the zoom target");
    }

    [TestMethod]
    public void Upgrade_AtOneToOne_WithOtherAspect_StaysAtOneToOne()
    {
        var r = new Rig { Canvas = new Size(1000, 1000) };
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred(r.Canvas);
        r.Settle();
        var pos = r.Pos;

        r.Show("A", 4500, 3000);
        Near(1, r.Scale, 1e-6, "still 100%");
        Assert.IsTrue(r.OneToOne);
        NearPoint(pos, r.Pos, 1e-9, "centre unchanged");
    }

    [TestMethod]
    public void Upgrade_KeepsOnScreenHeight_WhenLayoutUnitsDiffer()
    {
        var r = new Rig();
        r.Show("A", 400, 300); // no full-size metadata: laid out at its own small size
        r.M.ZoomAtPointByFactor(1.5f, Centre);
        var before = r.Scale * 300;
        r.Show("A", 4000, 3000);
        Near(before, r.Scale * 3000, 1e-3, "no metadata");

        r.Show("B", 2250, 3000); // already turned upright...
        r.M.ZoomAtPointByFactor(1.5f, Centre);
        before = r.Scale * 3000;
        r.Show("B", 4000, 3000, rotation: 90); // ...while the HQ carries a 90° EXIF rotation
        Near(before, r.Scale * 4000, 1e-3, "rotated HQ");
    }

    [TestMethod]
    public void FineZoomStoppingAt100_IsOneToOne_AndDoubleClickDoesNotMoveTheImage()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(3.9f, Centre); // 0.975
        r.M.ZoomAtPointByFactor(1.1f, Centre, stopAtOneToOne: true);
        Near(1, r.Scale, 1e-6, "stopped at 100%");
        Assert.IsTrue(r.OneToOne);

        r.M.Pan(100, 50);
        var pos = r.Pos;
        r.M.ZoomToHundred(Canvas, new Point(200, 150));
        r.Settle();
        NearPoint(pos, r.Pos, 1, "no jump");
    }

    [TestMethod]
    public void StickyWheelZoomLandingOn100_IsOneToOne()
    {
        AppConfig.Settings.StickyZoomLevels = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(3.4f, Centre); // 0.85
        r.M.ZoomAtPoint(ZoomDirection.In, Centre);
        Assert.IsTrue(r.OneToOne);
        r.Settle();
        Near(1, r.Scale, 1e-6, "snapped to 100%");
    }

    [TestMethod]
    public void ShrugInterruptedByNavigation_LeavesNoOffset()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomAtPointByFactor(2f, Centre);
        r.M.Pan(40, 0);
        var pos = r.Pos;
        r.M.Shrug();
        r.Tick(3);
        r.Show("B", 4000, 3000);
        NearPoint(pos, r.Pos, 1e-9, "same pan on the next photo");
    }
}
