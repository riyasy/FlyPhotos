using System.Numerics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Infra.Configuration;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;
using View = FlyPhotos.Display.Canvas.View;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The pan/zoom view on its own: no window, no Win2D. Each test drives a <see cref="Rig"/> — the real
/// <see cref="ViewController"/> with a hand-stepped clock, so animations run frame by frame (16 ms) and every run is
/// the same. The default canvas is 1000×800 and the default photo 4000×3000, which fits at 0.25 (the width limits it).
/// </summary>
[TestClass]
public sealed class ViewControllerTests
{
    private static readonly Size Canvas = new(1000, 800);
    private static readonly Point Centre = new(500, 400);

    [TestInitialize]
    public void Setup() => AppConfig.Settings = new AppSettings { StickyZoomLevels = false };

    private sealed class Rig
    {
        public readonly ViewController M;
        public double Now;
        public bool Fitted, OneToOne;
        public int ZoomChanges, Completions;
        private string _shown = "";

        public Rig()
        {
            M = new ViewController(() => Now) { CanvasSize = ViewControllerTests.Canvas };
            M.FitToScreenStateChanged += v => Fitted = v;
            M.OneToOneStateChanged += v => OneToOne = v;
            M.ZoomChanged += _ => ZoomChanges++;
            M.AnimationCompleted += () => Completions++;
        }

        public Size Canvas
        {
            get => M.CanvasSize;
            set => M.CanvasSize = value;
        }

        public View View => M.View;

        /// <summary>One install, as the canvas engine hands it over.</summary>
        public void Arrive(string path, double w, double h, ViewArrival a, int rotation = 0, Size? photoSize = null)
        {
            M.Show(path, new Size(w, h), rotation, a, photoSize);
            _shown = path;
        }

        /// <summary>
        /// Shows a photo the way the canvas controller classifies it: the first one ever, a new one, or a better
        /// image of the one on screen. <paramref name="photoSize"/>: the photo's size on screen by its metadata.
        /// </summary>
        public void Show(string path, double w, double h, int rotation = 0, Size? photoSize = null) =>
            Arrive(path, w, h, ViewController.ClassifyArrival(_shown, true, path, DisplayLevel.Hq), rotation, photoSize);

        public void Tick(int frames = 1)
        {
            for (var i = 0; i < frames; i++)
            {
                Now += 16;
                M.Tick();
            }
        }

        public void Settle()
        {
            for (var i = 0; i < 2000 && M.IsAnimating; i++) Tick();
            Assert.IsFalse(M.IsAnimating, "animation never settled");
        }

        public void ResizeTo(double w, double h) => M.Resize(new Size(w, h));

        /// <summary>The image pixel under screen point <paramref name="p"/>.</summary>
        public Vector2 ImagePointAt(Point p) => Vector2.Transform(new Vector2((float)p.X, (float)p.Y), M.Frame.MatInv);

        /// <summary>Where image pixel <paramref name="ip"/> is on screen.</summary>
        public Vector2 ScreenOf(Vector2 ip) => Vector2.Transform(ip, M.Frame.Mat);

        public float Scale => View.Scale;
        public Point Pos => View.Pos;
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
        Assert.IsFalse(r.M.IsAnimating);
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
        Assert.IsTrue(r.M.IsAnimating);
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
        r.M.WheelZoom(ZoomDirection.In, Centre);
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
        r.M.Fit(animate: true);
        r.Tick();
        Assert.IsTrue(r.ZoomChanges > before, "Fit re-aims the open-zoom's animation, which must not stay silent");
    }

    [TestMethod]
    public void OpenZoom_NotStartedWhenTheCanvasHasNoSize()
    {
        AppConfig.Settings.OpenExitZoom = true;
        var r = new Rig { Canvas = new Size(0, 0) };
        r.Show("A", 4000, 3000);
        Assert.IsFalse(r.M.IsAnimating, "a zero fit scale would feed log(0) to the spring");
    }

    // ---------------------------------------------------------------- navigation: Reset

    [TestMethod]
    public void Reset_NewPhoto_GoesBackToDefault()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomBy(2f, new Point(300, 300));
        r.M.Pan(40, 40);
        r.M.Rotate(90);

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
        r.M.WheelZoom(ZoomDirection.In, Centre);
        r.Tick(2);

        r.Show("B", 4000, 3000);

        Assert.IsFalse(r.M.IsAnimating);
        Near(0.3125, r.Scale, 1e-6, "B inherits the zoom target, not a mid-flight frame");
    }

    // ---------------------------------------------------------------- navigation: RememberPerPhoto

    private static Rig ZoomedRotatedA(PanZoomBehaviourOnNavigation mode)
    {
        SetMode(mode);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomBy(2f, new Point(300, 300)); // scale 0.5, centre moves to (700, 500)
        r.M.Rotate(90);
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
        NearPoint(new Point(700, 500), r.Pos, 1e-3, "pan"); // through a float scale
        Assert.AreEqual(90, r.View.Rotation);
        Assert.IsFalse(r.Fitted);
        Assert.IsFalse(r.OneToOne);
    }

    [TestMethod]
    public void Remember_AfterAResize_TheSamePartIsAtTheCentre()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.Show("B", 4000, 3000);
        r.ResizeTo(2000, 1600);

        r.Show("A", 4000, 3000);
        Near(0.5, r.Scale, 1e-6, "scale is absolute");
        // Saved 200 px right and 100 px below the centre; at the same scale that is the same part of the photo.
        NearPoint(new Point(1200, 900), r.Pos, 1e-3, "the same offset from the new centre");
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
        r.M.Rotate(-90);
        r.M.Fit(animate: true);
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
        r.M.Rotate(-90);
        r.M.Fit(animate: true); // starts off centre, settles at the default
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
        r.M.WheelZoom(ZoomDirection.In, new Point(200, 200));
        r.Tick(2);
        r.M.Fit(animate: false);
        Assert.IsFalse(r.M.IsAnimating, "nothing left to overwrite the fit");
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
        r.M.ZoomBy(2f, Centre);
        r.M.ZoomToHundred(); // back to the default, and centred
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
        r.M.ShowPage(new Size(16, 16), isFirstPage: false);
        r.M.ZoomBy(10f, Centre);
        r.Show("B", 4000, 3000);

        r.Show("ico", 256, 256);
        Near(1, r.Scale, 1e-6, "default view of page 1");
    }

    [TestMethod]
    public void Remember_RenamedPhotoKeepsItsView()
    {
        var r = ZoomedRotatedA(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        r.M.Rename("A", "A2"); // renamed while on screen: its view is saved under the new name
        r.Arrive("B", 4000, 3000, ViewArrival.NewPhoto);
        r.Arrive("A2", 4000, 3000, ViewArrival.NewPhoto);
        Near(0.5, r.Scale, 1e-6, "the view moved with the rename");

        r.Arrive("C", 4000, 3000, ViewArrival.NewPhoto);
        r.M.Rename("A2", "A3"); // renamed after it was saved: the stored view follows
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
        r.M.ZoomBy(2f, Centre);
        r.M.Pan(100, 50);

        r.Show("B", 8000, 6000);
        Near(0.5, r.Scale, 1e-6, "absolute scale kept");
        NearPoint(new Point(700, 500), r.Pos, 1e-3, "offset doubled with the image"); // through a float scale
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
        r.M.ZoomBy(2f, Centre);
        r.M.Pan(100, 50);

        r.Arrive("B", 100, 100, ViewArrival.NewPhoto);
        r.M.Rotate(90); // the user rotates while the placeholder is up
        r.Arrive("B", 4000, 3000, ViewArrival.FirstRealImage, rotation: 0);

        Near(0.5, r.Scale, 1e-6, "scale");
        // The centre was 45% across and 46.7% down A's box on screen; it stays there on B, turned to 1500×2000.
        NearPoint(new Point(575, 450 + 50 / 3.0), r.Pos, 1e-3, "the same place in the box on screen");
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
        r.M.ZoomBy(2f, new Point(300, 300));
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
        r.M.Rotate(90);
        r.Show("A", 4000, 3000, rotation: 90); // the HQ carries the EXIF rotation
        Assert.AreEqual(180, r.View.Rotation);
    }

    [TestMethod]
    public void Upgrade_UprightCachedPreviewOfARotatedRaw_StaysAtTrue100()
    {
        // A portrait RAW: the HQ is 6000×4000 in sensor orientation with a 90° rotation; the disk-cached preview is
        // already upright, and its size may have been stored in sensor orientation.
        var r = new Rig();
        var layout = ViewRules.PreviewLayoutSize(new Size(6000, 4000), 400, 600);
        r.Show("A", layout.Width, layout.Height);
        r.M.ZoomToHundred();
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
        r.M.WheelZoom(ZoomDirection.In, Centre); // heading to 1.25
        r.Tick(3);
        var heightOnScreen = r.Scale * 300;

        r.Show("A", 4000, 3000);
        Assert.IsTrue(r.M.IsAnimating, "the zoom keeps going");
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
        r.M.ZoomToHundred();
        r.Settle();
        r.M.Fit(animate: true);
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
        r.M.ZoomBy(2f, Centre);
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
        r.M.ZoomBy(2f, Centre);
        r.Canvas = new Size(0, 0); // as before the canvas was first laid out
        r.M.Resize(new Size(1600, 900));
        NearPoint(Centre, r.Pos, 1e-9, "no ratio to apply");
    }

    [TestMethod]
    public void ImageSizedResize_ShiftsTheImage_OrRefitsWhenItNoLongerFits()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ImageSizedResize(new Point(10, 20), new Size(2000, 1600));
        NearPoint(new Point(490, 380), r.Pos, 1e-9, "shifted with the canvas origin");
        Near(0.25, r.Scale, 1e-6, "scale kept: it still fits");

        var s = new Rig();
        s.Show("A", 4000, 3000);
        s.M.ImageSizedResize(new Point(10, 20), new Size(500, 400));
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

        r.M.WheelZoom(ZoomDirection.In, anchor);
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
        r.M.WheelZoom(ZoomDirection.In, Centre);
        r.M.WheelZoom(ZoomDirection.In, Centre);
        r.Settle();
        Near(0.25 * 1.25 * 1.25, r.Scale, 1e-6, "two notches in");

        r.M.WheelZoom(ZoomDirection.Out, Centre);
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
        r.M.ZoomBy(0.052f, Centre); // 0.013
        Near(0.013, r.Scale, 1e-6, "just above the floor");

        r.M.WheelZoom(ZoomDirection.Out, Centre);
        Assert.IsFalse(r.M.IsAnimating, "a wheel step below the floor is refused");
        r.M.ZoomBy(0.9f, Centre);
        Near(0.013, r.Scale, 1e-6, "a pinch below the floor is refused");

        r.M.ZoomBy(2f, Centre);
        Near(0.026, r.Scale, 1e-6, "zooming in is never floored");
    }

    [TestMethod]
    public void StickyZoom_SnapsOntoTheStops()
    {
        AppConfig.Settings.StickyZoomLevels = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomBy(1.8f, Centre); // 0.45
        r.M.WheelZoom(ZoomDirection.In, Centre); // 0.5625 crosses 0.5
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
        r.M.ZoomBy(1.5f, anchor);
        Assert.IsFalse(r.M.IsAnimating);
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

        r.M.StepZoom(ZoomDirection.In);
        Assert.IsTrue(r.OneToOne, "1:1 known up front");
        Assert.IsFalse(r.Fitted);
        r.Settle();
        Near(1, r.Scale, 1e-6, "100%");
        NearPoint(Centre, r.Pos, 0.5, "centred");

        r.M.StepZoom(ZoomDirection.In);
        r.Settle();
        Near(4, r.Scale, 1e-6, "400%");
        r.M.StepZoom(ZoomDirection.In);
        Assert.IsFalse(r.M.IsAnimating, "no stop above 400%");

        r.M.StepZoom(ZoomDirection.Out);
        r.Settle();
        r.M.StepZoom(ZoomDirection.Out);
        r.Settle();
        Near(0.25, r.Scale, 1e-6, "back at fit");
        Assert.IsTrue(r.Fitted);
        r.M.StepZoom(ZoomDirection.Out);
        Assert.IsFalse(r.M.IsAnimating, "no stop below fit");
    }

    [TestMethod]
    public void ZoomToHundred_CentresAt100()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.Pan(30, 30);
        r.M.ZoomToHundred();
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
        r.M.ZoomToHundred(anchor);
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
        r.M.Fit(animate: false);
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
            r.M.WheelZoom(ZoomDirection.In, new Point(300, 250));
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
        r.M.Rotate(90);
        r.M.Rotate(90);
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
        var drawnAtRest = r.M.Frame.Mat;
        r.M.Shrug();
        r.Tick(3);
        // The wiggle is drawn, not written into the view, so it can't leave an offset behind.
        Assert.AreNotEqual(drawnAtRest.M31, r.M.Frame.Mat.M31, "moving");
        Near(drawnAtRest.M32, r.M.Frame.Mat.M32, 1e-3, "only sideways");
        NearPoint(start, r.Pos, 1e-9, "the view itself doesn't move");
        r.Settle();
        Assert.AreEqual(drawnAtRest, r.M.Frame.Mat, "back exactly");
    }

    [TestMethod]
    public void Shrug_IsIgnoredWhileAnotherAnimationRuns()
    {
        var plain = new Rig();
        var shrugged = new Rig();
        foreach (var r in new[] { plain, shrugged })
        {
            r.Show("A", 4000, 3000);
            r.M.WheelZoom(ZoomDirection.In, new Point(300, 250));
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
        r.M.ExitZoom();
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
        r.M.Rotate(90);
        r.M.ShowPage(new Size(16, 16), isFirstPage: false);
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
        r.M.WheelZoom(ZoomDirection.In, new Point(333.3, 277.7));
        r.Tick(2);
        Assert.IsTrue(r.M.IsAnimating, "no rounding mid-animation");
        r.Settle();
        Near(Math.Round(r.M.Frame.Mat.M31), r.M.Frame.Mat.M31, 1e-3, "M31 whole");
        Near(Math.Round(r.M.Frame.Mat.M32), r.M.Frame.Mat.M32, 1e-3, "M32 whole");

        r.M.Fit(animate: true);
        r.Settle();
        Near(Math.Round(r.M.Frame.Mat.M31), r.M.Frame.Mat.M31, 1e-3, "fit M31 whole");
        Near(Math.Round(r.M.Frame.Mat.M32), r.M.Frame.Mat.M32, 1e-3, "fit M32 whole");
    }

    // ---------------------------------------------------------------- fixed defects

    [TestMethod]
    public void RememberPerPhoto_LeavingMidZoom_SavesTheZoomTarget()
    {
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.WheelZoom(ZoomDirection.In, Centre);
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
        r.M.ZoomToHundred();
        r.Settle();

        r.M.Fit(animate: true);
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
        r.M.ZoomToHundred();
        r.Settle();
        r.M.Fit(animate: true);
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
        r.M.ZoomToHundred();
        r.Settle();
        r.M.StepZoom(ZoomDirection.Out, new Point(100, 100));
        r.Settle();
        Near(0.25, r.Scale, 1e-6, "at the fit scale");
        Assert.IsFalse(r.Fitted, "off centre: not fitted");
    }

    [TestMethod]
    public void ResizeDuringFitAnimation_SettlesFittedToTheNewCanvas()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred();
        r.Settle();
        r.M.Fit(animate: true);
        r.Tick(2);
        r.ResizeTo(1600, 900);
        Assert.IsTrue(r.M.IsAnimating, "the animation keeps going");
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
        r.M.ZoomToHundred();
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
        r.M.WheelZoom(ZoomDirection.In, new Point(200, 200));
        r.Tick(2);
        r.ResizeTo(1600, 900);
        Assert.IsFalse(r.M.IsAnimating, "no animation left to overwrite the resize");
        Near(0.3125, r.Scale, 1e-6, "at the zoom target");
    }

    [TestMethod]
    public void Upgrade_AtOneToOne_WithOtherAspect_StaysAtOneToOne()
    {
        var r = new Rig { Canvas = new Size(1000, 1000) };
        r.Show("A", 4000, 3000);
        r.M.ZoomToHundred();
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
        r.M.ZoomBy(1.5f, Centre);
        var before = r.Scale * 300;
        r.Show("A", 4000, 3000);
        Near(before, r.Scale * 3000, 1e-3, "no metadata");

        r.Show("B", 2250, 3000); // already turned upright...
        r.M.ZoomBy(1.5f, Centre);
        before = r.Scale * 3000;
        r.Show("B", 4000, 3000, rotation: 90); // ...while the HQ carries a 90° EXIF rotation
        Near(before, r.Scale * 4000, 1e-3, "rotated HQ");
    }

    [TestMethod]
    public void FineZoomStoppingAt100_IsOneToOne_AndDoubleClickDoesNotMoveTheImage()
    {
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomBy(3.9f, Centre); // 0.975
        r.M.ZoomBy(1.1f, Centre, stopAtOneToOne: true);
        Near(1, r.Scale, 1e-6, "stopped at 100%");
        Assert.IsTrue(r.OneToOne);

        r.M.Pan(100, 50);
        var pos = r.Pos;
        r.M.ZoomToHundred(new Point(200, 150));
        r.Settle();
        NearPoint(pos, r.Pos, 1, "no jump");
    }

    [TestMethod]
    public void StickyWheelZoomLandingOn100_IsOneToOne()
    {
        AppConfig.Settings.StickyZoomLevels = true;
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomBy(3.4f, Centre); // 0.85
        r.M.WheelZoom(ZoomDirection.In, Centre);
        Assert.IsTrue(r.OneToOne);
        r.Settle();
        Near(1, r.Scale, 1e-6, "snapped to 100%");
    }

    // ---------------------------------------------------------------- preview -> HQ of another size (#195)

    /// <summary>
    /// A RAW's preview, laid out at the full size its metadata gives, and the HQ one decoder makes of it. Measured on
    /// the RAW test files (preview metadata from WIC, HQ from Rawler, WIC or ImageMagick, RAW decode on or off).
    /// </summary>
    private sealed record RawCase(string Name, double MetaW, double MetaH, double BmpW, double BmpH,
        double HqW, double HqH, int HqRotation, bool HqFailed = false)
    {
        public Size Layout => ViewRules.PreviewLayoutSize(new Size(MetaW, MetaH), BmpW, BmpH);
    }

    private static readonly RawCase[] RawCases =
    [
        new("CR3, Rawler crops the sensor border", 5496, 3670, 800, 534, 5472, 3648, 0),
        new("CR3, ImageMagick: the metadata size", 5496, 3670, 800, 534, 5496, 3670, 0),
        new("ARW, small embedded JPEG", 6024, 4024, 1024, 684, 1616, 1080, 0),
        new("ARW, Rawler decode", 6024, 4024, 1024, 684, 6000, 4000, 0),
        new("ARW, WIC embedded JPEG", 6240, 4168, 1024, 682, 6192, 4128, 0),
        new("portrait NEF, sensor axes + 270", 4040, 6064, 682, 1024, 6048, 4032, 270),
        new("portrait NEF, ImageMagick upright", 4040, 6064, 682, 1024, 4032, 6048, 0),
        new("portrait NEF, sensor axes + 90", 5520, 8280, 682, 1024, 8256, 5504, 90),
        new("Z9 NEF, embedded JPEG", 8280, 5520, 1024, 682, 8256, 5504, 0),
        new("HQ failed: the error icon", 5496, 3670, 800, 534, 600, 600, 0, HqFailed: true),
    ];

    /// <summary>The cases by name: a test method can't take the private record, and the name reads well per row.</summary>
    public static IEnumerable<object[]> RawCaseNames => RawCases.Select(c => new object[] { c.Name });

    private static RawCase Case(string name) => RawCases.Single(c => c.Name == name);

    // The canvas controller hands every install the photo's size by its metadata: the preview's layout (upright here).
    private static void ShowPreview(Rig r, string path, RawCase c) =>
        r.Show(path, c.Layout.Width, c.Layout.Height, photoSize: c.Layout);
    private static void ShowHq(Rig r, string path, RawCase c) => r.Show(path, c.HqW, c.HqH, c.HqRotation, c.Layout);

    /// <summary>The HQ's pixels per pixel of the photo (the preview's layout): the scale on the HQ is the preview's over this.</summary>
    private static double PerPhoto(RawCase c) =>
        ViewRules.ImagePixelsPerPhotoPixel(new Size(c.HqW, c.HqH), c.HqRotation, c.Layout).Value;

    /// <summary>
    /// The photo didn't jump: its larger on-screen side (the one a decoder's border crop changes least, or a downscale
    /// as much as the other) is the size it was.
    /// </summary>
    private static void NoJump((double X, double Y, double W, double H) before, Rig r, string what)
    {
        var after = CentreFraction(r);
        Near(1, Math.Max(after.W / before.W, after.H / before.H), 1e-5, what);
    }

    /// <summary>
    /// Where the canvas centre falls within the image's on-screen bounds, 0..1 per axis, and the size of those bounds
    /// in screen pixels.
    /// </summary>
    private static (double X, double Y, double W, double H) CentreFraction(Rig r)
    {
        float w = (float)r.View.ImageRect.Width, h = (float)r.View.ImageRect.Height;
        Vector2[] corners = [r.ScreenOf(new(0, 0)), r.ScreenOf(new(w, 0)), r.ScreenOf(new(0, h)), r.ScreenOf(new(w, h))];
        double minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X);
        double minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
        return ((Centre.X - minX) / (maxX - minX), (Centre.Y - minY) / (maxY - minY), maxX - minX, maxY - minY);
    }

    /// <summary>
    /// The canvas centre is on the same part of the photo as <paramref name="before"/>, to within a screen pixel (the
    /// resting frame is snapped to the pixel grid).
    /// </summary>
    private static void SamePartAtCentre((double X, double Y, double W, double H) before, Rig r)
    {
        var after = CentreFraction(r);
        Near(before.X, after.X, 1 / after.W, "centre X");
        Near(before.Y, after.Y, 1 / after.H, "centre Y");
    }

    [TestMethod]
    [DynamicData(nameof(RawCaseNames))]
    public void UpgradeToRawHq_Fitted_FitsTheHq(string name)
    {
        var c = Case(name);
        var r = new Rig();
        ShowPreview(r, "A", c);
        ShowHq(r, "A", c);
        Assert.IsTrue(r.Fitted);
        Near(ViewRules.FitScale(Canvas, new Size(c.HqW, c.HqH), c.HqRotation), r.Scale, 1e-6, "fit");
    }

    [TestMethod]
    [DynamicData(nameof(RawCaseNames))]
    public void UpgradeToRawHq_At100OnThePreview_KeepsTheSizeOnScreen_AndThePartAtTheCentre(string name)
    {
        // 100% of the metadata size; on the HQ the readout tells its own pixels (100.4% for a Rawler crop).
        var c = Case(name);
        var r = new Rig();
        ShowPreview(r, "A", c);
        r.M.ZoomToHundred(new Point(200, 150));
        r.Settle();
        var part = CentreFraction(r);

        ShowHq(r, "A", c);
        Near(1 / PerPhoto(c), r.Scale, 1e-5, "the photo keeps its size");
        NoJump(part, r, "no jump on screen");
        Assert.AreEqual(ViewRules.SameScale(r.Scale, 1f), r.OneToOne, "1:1 only at true 100%");
        SamePartAtCentre(part, r);
    }

    [TestMethod]
    [DynamicData(nameof(RawCaseNames))]
    public void UpgradeToRawHq_ZoomedAndPanned_KeepsTheSizeOnScreenAndThePartAtTheCentre(string name)
    {
        var c = Case(name);
        var r = new Rig();
        ShowPreview(r, "A", c);
        r.M.ZoomBy(7f, new Point(250, 600));
        r.M.Pan(30, -20);
        var (scale, part) = (r.Scale, CentreFraction(r));

        ShowHq(r, "A", c);
        Near(scale / PerPhoto(c), r.Scale, 1e-5, "the photo keeps its size");
        NoJump(part, r, "no jump on screen");
        SamePartAtCentre(part, r);
    }

    [TestMethod]
    [DynamicData(nameof(RawCaseNames))]
    public void UpgradeToRawHq_MidZoom_KeepsZoomingToItsTargetOnScreen(string name)
    {
        // The running animation owns the pan, so it lands on the target it aimed at on the preview: the same part of
        // the photo only when the HQ is the preview's size (a few pixels off for a sensor border).
        var c = Case(name);
        // Both spring kinds: double-click 100% (pan + zoom) and a wheel notch (anchored zoom).
        Action<Rig>[] zooms =
        [
            r => r.M.ZoomToHundred(new Point(300, 200)),
            r => r.M.WheelZoom(ZoomDirection.In, new Point(300, 200)),
        ];
        foreach (var zoom in zooms)
        {
            var reference = new Rig(); // the same zoom with no HQ arriving
            ShowPreview(reference, "A", c);
            zoom(reference);
            reference.Settle();

            var r = new Rig();
            ShowPreview(r, "A", c);
            zoom(r);
            r.Tick(3);
            ShowHq(r, "A", c);
            Assert.IsTrue(r.M.IsAnimating, "the zoom keeps going");
            r.Settle();
            Near(reference.Scale / PerPhoto(c), r.Scale, 1e-5, "zoom");
        }
    }

    [TestMethod]
    [DynamicData(nameof(RawCaseNames))]
    public void RetainAndRemember_RawHq_At100_StaysTrue1To1AcrossPhotos_WithoutAJump(string name)
    {
        var c = Case(name);
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        ShowPreview(r, "P0", c);
        ShowHq(r, "P0", c);
        r.M.ZoomToHundred();
        r.Settle();
        for (var i = 1; i <= 10; i++)
        {
            if (i % 2 == 1) // every other photo's HQ is already cached, so it comes with no preview first
            {
                ShowPreview(r, "P" + i, c);
                var onPreview = CentreFraction(r);
                ShowHq(r, "P" + i, c);
                NoJump(onPreview, r, $"no jump on photo {i}");
            }
            else
            {
                ShowHq(r, "P" + i, c);
            }
            Near(1, r.Scale, 1e-6, $"retained on photo {i}");
            Assert.IsTrue(r.OneToOne, $"1:1 on photo {i}");
        }

        if (c.HqFailed) return; // the error icon at 100% is its default view, which is not remembered
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        ShowPreview(r, "Q", c);
        ShowHq(r, "Q", c);
        ShowPreview(r, "P10", c);
        ShowHq(r, "P10", c);
        Near(1, r.Scale, 1e-6, "remembered");
    }

    [TestMethod]
    public void Retain_True1To1_LandscapeToPortraitCrop_StaysTrue1To1()
    {
        // Found in the UI test: Rawler crops 24 px off each axis of an ARW, a smaller share of the long side, which is
        // a portrait photo's height on screen.
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        var landscape = ViewRules.PreviewLayoutSize(new Size(6024, 4024), 1024, 684);
        var portrait = new Size(landscape.Height, landscape.Width); // a portrait photo's preview, upright on screen
        r.Show("A", landscape.Width, landscape.Height, photoSize: landscape);
        r.Show("A", 6000, 4000, photoSize: landscape);
        r.M.ZoomToHundred();
        r.Settle();

        r.Show("B", 6000, 4000, rotation: 270, photoSize: portrait); // its HQ, already prefetched
        Near(1, r.Scale, 1e-5, "portrait");
        Assert.IsTrue(r.OneToOne);
        r.Show("C", landscape.Width, landscape.Height, photoSize: landscape); // a landscape photo: preview, then HQ
        r.Show("C", 6000, 4000, photoSize: landscape);
        Near(1, r.Scale, 1e-5, "landscape again");
    }

    [TestMethod]
    public void Retain_True1To1_ToAndFromAPhotoWithNoMetadata_StaysTrue1To1()
    {
        // Found in the UI test: ImageMagick found no preview in one CR3, so its size by metadata wasn't known.
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        var withMetadata = ViewRules.PreviewLayoutSize(new Size(5496, 3670), 800, 534);
        r.Show("A", 5472, 3648); // its HQ, with no preview
        r.M.ZoomToHundred();
        r.Settle();

        r.Show("B", 5472, 3648, photoSize: withMetadata);
        Near(1, r.Scale, 1e-6, "to a photo with metadata");
        r.Show("A", 5472, 3648);
        Near(1, r.Scale, 1e-6, "back");
        Assert.IsTrue(r.OneToOne);
    }

    [TestMethod]
    public void Retain_True1To1_FromAPhotoWhosePreviewCameAfterItsHq_ThroughAPreview()
    {
        // Found in the UI test: the first photo opened was a CR3 with no quick preview, so its HQ went up first and
        // its preview (with the metadata size) loaded later; the canvas controller passes that size on leaving it.
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        var layout = ViewRules.PreviewLayoutSize(new Size(5496, 3670), 800, 534);
        r.Show("A", 5472, 3648);
        r.M.ZoomToHundred();
        r.Settle();

        r.M.LearnPhotoSize(layout);
        r.Show("B", layout.Width, layout.Height, photoSize: layout);
        r.Show("B", 5472, 3648, photoSize: layout);
        Near(1, r.Scale, 1e-6, "true 100% on B's HQ");
        Assert.IsTrue(r.OneToOne);
    }

    [TestMethod]
    [DynamicData(nameof(RawCaseNames))]
    public void Remember_RawHq_PannedView_KeepsTheSamePartOnEveryRevisit(string name)
    {
        // The view is saved on the HQ and restored onto the preview, laid out at the metadata size (#195 review).
        var c = Case(name);
        SetMode(PanZoomBehaviourOnNavigation.RememberPerPhoto);
        var r = new Rig();
        ShowPreview(r, "P", c);
        ShowHq(r, "P", c);
        r.M.ZoomBy(7f, new Point(250, 600));
        r.M.Pan(30, -20);
        var (scale, part) = (r.Scale, CentreFraction(r));

        for (var i = 1; i <= 5; i++)
        {
            ShowPreview(r, "Q", c);
            ShowHq(r, "Q", c);
            ShowPreview(r, "P", c);
            SamePartAtCentre(part, r); // already on the preview
            ShowHq(r, "P", c);
            Near(scale, r.Scale, 1e-6, $"zoom on revisit {i}");
            SamePartAtCentre(part, r);
        }
    }

    [TestMethod]
    public void ShrugInterruptedByNavigation_LeavesNoOffset()
    {
        SetMode(PanZoomBehaviourOnNavigation.RetainFromLastPhoto);
        var r = new Rig();
        r.Show("A", 4000, 3000);
        r.M.ZoomBy(2f, Centre);
        r.M.Pan(40, 0);
        var pos = r.Pos;
        r.M.Shrug();
        r.Tick(3);
        r.Show("B", 4000, 3000);
        NearPoint(pos, r.Pos, 1e-9, "same pan on the next photo");
    }
}
