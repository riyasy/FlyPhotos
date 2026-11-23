using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;
using Windows.Foundation;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The view on the install orders the real pipeline makes: the whole display pipeline (<see cref="DisplayHarness"/>)
/// with a real <see cref="ViewController"/> in its canvas. Every scenario runs in each navigation mode, with an HQ
/// window of 0 and 2, in Explorer and Folder mode, on photos whose HQ is the preview's size and on photos whose HQ is
/// half of it (a RAW's embedded JPEG). Besides the harness invariants (no image jumps when a better one replaces it,
/// the view is of the photo on screen), each checks what the mode promises.
/// </summary>
[TestClass]
public sealed class PipelineViewTests
{
    private static readonly Size Canvas = new(1000, 800);
    private static readonly Point Centre = new(500, 400);

    /// <summary>Both larger than the canvas, so the default view is the fit.</summary>
    private static readonly ImageSizes SameSizeHq = new(1200, 800, 240, 160, 1200, 800);
    private static readonly ImageSizes HalfSizeHq = new(2400, 1600, 240, 160, 1200, 800);

    public static IEnumerable<object[]> Rows =>
        from mode in new[] { PanZoomBehaviourOnNavigation.Reset, PanZoomBehaviourOnNavigation.RetainFromLastPhoto,
            PanZoomBehaviourOnNavigation.RememberPerPhoto }
        from hqHalf in new[] { 0, 2 }
        from source in new[] { ListSource.Explorer, ListSource.Folder }
        from halfHq in new[] { false, true }
        select new object[] { mode, hqHalf, source, halfHq };

    private sealed class Rig : IDisposable
    {
        public readonly DisplayHarness H;
        public readonly ViewController V;
        public readonly PanZoomBehaviourOnNavigation Mode;
        public readonly bool HalfHq;
        public float ZoomedScale;
        public Point ZoomedPos, ZoomedCentre;

        public Rig(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq, int photos, int openAt)
        {
            H = new DisplayHarness(photos: photos, openAt: openAt, hqHalf: hqHalf, source: source);
            AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
            (Mode, HalfHq) = (mode, halfHq);
            H.Decoder.Sizes = halfHq ? HalfSizeHq : SameSizeHq;
            V = new ViewController(() => 0) { CanvasSize = Canvas };
            H.Canvas.View = V;
        }

        public Rig Open()
        {
            H.Open();
            H.CheckInvariants();
            return this;
        }

        /// <summary>The user zooms the photo on screen 2× and pans it, at once.</summary>
        public void ZoomAndPan()
        {
            V.ZoomBy(2f, Centre);
            V.Pan(30, -20);
            (ZoomedScale, ZoomedPos, ZoomedCentre) = (V.View.Scale, V.View.Pos, CentreOnImage);
        }

        public Point CentreOnImage =>
            ViewRules.CentreOnImage(V.View.Pos, V.View.Scale, V.View.ImageSize, V.View.Rotation, Canvas);

        public void Settle()
        {
            H.Settle();
            H.CheckInvariants();
        }

        /// <summary>
        /// A photo not seen before: Reset and Remember fit it. Retain keeps the scale and the part at the centre, checked
        /// where the HQ is the preview's size (otherwise it depends on whether the photo's size was known in time).
        /// </summary>
        public void ExpectNewPhotoView(string what)
        {
            if (Mode != PanZoomBehaviourOnNavigation.RetainFromLastPhoto)
            {
                ExpectFit(what);
                return;
            }
            if (HalfHq) return;
            Near(ZoomedScale, V.View.Scale, ZoomedScale * 1e-4, $"{what}: the zoom retained");
            Near(ZoomedCentre.X, CentreOnImage.X, 1e-3, $"{what}: the same part at the centre, X");
            Near(ZoomedCentre.Y, CentreOnImage.Y, 1e-3, $"{what}: the same part at the centre, Y");
        }

        /// <summary>Back on the zoomed photo: Reset fits it, Remember gives its view back, Retain as for a new one.</summary>
        public void ExpectReturnView(string what)
        {
            switch (Mode)
            {
                case PanZoomBehaviourOnNavigation.Reset:
                    ExpectFit(what);
                    break;
                case PanZoomBehaviourOnNavigation.RememberPerPhoto:
                    Near(ZoomedScale, V.View.Scale, ZoomedScale * 1e-4, $"{what}: its zoom back");
                    Near(ZoomedPos.X, V.View.Pos.X, 0.5, $"{what}: its pan back, X");
                    Near(ZoomedPos.Y, V.View.Pos.Y, 0.5, $"{what}: its pan back, Y");
                    break;
                default:
                    ExpectNewPhotoView(what);
                    break;
            }
        }

        public void ExpectFit(string what)
        {
            var fit = ViewRules.FitScale(Canvas, V.View.ImageSize, V.View.Rotation);
            Near(fit, V.View.Scale, fit * 1e-4, $"{what}: fitted");
            Assert.IsTrue(ViewRules.IsCentred(V.View.Pos, Canvas), $"{what}: centred");
        }

        public void Dispose() => H.Dispose();
    }

    private static void Near(double expected, double actual, double tolerance, string what) =>
        Assert.IsTrue(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void Burst_HeldKeyAwayAndBack(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 40, openAt: 10).Open();
        r.ZoomAndPan();

        for (var i = 0; i < 8; i++) r.H.Next(); // a held key: no loading between the repeats
        r.H.Brake();
        r.Settle();
        Assert.AreEqual(18, r.H.Nav.Position);
        r.ExpectNewPhotoView("8 on");

        for (var i = 0; i < 8; i++) r.H.Prev();
        r.H.Brake();
        r.Settle();
        Assert.AreEqual(10, r.H.Nav.Position);
        r.ExpectReturnView("back");
    }

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void FarJump_EndAndHome(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 120, openAt: 0).Open();
        r.ZoomAndPan();

        r.H.Run(r.H.Controller.FlyToLast);
        r.Settle();
        r.ExpectNewPhotoView("End");

        r.H.Run(r.H.Controller.FlyToFirst);
        r.Settle();
        r.ExpectReturnView("Home");
    }

    /// <summary>HQ decodes are slow: the user moves on before they finish, then they all finish late.</summary>
    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void SlowHqs_MovingOnBeforeTheyFinish(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 30, openAt: 5).Open();
        r.ZoomAndPan();
        r.H.Decoder.Script = d => d.Kind == DecodeKind.Hq ? Reply.Pending : Reply.Item;

        for (var i = 0; i < 3; i++)
        {
            r.H.Next();
            r.Settle();
        }
        r.H.Next();
        r.H.Next();
        r.H.Brake();
        r.Settle();
        r.H.Decoder.Script = _ => Reply.Item;
        r.H.Decoder.CompleteAll();
        r.Settle();
        Assert.AreEqual(DisplayLevel.Hq, r.H.ShownLevel);
        r.ExpectNewPhotoView("5 on, HQs late");

        for (var i = 0; i < 5; i++) r.H.Prev();
        r.H.Brake();
        r.Settle();
        r.ExpectReturnView("back");
    }

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void Delete_WhileZoomed(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 10, openAt: 3);
        r.H.Controller.DeleteFile = _ => Task.FromResult(new DeleteResult(true, false));
        r.Open();
        r.ZoomAndPan();

        DeleteResult? result = null;
        r.H.Run(async () => result = await r.H.Controller.DeleteCurrentPhoto());
        Assert.IsTrue(result!.DeleteSuccess);
        r.Settle();
        Assert.AreEqual("img_00004.jpg", r.H.CurrentName);
        r.ExpectNewPhotoView("the next one");
    }

    /// <summary>A renamed photo keeps its view, and Remember finds it under the new name.</summary>
    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void Rename_WhileZoomed_ThenAwayAndBack(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 10, openAt: 3).Open();
        r.ZoomAndPan();

        RenameResult? result = null;
        r.H.Run(async () => result = await r.H.Controller.RenameCurrentPhoto("zzz.jpg"));
        Assert.IsTrue(result!.Success, result.FailMessage);
        r.Settle();
        Assert.AreEqual("zzz.jpg", r.H.CurrentName);
        Near(r.ZoomedScale, r.V.View.Scale, r.ZoomedScale * 1e-6, "the view stays through the rename");

        r.H.Run(() => r.H.Controller.FlyBy(-1));
        r.Settle();
        r.ExpectNewPhotoView("the one before");
        r.H.Run(() => r.H.Controller.FlyBy(1));
        r.Settle();
        Assert.AreEqual("zzz.jpg", r.H.CurrentName);
        r.ExpectReturnView("back on the renamed photo");
    }

    /// <summary>A file that fails to decode, passed through: its error screen doesn't disturb the photos either side.</summary>
    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void BrokenFile_PassedThrough(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 10, openAt: 3);
        var broken = r.H.PathOf(4);
        r.H.Decoder.Script = d => d.Path == broken && d.Kind != DecodeKind.DiskWrite ? Reply.Error : Reply.Item;
        r.Open();
        r.ZoomAndPan();

        r.H.Next();
        r.Settle();
        Assert.AreEqual("img_00004.jpg", r.H.CurrentName);
        r.H.Next();
        r.Settle();
        r.ExpectNewPhotoView("past the broken file");

        r.H.Prev();
        r.H.Prev();
        r.H.Brake();
        r.Settle();
        r.ExpectReturnView("back across it");
    }

    /// <summary>One photo: navigation does nothing, so the view stays as the user left it.</summary>
    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void OnePhoto_NavigationLeavesTheView(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        using var r = new Rig(mode, hqHalf, source, halfHq, photos: 1, openAt: 0).Open();
        r.ZoomAndPan();
        r.H.Next();
        r.H.Prev();
        r.H.Run(r.H.Controller.FlyToLast);
        r.H.Run(r.H.Controller.FlyToFirst);
        r.H.Brake();
        r.Settle();
        Near(r.ZoomedScale, r.V.View.Scale, 1e-6, "scale");
        Near(r.ZoomedPos.X, r.V.View.Pos.X, 1e-6, "pan X");
    }

    /// <summary>A secondary instance lists only its photo: navigation does nothing and nothing else loads.</summary>
    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void SecondaryInstance_NavigationLeavesTheView(PanZoomBehaviourOnNavigation mode, int hqHalf, ListSource source, bool halfHq)
    {
        AppConfig.Volatile.IsSecondaryInstance = true;
        try
        {
            using var r = new Rig(mode, hqHalf, source, halfHq, photos: 5, openAt: 2).Open();
            r.ZoomAndPan();
            r.H.Next();
            r.H.Prev();
            r.H.Brake();
            r.Settle();
            Assert.AreEqual(1, r.H.Nav.Keys.Count, "only the opened photo");
            Near(r.ZoomedScale, r.V.View.Scale, 1e-6, "scale");
            Assert.AreEqual(1, r.H.Canvas.History.Select(i => i.Photo).Distinct().Count(), "nothing else shown");
        }
        finally
        {
            AppConfig.Volatile.IsSecondaryInstance = false;
        }
    }
}
