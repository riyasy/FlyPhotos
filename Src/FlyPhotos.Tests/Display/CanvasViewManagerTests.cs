using System.Diagnostics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The pan/zoom view on its own (no window, no Win2D): one test per fixed defect. Animations are run to their end by
/// ticking in real time, so a test that settles one takes about a second.
/// </summary>
[TestClass]
public sealed class CanvasViewManagerTests
{
    private CanvasViewState _view = null!;
    private CanvasViewManager _m = null!;
    private bool _fitted, _oneToOne;
    private static readonly Size Canvas = new(1000, 800);
    private static readonly Point Centre = new(500, 400);

    [TestInitialize]
    public void Setup()
    {
        AppConfig.Settings = new AppSettings { StickyZoomLevels = false };
        _view = new CanvasViewState();
        _m = new CanvasViewManager(_view);
        _m.FitToScreenStateChanged += v => _fitted = v;
        _m.OneToOneStateChanged += v => _oneToOne = v;
    }

    // A photo arriving: first ever, a new photo, or a better image of the same photo.
    private void ShowFirst(string path, Size size, int rotation = 0) =>
        _m.SetScaleAndPosition(path, size, rotation, Canvas, isFirstPhotoEver: true, isNewPhoto: true, isUpgradeFromPlaceholder: false);
    private void ShowNew(string path, Size size, int rotation = 0) =>
        _m.SetScaleAndPosition(path, size, rotation, Canvas, isFirstPhotoEver: false, isNewPhoto: true, isUpgradeFromPlaceholder: false);
    private void Upgrade(string path, Size size, int rotation = 0, Size? canvas = null) =>
        _m.SetScaleAndPosition(path, size, rotation, canvas ?? Canvas, isFirstPhotoEver: false, isNewPhoto: false, isUpgradeFromPlaceholder: false);

    private void Settle()
    {
        var sw = Stopwatch.StartNew();
        while (_m.PanZoomAnimationOnGoing)
        {
            Assert.IsTrue(sw.ElapsedMilliseconds < 5000, "animation never settled");
            Thread.Sleep(5);
            _m.OnUpdate();
        }
    }

    private static void AreNear(double expected, double actual, double tolerance, string message) =>
        Assert.IsTrue(Math.Abs(expected - actual) <= tolerance, $"{message}: expected {expected}, got {actual}");

    [TestMethod]
    public void RememberPerPhoto_LeavingMidZoom_SavesTheZoomTarget()
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RememberPerPhoto;
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomAtPoint(ZoomDirection.In, Centre); // heading to 0.25 * 1.25; not ticked, so still at 0.25 on screen
        var target = _view.LastScaleTo;

        _m.CacheCurrentViewState("A", Canvas);
        ShowNew("B", new Size(4000, 3000));
        _m.CacheCurrentViewState("B", Canvas);
        ShowNew("A", new Size(4000, 3000));

        AreNear(target, _view.Scale, 1e-6, "restored scale");
        AreNear(target, _view.LastScaleTo, 1e-6, "restored target");
    }

    [TestMethod]
    public void Fit_OnPreviewWithOtherAspect_FitsWhatIsShown_AndHqStaysFitted()
    {
        var square = new Size(1000, 1000); // the width limits the fit
        _m.SetScaleAndPosition("A", new Size(4000, 3000), 0, square, true, true, false); // 4:3 preview layout
        _m.ZoomToHundred(square);
        Settle();

        _m.ZoomPanToFit(animateChange: true, square);
        Settle();
        AreNear(0.25, _view.Scale, 1e-4, "fitted to the preview as laid out");
        Assert.IsTrue(_fitted, "fitted after the fit animation");

        Upgrade("A", new Size(4500, 3000), canvas: square); // 3:2 HQ
        AreNear(1000.0 / 4500, _view.Scale, 1e-4, "HQ re-fitted");
        Assert.IsTrue(_fitted, "HQ fitted");
    }

    [TestMethod]
    public void DragDuringFitAnimation_EndsNotFitted()
    {
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomToHundred(Canvas);
        Settle();

        _m.ZoomPanToFit(animateChange: true, Canvas);
        _m.Pan(30, 0);
        Settle();

        Assert.IsFalse(_fitted, "panned off centre: not fitted");
    }

    [TestMethod]
    public void AnchoredStepZoomToFit_IsNotFitted()
    {
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomToHundred(Canvas);
        Settle();

        _m.StepZoom(ZoomDirection.Out, Canvas, new Point(100, 100)); // 100% -> fit, keeping (100,100) still
        Settle();

        AreNear(0.25, _view.Scale, 1e-4, "at the fit scale");
        Assert.IsFalse(_fitted, "off centre: not fitted");
    }

    [TestMethod]
    public void ResizeDuringFitAnimation_SettlesFittedToTheNewCanvas()
    {
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomToHundred(Canvas);
        Settle();

        _m.ZoomPanToFit(animateChange: true, Canvas);
        var bigger = new Size(1600, 900);
        _m.HandleSizeChange(bigger, Canvas);
        Settle();

        AreNear(900.0 / 3000, _view.Scale, 1e-4, "fit scale of the new canvas");
        AreNear(800, _view.ImagePos.X, 1, "centred X");
        AreNear(450, _view.ImagePos.Y, 1, "centred Y");
        Assert.IsTrue(_fitted, "fitted");
    }

    [TestMethod]
    public void ResizeDuringWheelZoom_JumpsTheZoomToItsEnd()
    {
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomAtPoint(ZoomDirection.In, new Point(200, 200));

        _m.HandleSizeChange(new Size(1600, 900), Canvas);

        Assert.IsFalse(_m.PanZoomAnimationOnGoing, "no animation left to overwrite the resize");
        AreNear(_view.LastScaleTo, _view.Scale, 1e-6, "at the zoom target");
    }

    [TestMethod]
    public void Upgrade_AtOneToOne_WithOtherAspect_StaysAtOneToOne()
    {
        var square = new Size(1000, 1000);
        _m.SetScaleAndPosition("A", new Size(4000, 3000), 0, square, true, true, false);
        _m.ZoomToHundred(square);
        Settle();
        var pos = _view.ImagePos;

        Upgrade("A", new Size(4500, 3000), canvas: square);

        AreNear(1.0, _view.Scale, 1e-6, "still 100%");
        Assert.IsTrue(_oneToOne, "1:1");
        AreNear(pos.X, _view.ImagePos.X, 1e-6, "centre X unchanged");
        AreNear(pos.Y, _view.ImagePos.Y, 1e-6, "centre Y unchanged");
    }

    [TestMethod]
    public void Upgrade_KeepsOnScreenHeight_WhenLayoutUnitsDiffer()
    {
        // A preview with no full-size metadata, laid out at its own small size.
        ShowFirst("A", new Size(400, 300));
        _m.ZoomAtPointByFactor(1.5f, Centre);
        var before = _view.Scale * 300;
        Upgrade("A", new Size(4000, 3000));
        AreNear(before, _view.Scale * 3000, 1e-3, "on-screen height, no metadata");

        // A preview already turned upright (no rotation) while the HQ carries a 90° EXIF rotation.
        ShowNew("B", new Size(2250, 3000));
        _m.ZoomAtPointByFactor(1.5f, Centre);
        before = _view.Scale * 3000;
        Upgrade("B", new Size(4000, 3000), rotation: 90);
        AreNear(before, _view.Scale * 4000, 1e-3, "on-screen height, rotated HQ");
    }

    [TestMethod]
    public void FineZoomStoppingAt100_IsOneToOne_AndDoubleClickDoesNotMoveTheImage()
    {
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomAtPointByFactor(3.9f, Centre); // 0.975
        _m.ZoomAtPointByFactor(1.1f, Centre, stopAtOneToOne: true);
        AreNear(1.0, _view.Scale, 1e-6, "stopped at 100%");
        Assert.IsTrue(_oneToOne, "1:1 on");

        _m.Pan(100, 50);
        var pos = _view.ImagePos;
        _m.ZoomToHundred(Canvas, new Point(200, 150));
        Settle();

        AreNear(pos.X, _view.ImagePos.X, 1, "X unchanged");
        AreNear(pos.Y, _view.ImagePos.Y, 1, "Y unchanged");
    }

    [TestMethod]
    public void StickyWheelZoomLandingOn100_IsOneToOne()
    {
        AppConfig.Settings.StickyZoomLevels = true;
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomAtPointByFactor(3.4f, Centre); // 0.85; one wheel step in crosses 100%

        _m.ZoomAtPoint(ZoomDirection.In, Centre);

        AreNear(1.0, _view.LastScaleTo, 1e-6, "snapped to 100%");
        Assert.IsTrue(_oneToOne, "1:1 on");
    }

    [TestMethod]
    public void ShrugInterruptedByNavigation_LeavesNoOffset()
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RetainFromLastPhoto;
        ShowFirst("A", new Size(4000, 3000));
        _m.ZoomAtPointByFactor(2f, Centre);
        _m.Pan(40, 0);
        var pos = _view.ImagePos;

        _m.Shrug();
        Thread.Sleep(40);
        _m.OnUpdate(); // mid-wiggle
        ShowNew("B", new Size(4000, 3000));

        AreNear(pos.X, _view.ImagePos.X, 0.01, "same pan on the next photo");
    }
}
