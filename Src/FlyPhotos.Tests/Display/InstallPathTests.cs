using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The install path end to end, headless: real photos whose preview and HQ arrive in the orders the app meets, driven
/// through <see cref="ViewController.Install"/> as the canvas engine calls it, in all three navigation modes.
/// <list type="bullet">
///   <item>The first photo, or a far jump (End in a large list), shown HQ first: no preview, so its size by metadata
///     is unknown until the preview loads afterwards (and is never shown).</item>
///   <item>A far jump shows the loading placeholder before either image.</item>
///   <item>SVG and PDF, whose "size" is a render size: the preview's metadata gives it, the HQ item reports it.</item>
/// </list>
/// The canvas is 1000×800. Every photo is opened at 100% on its HQ before moving on, so Retain must keep true 100% on
/// every HQ after it; Reset fits every new photo; Remember gives each photo its own view back.
/// </summary>
[TestClass]
public sealed class InstallPathTests
{
    /// <summary>
    /// A kind of photo: its size by metadata, its preview bitmap, and its HQ (size and EXIF rotation). The HQ of an
    /// "ARW embedded" is a small JPEG, a quarter of the metadata size, so a mix-up of the two scales shows as 370%.
    /// </summary>
    public sealed record Kind(string Name, string Ext, double MetaW, double MetaH, int PrevW, int PrevH,
        double HqW, double HqH, int HqRotation = 0);

    private static readonly Kind[] Kinds =
    [
        new("jpeg", ".jpg", 6000, 4000, 300, 200, 6000, 4000),
        new("arw embedded", ".arw", 6024, 4024, 1024, 684, 1616, 1080),
        new("arw rawler", ".arw", 6024, 4024, 1024, 684, 6000, 4000),
        new("nef portrait", ".nef", 4040, 6064, 682, 1024, 6048, 4032, 270),
        new("svg", ".svg", 2000, 1333, 800, 533, 2000, 1334), // the HQ's fit rounds differently from the preview's metadata
        new("pdf", ".pdf", 794, 1123, 566, 800, 794, 1123),
    ];

    private static readonly PanZoomBehaviourOnNavigation[] Modes =
        [PanZoomBehaviourOnNavigation.RetainFromLastPhoto, PanZoomBehaviourOnNavigation.Reset, PanZoomBehaviourOnNavigation.RememberPerPhoto];

    public static IEnumerable<object[]> KindsAndModes =>
        from k in Kinds from m in Modes select new object[] { k.Name, m };

    [TestInitialize]
    public void Setup() => AppConfig.Settings = new AppSettings { OpenExitZoom = false, StickyZoomLevels = false };

    /// <summary>What the canvas engine does for each install, with a hand-stepped clock.</summary>
    private sealed class App
    {
        public readonly ViewController V;
        private double _now;
        private int _count;
        private readonly DisplayItem _loading = new PreviewDisplayItem(TestGpu.Bitmap(64, 64), Origin.ErrorScreen);

        public App()
        {
            V = new ViewController(() => _now) { CanvasSize = new Size(1000, 800) };
        }

        public Photo Photo(Kind k) => new($@"c:\fly\{k.Name.Replace(' ', '_')}_{++_count}{k.Ext}");

        /// <summary>The loading placeholder, as on a far jump before anything has loaded.</summary>
        public void Placeholder(Photo p) =>
            V.Install(p, p.FilePath, ViewRules.LayoutSize(p, _loading), 0, DisplayLevel.PlaceHolder);

        /// <summary>The preview loads into the photo's slot; nothing is installed (an HQ may be on screen already).</summary>
        public static void PreviewLoads(Photo p, Kind k) =>
            p.Slots.TryAttachPreview(new PreviewDisplayItem(TestGpu.Bitmap(k.PrevW, k.PrevH), Origin.Disk,
                new ImageMetadata(k.MetaW, k.MetaH)), null);

        public void ShowPreview(Photo p, Kind k)
        {
            PreviewLoads(p, k);
            V.Install(p, p.FilePath, ViewRules.LayoutSize(p, p.Slots.Preview!), 0, DisplayLevel.Preview);
        }

        /// <summary>The HQ, laid out at its own size, as <see cref="ViewRules.LayoutSize"/> gives once it is stored.</summary>
        public void ShowHq(Photo p, Kind k) =>
            V.Install(p, p.FilePath, new Size(k.HqW, k.HqH), k.HqRotation, DisplayLevel.Hq);

        public void Settle()
        {
            for (var i = 0; i < 2000 && V.IsAnimating; i++)
            {
                _now += 16;
                V.Tick();
            }
            Assert.IsFalse(V.IsAnimating, "animation never settled");
        }

        /// <summary>The user zooms the HQ on screen to 100%.</summary>
        public void ZoomTo100()
        {
            V.ZoomToHundred();
            Settle();
        }

        /// <summary>The larger side of the image on screen, in screen pixels.</summary>
        public double OnScreen
        {
            get
            {
                var (w, h) = ViewRules.OnScreenSize(V.View.ImageSize, V.View.Rotation);
                return Math.Max(w, h) * V.View.Scale;
            }
        }

        public bool Fitted { get; private set; }
        public bool OneToOne { get; private set; }

        public App Watch()
        {
            V.FitToScreenStateChanged += f => Fitted = f;
            V.OneToOneStateChanged += o => OneToOne = o;
            return this;
        }
    }

    private static Kind KindNamed(string name) => Kinds.Single(k => k.Name == name);

    private static void Near(double expected, double actual, double tolerance, string what) =>
        Assert.IsTrue(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    /// <summary>What the mode expects of an HQ arriving after the previous photo was left at true 100%.</summary>
    private static void ExpectAfterLeaving100(App app, PanZoomBehaviourOnNavigation mode, Kind k, string what)
    {
        if (mode == PanZoomBehaviourOnNavigation.RetainFromLastPhoto)
        {
            Near(1, app.V.View.Scale, 1e-3, $"{what}: true 100% retained");
            Assert.IsTrue(app.OneToOne, $"{what}: 1:1");
        }
        else
        {
            var fit = ViewRules.FitScale(new Size(1000, 800), new Size(k.HqW, k.HqH), k.HqRotation);
            Near(Math.Min(fit, 1f), app.V.View.Scale, 1e-4, $"{what}: the default view");
            Assert.IsTrue(app.Fitted, $"{what}: fitted");
        }
    }

    /// <summary>The preview → HQ swap left the photo at its size on screen (no jump).</summary>
    private static void NoJump(double before, App app, string what) =>
        Near(1, app.OnScreen / before, 2e-3, $"{what}: no jump on screen");

    // ---------------------------------------------------------------- the first photo, HQ first

    [TestMethod]
    [DynamicData(nameof(KindsAndModes))]
    public void FirstPhotoHqFirst_PreviewLoadsLater_ThenNavigation(string kind, PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowHq(a, k);              // the first photo: its HQ straight away, no preview
        app.ZoomTo100();
        App.PreviewLoads(a, k);        // its preview loads afterwards, and is never shown

        var b = app.Photo(k);          // the next photo: preview, then HQ
        app.ShowPreview(b, k);
        var onPreview = app.OnScreen;
        app.ShowHq(b, k);
        NoJump(onPreview, app, "B");
        ExpectAfterLeaving100(app, mode, k, "B");

        var c = app.Photo(k);          // and one whose HQ is already cached
        app.ShowHq(c, k);
        ExpectAfterLeaving100(app, mode, k, "C");

        if (mode != PanZoomBehaviourOnNavigation.RememberPerPhoto) return;
        app.ShowHq(a, k);              // back to the first photo: its own 100%
        Near(1, app.V.View.Scale, 1e-3, "A remembered at 100%");
        Assert.IsTrue(app.OneToOne, "A: 1:1");
    }

    [TestMethod]
    [DynamicData(nameof(KindsAndModes))]
    public void FirstPhotoHqFirst_ZoomedAndPanned_RetainedOrRememberedThroughTheLatePreview(string kind,
        PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowHq(a, k);
        app.V.ZoomBy(3f, new Windows.Foundation.Point(300, 300));
        app.V.Pan(40, -25);
        var (zoomed, onScreen) = (app.V.View.Scale, app.OnScreen);
        App.PreviewLoads(a, k);

        var b = app.Photo(k);
        app.ShowPreview(b, k);
        var onPreview = app.OnScreen;
        app.ShowHq(b, k);
        NoJump(onPreview, app, "B");
        if (mode == PanZoomBehaviourOnNavigation.RetainFromLastPhoto)
            Near(onScreen, app.OnScreen, onScreen * 2e-3, "B: the same size on screen as A");

        app.ShowHq(a, k);
        if (mode == PanZoomBehaviourOnNavigation.RememberPerPhoto)
            Near(zoomed, app.V.View.Scale, zoomed * 1e-4, "A: its own zoom back");
    }

    // ---------------------------------------------------------------- a far jump: the placeholder first

    [TestMethod]
    [DynamicData(nameof(KindsAndModes))]
    public void FarJump_PlaceholderThenHq_PreviewLoadsLater_ThenNavigation(string kind, PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowPreview(a, k);
        app.ShowHq(a, k);
        app.ZoomTo100();

        var z = app.Photo(k);          // End: nothing loaded there yet
        app.Placeholder(z);
        app.ShowHq(z, k);              // its HQ before its preview
        ExpectAfterLeaving100(app, mode, k, "Z");
        App.PreviewLoads(z, k);

        var y = app.Photo(k);
        app.ShowPreview(y, k);
        var onPreview = app.OnScreen;
        app.ShowHq(y, k);
        NoJump(onPreview, app, "Y");
        ExpectAfterLeaving100(app, mode, k, "Y");
    }

    [TestMethod]
    [DynamicData(nameof(KindsAndModes))]
    public void FarJump_PlaceholderThenPreviewThenHq(string kind, PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowPreview(a, k);
        app.ShowHq(a, k);
        app.ZoomTo100();

        var z = app.Photo(k);
        app.Placeholder(z);
        app.ShowPreview(z, k);
        var onPreview = app.OnScreen;
        app.ShowHq(z, k);
        NoJump(onPreview, app, "Z");
        ExpectAfterLeaving100(app, mode, k, "Z");

        if (mode != PanZoomBehaviourOnNavigation.RememberPerPhoto) return;
        app.Placeholder(a);
        app.ShowHq(a, k);
        Near(1, app.V.View.Scale, 1e-3, "A remembered at 100% through the placeholder");
    }

    // ---------------------------------------------------------------- Remember on a far revisit

    public static IEnumerable<object[]> KindNames => Kinds.Select(k => new object[] { k.Name });

    /// <summary>
    /// Back to a photo whose preview was unloaded meanwhile (a far jump and back): its HQ goes up before the preview
    /// that tells its size, so the remembered zoom on the photo must come back through the image it was saved on
    /// (it landed at 26.8% for an embedded-JPEG ARW).
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(KindNames))]
    public void Remember_FarRevisit_HqBeforeItsPreview_RestoresTheView(string kind)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RememberPerPhoto;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowPreview(a, k);
        app.ShowHq(a, k);
        app.V.ZoomBy(2.5f, new Windows.Foundation.Point(400, 300));
        var zoomed = app.V.View.Scale;

        var z = app.Photo(k);
        app.Placeholder(z);
        app.ShowHq(z, k);

        a.Slots.DetachPreview()?.Release(); // the preview left the cache window
        app.Placeholder(a);
        app.ShowHq(a, k);
        Near(zoomed, app.V.View.Scale, zoomed * 1e-4, "A's own zoom back");
        App.PreviewLoads(a, k);

        app.ShowHq(z, k);                    // and once more, now that the preview is back
        app.ShowHq(a, k);
        Near(zoomed, app.V.View.Scale, zoomed * 1e-4, "A's zoom again");
    }

    /// <summary>
    /// A revisit through a disk-cached preview, which reports the HQ's size rather than the file's own (sensor) size
    /// the first visit saw: the photo's size by metadata changed, so only the scale on the HQ itself is exact.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(KindNames))]
    public void Remember_RevisitThroughACachedPreview_ComesBackOnTheSameHqScale(string kind)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RememberPerPhoto;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowHq(a, k);              // HQ first, the file's own preview later
        app.ZoomTo100();
        App.PreviewLoads(a, k);

        var z = app.Photo(k);
        app.Placeholder(z);
        app.ShowHq(z, k);

        // Back: the disk cache's preview, sized as the HQ (turned to the preview's axes).
        a.Slots.DetachPreview()?.Release();
        var cached = k with { MetaW = k.HqRotation % 180 != 0 ? k.HqH : k.HqW, MetaH = k.HqRotation % 180 != 0 ? k.HqW : k.HqH };
        app.Placeholder(a);
        app.ShowPreview(a, cached);
        app.ShowHq(a, k);
        Near(1, app.V.View.Scale, 1e-4, "A back at true 100%");
        Assert.IsTrue(app.OneToOne, "A: 1:1");
    }

    /// <summary>Leaving a photo while only its placeholder is up keeps the view remembered for it.</summary>
    [TestMethod]
    [DynamicData(nameof(KindNames))]
    public void Remember_LeavingWhileOnlyThePlaceholderIsUp_KeepsTheView(string kind)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RememberPerPhoto;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowPreview(a, k);
        app.ShowHq(a, k);
        app.V.ZoomBy(2.5f, new Windows.Foundation.Point(400, 300));
        var zoomed = app.V.View.Scale;

        var b = app.Photo(k);
        app.ShowPreview(b, k);
        app.ShowHq(b, k);
        app.Placeholder(a);                  // back to A, and away again before anything of it loads
        app.Placeholder(b);
        app.ShowHq(b, k);
        app.ShowHq(a, k);
        Near(zoomed, app.V.View.Scale, zoomed * 1e-4, "A's view survived the placeholder");
    }

    // ---------------------------------------------------------------- formats mixed

    public static IEnumerable<object[]> AllModes => Modes.Select(m => new object[] { m });

    /// <summary>
    /// One photo of each kind in turn, each through the placeholder, the first shown HQ first with its preview late.
    /// Retain across kinds keeps the zoom on the photo itself (#195): after a JPEG at 100%, an ARW whose HQ is its
    /// small embedded JPEG shows at 100% of the photo's size by metadata, which is 373% of that HQ.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AllModes))]
    public void MixedFormats_ThroughThePlaceholder_KeepTheZoomOnThePhoto(PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        var app = new App().Watch();
        var first = Kinds[0];
        var a = app.Photo(first);
        app.ShowHq(a, first);
        app.ZoomTo100();
        App.PreviewLoads(a, first);

        foreach (var k in Kinds.Skip(1).Concat(Kinds.Take(1)))
        {
            var p = app.Photo(k);
            app.Placeholder(p);
            app.ShowPreview(p, k);
            var onPreview = app.OnScreen;
            app.ShowHq(p, k);
            NoJump(onPreview, app, k.Name);
            if (mode == PanZoomBehaviourOnNavigation.RetainFromLastPhoto)
            {
                var hqPerPhoto = ViewRules.ImagePixelsPerPhotoPixel(new Size(k.HqW, k.HqH), k.HqRotation,
                    ViewRules.PhotoSizeOnScreen(p))!.Value;
                Near(1 / hqPerPhoto, app.V.View.Scale, 1e-3, $"{k.Name}: the photo at 100% of its size");
            }
            else
            {
                ExpectAfterLeaving100(app, mode, k, k.Name);
                app.ZoomTo100(); // each left at 100%
            }
        }
    }
}
