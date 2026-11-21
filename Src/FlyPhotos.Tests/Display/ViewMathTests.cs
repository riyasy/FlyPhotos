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
/// The view's pure parts: <see cref="ZoomGeometry"/>'s rules, <see cref="PerPhotoViewStore"/>'s normalization,
/// <see cref="CanvasController.ClassifyArrival"/> and <see cref="CanvasViewState"/>'s transform.
/// </summary>
[TestClass]
public sealed class ViewMathTests
{
    private static readonly Size Canvas = new(1000, 800);
    private static readonly Size Photo = new(4000, 3000);

    [TestInitialize]
    public void Setup() => AppConfig.Settings = new AppSettings();

    private static void Near(double expected, double actual, double tolerance = 1e-6, string what = "") =>
        Assert.IsTrue(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    private static void NearPoint(Point expected, Point actual, double tolerance = 1e-3)
    {
        Near(expected.X, actual.X, tolerance, "X");
        Near(expected.Y, actual.Y, tolerance, "Y");
    }

    // ---------------------------------------------------------------- fit / default / floor

    [TestMethod]
    [DataRow(0, 0.25)]
    [DataRow(90, 0.2)]
    [DataRow(180, 0.25)]
    [DataRow(270, 0.2)]
    [DataRow(-90, 0.2)]
    [DataRow(450, 0.2)]
    [DataRow(360, 0.25)]
    public void FitScale_FollowsTheRotation(int rotation, double expected) =>
        Near(expected, ZoomGeometry.CalculateScreenFitScale(Canvas, Photo, rotation));

    [TestMethod]
    public void FitScale_UsesTheSmallerAxis_AndThePadding()
    {
        Near(0.2, ZoomGeometry.CalculateScreenFitScale(Canvas, new Size(3000, 4000), 0), what: "portrait: height limits");
        AppConfig.Settings.ImageFitPercentage = 50;
        Near(0.125, ZoomGeometry.CalculateScreenFitScale(Canvas, Photo, 0), what: "50% padding");
    }

    [TestMethod]
    public void FitScale_EmptyCanvasIsZero() =>
        Near(0, ZoomGeometry.CalculateScreenFitScale(new Size(0, 0), Photo, 0));

    [TestMethod]
    public void DefaultScale_NeverAbove100_UnlessStretching()
    {
        Near(0.25, ZoomGeometry.DefaultScale(Canvas, Photo, 0), what: "large: the fit");
        Near(1, ZoomGeometry.DefaultScale(Canvas, new Size(200, 100), 0), what: "small: 100%");
        AppConfig.Settings.StretchSmallImages = true;
        Near(5, ZoomGeometry.DefaultScale(Canvas, new Size(200, 100), 0), what: "small, stretched: the fit");
    }

    [TestMethod]
    public void MinZoomOutScale_Is5Percent_OrA50PxLongSide()
    {
        Near(0.05, ZoomGeometry.MinZoomOutScale(new Size(800, 600)), what: "normal image");
        Near(0.0125, ZoomGeometry.MinZoomOutScale(Photo), what: "4000 px: 50 px long side");
        Near(0.0005, ZoomGeometry.MinZoomOutScale(new Size(30000, 100000)), what: "huge, tall");
        Near(0.05, ZoomGeometry.MinZoomOutScale(new Size(0, 0)), what: "empty");
    }

    // ---------------------------------------------------------------- zoom steps

    [TestMethod]
    public void AnchorPreservingPan_KeepsTheAnchorOverTheSamePixel()
    {
        NearPoint(new Point(700, 500), ZoomGeometry.AnchorPreservingPan(new Point(300, 300), new Point(500, 400), 0.25f, 0.5f));
        NearPoint(new Point(500, 400), ZoomGeometry.AnchorPreservingPan(new Point(300, 300), new Point(500, 400), 1f, 1f));
        NearPoint(new Point(500, 400), ZoomGeometry.AnchorPreservingPan(new Point(500, 400), new Point(500, 400), 1f, 3f));
    }

    [TestMethod]
    public void WheelStep_Is125PercentIn_80PercentOut()
    {
        AppConfig.Settings.StickyZoomLevels = false;
        Near(0.3125, ZoomGeometry.WheelStepTarget(0.25f, ZoomDirection.In, Photo)!.Value);
        Near(0.2, ZoomGeometry.WheelStepTarget(0.25f, ZoomDirection.Out, Photo)!.Value);
    }

    [TestMethod]
    public void WheelStep_SnapsWhenSticky()
    {
        AppConfig.Settings.StickyZoomLevels = true;
        Near(1, ZoomGeometry.WheelStepTarget(0.85f, ZoomDirection.In, Photo)!.Value, what: "crosses 100%");
        Near(0.5, ZoomGeometry.WheelStepTarget(0.6f, ZoomDirection.Out, Photo)!.Value, what: "crosses 50%");
        Near(1.25, ZoomGeometry.WheelStepTarget(1f, ZoomDirection.In, Photo)!.Value, what: "from a stop: none crossed");
    }

    [TestMethod]
    public void WheelStep_OutBelowTheFloor_IsRefused_InNever()
    {
        Assert.IsNull(ZoomGeometry.WheelStepTarget(0.013f, ZoomDirection.Out, Photo));
        Assert.IsNotNull(ZoomGeometry.WheelStepTarget(0.0001f, ZoomDirection.In, Photo));
    }

    [TestMethod]
    [DataRow(1.0625f, 0.8f, 1.0f)]   // in, crosses 1
    [DataRow(1.25f, 1.0f, 1.25f)]    // in from a stop: 1 is not crossed
    [DataRow(12f, 0.4f, 0.5f)]       // in past several: the first
    [DataRow(1.0f, 1.25f, 1.0f)]     // out onto 1
    [DataRow(0.4f, 2.2f, 2.0f)]      // out past several: the nearest
    [DataRow(0.36f, 0.45f, 0.36f)]   // out, none crossed
    public void ApplyZoomSnap(float newScale, float oldScale, float expected) =>
        Near(expected, ZoomGeometry.ApplyZoomSnap(newScale, oldScale, newScale > oldScale ? ZoomDirection.In : ZoomDirection.Out));

    [TestMethod]
    [DataRow(1.07f, 0.97f, 1f)]
    [DataRow(0.97f, 1.07f, 1f)]
    [DataRow(1.0f, 0.97f, 1f)]
    [DataRow(1.2f, 1.1f, 1.2f)]
    [DataRow(0.9f, 1.0f, 0.9f)]      // starting on the stop is not crossing it
    public void StopOnCrossing(float newScale, float oldScale, float expected) =>
        Near(expected, ZoomGeometry.StopOnCrossing(newScale, oldScale, 1f));

    [TestMethod]
    public void ZoomStops_AreFit100And400_Deduplicated()
    {
        CollectionAssert.AreEqual(new[] { 0.25f, 1f, 4f }, ZoomGeometry.BuildZoomStops(0.25f));
        CollectionAssert.AreEqual(new[] { 1f, 4f }, ZoomGeometry.BuildZoomStops(1f));
        CollectionAssert.AreEqual(new[] { 1f, 2f, 4f }, ZoomGeometry.BuildZoomStops(2f));
        CollectionAssert.AreEqual(new[] { 1f, 4f, 5f }, ZoomGeometry.BuildZoomStops(5f));
    }

    [TestMethod]
    public void NextZoomStop_WalksTheStops_AndIsNullAtTheEnds()
    {
        Near(1, ZoomGeometry.NextZoomStop(0.25f, 0.25f, ZoomDirection.In)!.Value);
        Near(4, ZoomGeometry.NextZoomStop(1f, 0.25f, ZoomDirection.In)!.Value);
        Assert.IsNull(ZoomGeometry.NextZoomStop(4f, 0.25f, ZoomDirection.In));
        Near(0.25, ZoomGeometry.NextZoomStop(0.5f, 0.25f, ZoomDirection.Out)!.Value, what: "between stops: the one below");
        Assert.IsNull(ZoomGeometry.NextZoomStop(0.25f, 0.25f, ZoomDirection.Out));
        Near(4, ZoomGeometry.NextZoomStop(1.0005f, 0.25f, ZoomDirection.In)!.Value, what: "within tolerance of 1 counts as 1");
        Assert.IsNull(ZoomGeometry.NextZoomStop(0.1f, 0.25f, ZoomDirection.Out), "below fit: nothing lower");
    }

    // ---------------------------------------------------------------- upgrade ratio

    [TestMethod]
    public void UpgradeRatio_SameImage_IsOne()
    {
        Near(1, ZoomGeometry.UpgradeScaleRatio(Canvas, Photo, 0, null, Photo, 0, null, fitted: false));
        Near(1, ZoomGeometry.UpgradeScaleRatio(Canvas, Photo, 0, null, Photo, 0, null, fitted: true));
    }

    [TestMethod]
    public void UpgradeRatio_NotFittedNoMetadata_KeepsTheOnScreenHeight()
    {
        Near(0.1, ZoomGeometry.UpgradeScaleRatio(Canvas, new Size(400, 300), 0, null, Photo, 0, null, fitted: false), what: "no metadata");
        Near(0.75, ZoomGeometry.UpgradeScaleRatio(Canvas, new Size(2250, 3000), 0, null, Photo, 90, null, fitted: false), what: "upright preview");
        Near(1, ZoomGeometry.UpgradeScaleRatio(new Size(1000, 1000), Photo, 0, null, new Size(4500, 3000), 0, null, fitted: false),
            what: "only the aspect differs: 100% stays 100%");
    }

    [TestMethod]
    public void UpgradeRatio_NotFitted_SmallerHq_KeepsTheOnScreenHeight() =>
        Near(3670 / 1080.0, ZoomGeometry.UpgradeScaleRatio(Canvas, new Size(5496, 3670), 0, null, new Size(1616, 1080), 0, null, fitted: false));

    [TestMethod]
    public void UpgradeRatio_NotFitted_KnownPhotoSize_GoesByTheRatioToThePhoto() =>
        Near(1 / 0.25, ZoomGeometry.UpgradeScaleRatio(Canvas, Photo, 0, 1f, new Size(1000, 760), 0, 0.25f, fitted: false));

    [TestMethod]
    public void ImagePixelsPerPhotoPixel_IsTheLargerAxisRatio()
    {
        var photo = new Size(6024, 4024);
        Near(1, ZoomGeometry.ImagePixelsPerPhotoPixel(photo, 0, photo).Value, what: "the preview, laid out at the photo's size");
        Near(6000 / 6024.0, ZoomGeometry.ImagePixelsPerPhotoPixel(new Size(6000, 4000), 0, photo).Value, what: "Rawler crop");
        Near(6000 / 6024.0, ZoomGeometry.ImagePixelsPerPhotoPixel(new Size(6000, 4000), 270, new Size(4024, 6024)).Value,
            what: "the same crop on a portrait photo, HQ in sensor axes");
        Near(1080 / 4024.0, ZoomGeometry.ImagePixelsPerPhotoPixel(new Size(1616, 1080), 0, photo).Value, what: "embedded JPEG");
        Assert.IsNull(ZoomGeometry.ImagePixelsPerPhotoPixel(new Size(1616, 1080), 0, null), "no metadata");
    }

    [TestMethod]
    public void UpgradeRatio_Fitted_KeepsTheFit()
    {
        var square = new Size(1000, 1000);
        var ratio = ZoomGeometry.UpgradeScaleRatio(square, Photo, 0, null, new Size(4500, 3000), 0, null, fitted: true);
        Near(ZoomGeometry.CalculateScreenFitScale(square, new Size(4500, 3000), 0),
            ZoomGeometry.CalculateScreenFitScale(square, Photo, 0) * ratio, what: "lands on the new fit");
    }

    [TestMethod]
    public void UpgradeRatio_EmptySizes_AreOne() =>
        Near(1, ZoomGeometry.UpgradeScaleRatio(Canvas, Photo, 0, null, new Size(0, 0), 0, null, fitted: false));

    [TestMethod]
    public void UpgradeRatio_WhenFitBindsTheHeight_TheTwoRulesAgree()
    {
        // Landscape window, preview and HQ of different aspect but both limited by the height: either rule gives 1.
        var wide = new Size(2000, 800);
        Near(1, ZoomGeometry.UpgradeScaleRatio(wide, Photo, 0, null, new Size(4500, 3000), 0, null, fitted: true));
        Near(1, ZoomGeometry.UpgradeScaleRatio(wide, Photo, 0, null, new Size(4500, 3000), 0, null, fitted: false));
    }

    [TestMethod]
    [DataRow(0, 4000.0, 3000.0)]
    [DataRow(90, 3000.0, 4000.0)]
    [DataRow(270, 3000.0, 4000.0)]
    [DataRow(180, 4000.0, 3000.0)]
    public void OnScreenSize(int rotation, double width, double height)
    {
        var size = ZoomGeometry.OnScreenSize(Photo, rotation);
        Near(width, size.Width);
        Near(height, size.Height);
    }

    // ---------------------------------------------------------------- pan rules

    [TestMethod]
    public void CentreOnImage_IsTheFractionOfTheOnScreenBoxAtTheCanvasCentre()
    {
        NearPoint(new Point(0.5, 0.5), ZoomGeometry.CentreOnImage(new Point(500, 400), 0.25f, Photo, 0, Canvas));
        // 4000×3000 at 0.25 is 1000×750 on screen; its centre 200 px left of the canvas centre.
        NearPoint(new Point(0.7, 0.5), ZoomGeometry.CentreOnImage(new Point(300, 400), 0.25f, Photo, 0, Canvas));
        // Turned 90°: 750×1000 on screen; its centre 200 px above.
        NearPoint(new Point(0.5, 0.7), ZoomGeometry.CentreOnImage(new Point(500, 200), 0.25f, Photo, 90, Canvas));
        NearPoint(new Point(0.5, 0.5), ZoomGeometry.CentreOnImage(new Point(300, 200), 0.25f, new Size(0, 0), 0, Canvas));
    }

    [TestMethod]
    public void ImagePosFor_InvertsCentreOnImage()
    {
        var pos = new Point(623, 371);
        var centre = ZoomGeometry.CentreOnImage(pos, 0.3f, Photo, 270, Canvas);
        NearPoint(pos, ZoomGeometry.ImagePosFor(centre, 0.3f, Photo, 270, Canvas));
    }

    [TestMethod]
    public void CentreOnImage_LandsAlikeOnAnHqInSensorAxesAndAnUprightOne()
    {
        // The same portrait photo as two decoders make it: sensor axes with a 270° rotation, or already upright.
        var centre = ZoomGeometry.CentreOnImage(new Point(300, 250), 0.25f, Photo, 0, Canvas);
        NearPoint(ZoomGeometry.ImagePosFor(centre, 0.25f, new Size(4032, 6048), 0, Canvas),
            ZoomGeometry.ImagePosFor(centre, 0.25f, new Size(6048, 4032), 270, Canvas));
    }

    [TestMethod]
    public void ResizedPan_KeepsTheRelativePlace()
    {
        NearPoint(new Point(960, 562.5), ZoomGeometry.ResizedPan(new Point(600, 500), Canvas, new Size(1600, 900)));
        NearPoint(new Point(600, 500), ZoomGeometry.ResizedPan(new Point(600, 500), new Size(0, 0), Canvas));
    }

    [TestMethod]
    public void IsCentred_WithinOnePixel()
    {
        Assert.IsTrue(ZoomGeometry.IsCentred(new Point(500.9, 399.1), Canvas));
        Assert.IsFalse(ZoomGeometry.IsCentred(new Point(501, 400), Canvas));
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(360, 0)]
    [DataRow(-90, 270)]
    [DataRow(450, 90)]
    [DataRow(-720, 0)]
    public void NormalizeDegrees(int degrees, int expected) => Assert.AreEqual(expected, ZoomGeometry.NormalizeDegrees(degrees));

    [TestMethod]
    public void SameScale_WithinTheTolerance()
    {
        Assert.IsTrue(ZoomGeometry.SameScale(1f, 1.0009f));
        Assert.IsFalse(ZoomGeometry.SameScale(1f, 1.0011f));
    }

    // ---------------------------------------------------------------- preview layout

    [TestMethod]
    [DataRow(4000, 6000, 400, 600, 4000, 6000)]   // upright preview, upright size
    [DataRow(6000, 4000, 400, 600, 4000, 6000)]   // upright (cached) preview, size in sensor orientation
    [DataRow(4000, 6000, 600, 400, 6000, 4000)]   // preview still to be rotated (NEF), upright size
    [DataRow(6000, 4000, 600, 400, 6000, 4000)]   // both in sensor orientation
    [DataRow(4000, 3000, 300, 300, 3000, 3000)]   // square preview: no orientation to match, the height is kept
    [DataRow(4000, 3000, 400, 290, 4137.931, 3000)] // a slightly different aspect: the height kept, the width matched
    public void PreviewLayoutSize_IsTheFullSizeInThePreviewsAxes(double fw, double fh, double pw, double ph,
        double ew, double eh)
    {
        var s = ZoomGeometry.PreviewLayoutSize(new Size(fw, fh), pw, ph);
        Near(ew, s.Width, 1e-3, "width");
        Near(eh, s.Height, 1e-3, "height");
    }

    // ---------------------------------------------------------------- PerPhotoViewStore

    [TestMethod]
    public void Store_RoundTrip_ReBasesTheUserRotation()
    {
        var store = new PerPhotoViewStore();
        store.Save("A", 0.5f, new Point(0.7, 0.4), 90, 0);

        Assert.IsTrue(store.TryRestore("A", 0, out var same));
        Near(0.5, same.PhotoZoom);
        NearPoint(new Point(0.7, 0.4), same.CentreOnImage);
        Assert.AreEqual(90, same.Rotation);

        Assert.IsTrue(store.TryRestore("A", 90, out var moved));
        Assert.AreEqual(180, moved.Rotation, "user's 90 onto an EXIF 90");
    }

    [TestMethod]
    public void Store_RemoveRenameMissing()
    {
        var store = new PerPhotoViewStore();
        var centre = new Point(0.5, 0.5);
        Assert.IsFalse(store.TryRestore("A", 0, out _), "nothing stored");
        store.Save("A", 0.5f, centre, 0, 0);
        store.Rename("A", "B");
        Assert.IsFalse(store.TryRestore("A", 0, out _));
        Assert.IsTrue(store.TryRestore("B", 0, out _));
        store.Rename("missing", "C"); // no-op
        store.Remove("B");
        Assert.IsFalse(store.TryRestore("B", 0, out _));
    }

    // ---------------------------------------------------------------- ClassifyArrival

    [TestMethod]
    public void ClassifyArrival_CoversEveryInstall()
    {
        Assert.AreEqual(ViewArrival.FirstPhoto, CanvasController.ClassifyArrival("", false, "a", DisplayLevel.Preview));
        Assert.AreEqual(ViewArrival.FirstPhoto, CanvasController.ClassifyArrival("", false, "a", DisplayLevel.PlaceHolder));
        Assert.AreEqual(ViewArrival.NewPhoto, CanvasController.ClassifyArrival("a", true, "b", DisplayLevel.Hq));
        Assert.AreEqual(ViewArrival.NewPhoto, CanvasController.ClassifyArrival("a", false, "b", DisplayLevel.PlaceHolder));
        Assert.AreEqual(ViewArrival.FirstRealImage, CanvasController.ClassifyArrival("a", false, "a", DisplayLevel.Preview));
        Assert.AreEqual(ViewArrival.FirstRealImage, CanvasController.ClassifyArrival("a", false, "a", DisplayLevel.Hq));
        Assert.AreEqual(ViewArrival.SameImageUpgrade, CanvasController.ClassifyArrival("a", true, "a", DisplayLevel.Hq));
        Assert.AreEqual(ViewArrival.SameImageUpgrade, CanvasController.ClassifyArrival("a", true, "a", DisplayLevel.Preview),
            "a reload at the same level");
        Assert.AreEqual(ViewArrival.SameImageUpgrade, CanvasController.ClassifyArrival("a", false, "a", DisplayLevel.PlaceHolder),
            "the placeholder again");
    }

    // ---------------------------------------------------------------- CanvasViewState

    [TestMethod]
    [DataRow(0.37f, 0)]
    [DataRow(1f, 0)]
    [DataRow(2.3f, 90)]
    [DataRow(0.1234f, 270)]
    public void SnapToPixelGrid_LandsTheTranslationOnWholePixels(float scale, int rotation)
    {
        var v = new CanvasViewState { ImageRect = new Rect(0, 0, 4001, 2999), Rotation = rotation, Scale = scale };
        var raw = new Point(123.37, 456.81);
        v.ImagePos = v.SnapImagePosToPixelGrid(scale, raw);
        Assert.IsTrue(Math.Abs(v.ImagePos.X - raw.X) <= 0.5 + 1e-4 && Math.Abs(v.ImagePos.Y - raw.Y) <= 0.5 + 1e-4, "≤ 0.5 px nudge");
        v.SnapTranslation = false;
        v.UpdateTransform();
        Near(Math.Round(v.Mat.M31), v.Mat.M31, 2e-3, "M31");
        Near(Math.Round(v.Mat.M32), v.Mat.M32, 2e-3, "M32");
    }

    [TestMethod]
    public void Transform_MapsTheImageCentreToImagePos_AndInverts()
    {
        var v = new CanvasViewState { ImageRect = new Rect(0, 0, 4000, 3000), Rotation = 90, Scale = 0.5f, ImagePos = new Point(600, 450) };
        v.SnapTranslation = false;
        v.UpdateTransform();
        var c = Vector2.Transform(new Vector2(2000, 1500), v.Mat);
        Near(600, c.X, 1e-3, "centre X");
        Near(450, c.Y, 1e-3, "centre Y");
        var corner = Vector2.Transform(new Vector2(0, 0), v.Mat); // rotated 90°: top-left goes to the top-right
        Near(600 + 750, corner.X, 1e-3, "corner X");
        Near(450 - 1000, corner.Y, 1e-3, "corner Y");
        var back = Vector2.Transform(corner, v.MatInv);
        Near(0, back.X, 1e-2, "inverse X");
        Near(0, back.Y, 1e-2, "inverse Y");
    }
}
