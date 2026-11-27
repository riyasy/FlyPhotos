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

    // ---------------------------------------------------------------- Reset on navigation (a slideshow)

    /// <summary>
    /// A slideshow opens each photo at its default view, fitted edge to edge (as if "Image to screen fit" were 100%),
    /// and leaves the views the user remembered alone.
    /// </summary>
    [TestMethod]
    [DataRow(PanZoomBehaviourOnNavigation.RetainFromLastPhoto)]
    [DataRow(PanZoomBehaviourOnNavigation.RememberPerPhoto)]
    public void SlideshowFit_OpensPhotosAtTheirDefault_AndKeepsRememberedViews(PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        AppConfig.Settings.ImageFitPercentage = 70;
        var k = Kinds[0]; // larger than the canvas: its default is the fit
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowHq(a, k);
        var defaultScale = app.V.View.Scale / 0.7f; // edge to edge
        app.V.ZoomBy(2.5f, new Windows.Foundation.Point(400, 300));
        var zoomed = app.V.View.Scale;

        app.V.SlideshowFit = true;
        var z = app.Photo(k);
        app.ShowHq(z, k);
        Near(defaultScale, app.V.View.Scale, defaultScale * 1e-4, "Z at its default");
        app.ShowHq(a, k);
        Near(defaultScale, app.V.View.Scale, defaultScale * 1e-4, "A at its default too");

        app.V.SlideshowFit = false;
        app.ShowHq(z, k);
        app.ShowHq(a, k);
        if (mode == PanZoomBehaviourOnNavigation.RememberPerPhoto)
            Near(zoomed, app.V.View.Scale, zoomed * 1e-4, "A's remembered zoom kept");
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

    /// <summary>
    /// Left on its HQ before its preview loaded, so its photo size was never learnt; back through the preview, then
    /// the HQ, it must keep its size on screen.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(KindNames))]
    [Ignore("Known bug: the RAW kinds jump (embedded-JPEG ARW to 0.27x) until it is fixed")]
    public void Remember_LeftBeforeItsPreviewLoaded_ComesBackWithoutAJump(string kind)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RememberPerPhoto;
        var k = KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowHq(a, k);
        app.V.ZoomBy(2.5f, new Windows.Foundation.Point(400, 300));
        var left = app.OnScreen;

        var b = app.Photo(k);
        app.ShowPreview(b, k);
        app.ShowHq(b, k);

        app.ShowPreview(a, k);
        var onPreview = app.OnScreen;
        app.ShowHq(a, k);
        NoJump(onPreview, app, "A");
        Near(left, app.OnScreen, left * 2e-3, "A at the size it was left at");
    }

    /// <summary>
    /// A photo's default view is the same whichever of its images arrives first. Its HQ here is an embedded JPEG
    /// smaller than the canvas: through the preview it is fitted, HQ first it would show at the HQ's 100%.
    /// </summary>
    [TestMethod]
    [DataRow(PanZoomBehaviourOnNavigation.Reset)]
    [DataRow(PanZoomBehaviourOnNavigation.RememberPerPhoto)]
    [Ignore("Known bug: HQ first shows 600 px, through the preview 1000 px, until it is fixed")]
    public void DefaultView_SameWhetherThePreviewOrTheHqComesFirst(PanZoomBehaviourOnNavigation mode)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = mode;
        var k = new Kind("arw small embedded", ".arw", 6000, 4000, 300, 200, 600, 400);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowPreview(a, k);
        app.ShowHq(a, k);
        var throughThePreview = app.OnScreen;

        var b = app.Photo(k);
        app.Placeholder(b);
        app.ShowHq(b, k);
        Near(throughThePreview, app.OnScreen, throughThePreview * 2e-3, "B, HQ first, at A's size on screen");
    }

    /// <summary>
    /// Retain at true 100%, a far jump to a photo whose HQ goes up before its preview, and back before that preview
    /// loads: the photo returned to comes back through its preview, and its HQ must land at true 100%. Sizes from the
    /// CR3 in nav-sizes.ps1: metadata 5496×3670, HQ 5472×3648.
    /// </summary>
    [TestMethod]
    [DataRow("cr3")]
    [DataRow("arw embedded")]
    [DataRow("arw rawler")]
    [DataRow("jpeg")]
    public void Retain_FarJumpAndBack_LeavingBeforeThePreviewLoads_KeepsTrue100(string kind)
    {
        AppConfig.Settings.PanZoomBehaviourOnNavigation = PanZoomBehaviourOnNavigation.RetainFromLastPhoto;
        var k = kind == "cr3" ? new Kind("cr3", ".cr3", 5496, 3670, 1498, 1000, 5472, 3648) : KindNamed(kind);
        var app = new App().Watch();
        var a = app.Photo(k);
        app.ShowHq(a, k);
        app.ZoomTo100();
        App.PreviewLoads(a, k);

        var z = app.Photo(k);           // End: its HQ before its preview
        app.Placeholder(z);
        app.ShowHq(z, k);
        Near(1, app.V.View.Scale, 1e-4, "Z at true 100%");

        a.Slots.DetachPreview()?.Release();
        app.Placeholder(a);             // Home, before Z's preview loaded
        app.ShowPreview(a, k);
        app.ShowHq(a, k);
        Near(1, app.V.View.Scale, 1e-4, "A back at true 100%");
        Assert.IsTrue(app.OneToOne, "A: 1:1");
    }

    // ---------------------------------------------------------------- settings, pairwise

    private static readonly Size Canvas = new(1000, 800);

    private static readonly Kind SmallJpeg = new("small jpeg", ".jpg", 300, 200, 150, 100, 300, 200);
    private static readonly Kind SmallArw = new("small arw embedded", ".arw", 600, 400, 300, 200, 150, 100);

    private const string PreviewFirst = "preview>HQ", HqFirst = "HQ>preview", PlaceholderFirst = "placeholder>HQ>preview";

    /// <summary>
    /// Every pair of these values in nine rows (docs/work/20261123-canvas-readability/test-plan.md §5): mode, install
    /// order, photo smaller or larger than the canvas, HQ the preview's size or a quarter of it, stretch, fit %,
    /// sticky zoom, open zoom.
    /// </summary>
    public static IEnumerable<object[]> PairwiseRows =>
    [
        ["P1", PanZoomBehaviourOnNavigation.RetainFromLastPhoto, PreviewFirst, true, true, true, 90, false, true],
        ["P2", PanZoomBehaviourOnNavigation.RememberPerPhoto, PreviewFirst, false, false, false, 100, true, false],
        ["P3", PanZoomBehaviourOnNavigation.Reset, PlaceholderFirst, true, true, false, 100, true, true],
        ["P4", PanZoomBehaviourOnNavigation.Reset, HqFirst, false, false, true, 100, false, false],
        ["P5", PanZoomBehaviourOnNavigation.RetainFromLastPhoto, HqFirst, true, false, false, 90, true, false],
        ["P6", PanZoomBehaviourOnNavigation.RememberPerPhoto, PlaceholderFirst, false, true, true, 90, false, false],
        ["P7", PanZoomBehaviourOnNavigation.RetainFromLastPhoto, PlaceholderFirst, false, false, true, 100, true, true],
        ["P8", PanZoomBehaviourOnNavigation.RememberPerPhoto, HqFirst, true, true, false, 100, false, true],
        ["P9", PanZoomBehaviourOnNavigation.Reset, PreviewFirst, false, true, true, 90, true, false],
    ];

    /// <summary>
    /// Open A, wheel-zoom two notches, pan, go to B (the other size class), come back to A. Each image arrives in the
    /// row's order; every preview → HQ keeps the size on screen, and the mode decides each photo's view.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(PairwiseRows))]
    public void Pairwise_SettingsAndInstallOrders(string row, PanZoomBehaviourOnNavigation mode, string order,
        bool small, bool rawHq, bool stretch, int fitPercent, bool sticky, bool openZoom)
    {
        AppConfig.Settings = new AppSettings
        {
            PanZoomBehaviourOnNavigation = mode, StretchSmallImages = stretch, ImageFitPercentage = fitPercent,
            StickyZoomLevels = sticky, OpenExitZoom = openZoom,
        };
        var kindA = small ? (rawHq ? SmallArw : SmallJpeg) : (rawHq ? KindNamed("arw embedded") : KindNamed("jpeg"));
        var kindB = small ? (rawHq ? KindNamed("arw embedded") : KindNamed("jpeg")) : (rawHq ? SmallArw : SmallJpeg);
        var app = new App().Watch();

        // A, the first photo: no placeholder, and the open zoom if it is on.
        var a = app.Photo(kindA);
        Arrive(app, a, kindA, order == PlaceholderFirst ? HqFirst : order, () => AtDefault(app, $"{row} A"));

        var from = app.V.View.Scale;
        app.V.WheelZoom(ZoomDirection.In, new Windows.Foundation.Point(600, 350));
        app.V.WheelZoom(ZoomDirection.In, new Windows.Foundation.Point(600, 350));
        app.Settle();
        var oneNotch = ViewRules.WheelStepTarget(from, ZoomDirection.In, app.V.View.ImageSize)!.Value;
        var twoNotches = ViewRules.WheelStepTarget(oneNotch, ZoomDirection.In, app.V.View.ImageSize)!.Value;
        Near(twoNotches, app.V.View.Scale, twoNotches * 1e-4, $"{row} A: two wheel notches");
        app.V.Pan(40, -30);
        Assert.IsFalse(app.Fitted, $"{row} A: zoomed is not fitted");

        var (aScale, aPos, aCentre) = (app.V.View.Scale, app.V.View.Pos, CentreOnImage(app));
        var aPhotoZoom = aScale * PerPhoto(app, a)!.Value;

        var b = app.Photo(kindB);
        Arrive(app, b, kindB, order, () =>
        {
            if (mode != PanZoomBehaviourOnNavigation.RetainFromLastPhoto)
            {
                AtDefault(app, $"{row} B");
                return;
            }
            // The photo zoom carries over where B's size is known, else the scale itself.
            var expected = PerPhoto(app, b) is { } perB ? aPhotoZoom / perB : aScale;
            Near(expected, app.V.View.Scale, expected * 1e-3, $"{row} B: A's zoom retained");
            Near(aCentre.X, CentreOnImage(app).X, 1e-3, $"{row} B: the same part at the centre, X");
            Near(aCentre.Y, CentreOnImage(app).Y, 1e-3, $"{row} B: the same part at the centre, Y");
            Assert.IsFalse(app.Fitted, $"{row} B: not fitted");
        });

        // Back to A, its preview gone from the cache, in the row's order again.
        a.Slots.DetachPreview()?.Release();
        Arrive(app, a, kindA, order, () =>
        {
            if (mode == PanZoomBehaviourOnNavigation.Reset) AtDefault(app, $"{row} A again");
        });
        if (mode != PanZoomBehaviourOnNavigation.RememberPerPhoto) return;
        Near(aScale, app.V.View.Scale, aScale * 1e-4, $"{row} A: its own zoom back");
        Near(aPos.X, app.V.View.Pos.X, 0.5, $"{row} A: its own pan back, X");
        Near(aPos.Y, app.V.View.Pos.Y, 0.5, $"{row} A: its own pan back, Y");
        Assert.IsFalse(app.Fitted, $"{row} A: not fitted");
    }

    /// <summary>
    /// One photo arriving in <paramref name="order"/>. <paramref name="atFirstImage"/> checks the view on its first
    /// real image; a preview → HQ after it must not jump.
    /// </summary>
    private static void Arrive(App app, Photo p, Kind k, string order, Action atFirstImage)
    {
        if (order == PlaceholderFirst) app.Placeholder(p);
        if (order == PreviewFirst)
        {
            app.ShowPreview(p, k);
            app.Settle();
            atFirstImage();
            var onPreview = app.OnScreen;
            app.ShowHq(p, k);
            NoJump(onPreview, app, k.Name);
        }
        else
        {
            app.ShowHq(p, k);
            app.Settle();
            atFirstImage();
            App.PreviewLoads(p, k);
        }
    }

    /// <summary>The default view: the default scale, centred, and fitted only if that is the fit.</summary>
    private static void AtDefault(App app, string what)
    {
        var view = app.V.View;
        var scale = ViewRules.DefaultScale(Canvas, view.ImageSize, view.Rotation);
        Near(scale, view.Scale, scale * 1e-4, $"{what}: the default scale");
        Assert.IsTrue(ViewRules.IsCentred(view.Pos, Canvas), $"{what}: centred");
        Assert.AreEqual(ViewRules.SameScale(scale, ViewRules.FitScale(Canvas, view.ImageSize, view.Rotation)),
            app.Fitted, $"{what}: fitted");
    }

    private static Windows.Foundation.Point CentreOnImage(App app) =>
        ViewRules.CentreOnImage(app.V.View.Pos, app.V.View.Scale, app.V.View.ImageSize, app.V.View.Rotation, Canvas);

    /// <summary>The image on screen's pixels per photo pixel, null while the photo's size is unknown.</summary>
    private static float? PerPhoto(App app, Photo p) =>
        ViewRules.ImagePixelsPerPhotoPixel(app.V.View.ImageSize, app.V.View.Rotation, ViewRules.PhotoSizeOnScreen(p));

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
