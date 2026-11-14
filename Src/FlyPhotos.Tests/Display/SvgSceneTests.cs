using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The SVG GPU scene and its display item, on the headless software device with real SVG files: the item rasterizes
/// at its reported size, and the scene's device resources live until its last owner (the item, or a renderer still
/// drawing it) lets go.
/// </summary>
[TestClass]
public sealed class SvgSceneTests
{
    // What the reader draws with: a render target is a resource creator with a DPI, like the app's canvas.
    private static readonly CanvasRenderTarget Ctrl = new(TestGpu.Device, 1, 1, 96);
    private string _folder = null!;

    [TestInitialize]
    public void Setup()
    {
        AppConfig.Settings = new AppSettings();
        _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FlySvg_" + Guid.NewGuid().ToString("N"))).FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A 200 x 100 SVG: red on the left half, blue on the right.
    private string RedBlueSvg()
    {
        var path = Path.Combine(_folder, "red_blue.svg");
        File.WriteAllText(path, """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100">
              <rect x="0" y="0" width="100" height="100" fill="#ff0000"/>
              <rect x="100" y="0" width="100" height="100" fill="#0000ff"/>
            </svg>
            """);
        return path;
    }

    private async Task<SvgHqDisplayItem> LoadHq()
    {
        var (ok, hq) = await SvgReader.GetHq(Ctrl, RedBlueSvg());
        Assert.IsTrue(ok);
        Assert.IsInstanceOfType<SvgHqDisplayItem>(hq, "drawn as a GPU scene, not resvg's bitmap");
        return (SvgHqDisplayItem)hq;
    }

    [TestMethod]
    public async Task An_svg_item_rasterizes_at_its_reported_size()
    {
        var item = await LoadHq();
        Assert.AreEqual((2000, 1000), (item.Width, item.Height), "fitted to 2000 px on the longer side");

        using (var bitmap = item.Rasterize())
        {
            Assert.AreEqual(2000u, bitmap.SizeInPixels.Width);
            Assert.AreEqual(1000u, bitmap.SizeInPixels.Height);
            var left = bitmap.GetPixelColors(500, 500, 1, 1)[0];
            var right = bitmap.GetPixelColors(1500, 500, 1, 1)[0];
            Assert.IsTrue(left.R > 200 && left.B < 50, $"left is red: {left}");
            Assert.IsTrue(right.B > 200 && right.R < 50, $"right is blue: {right}");
        }

        item.Release();
        Assert.IsTrue(item.IsFreed);
        Assert.IsNull(item.Scene.TryAcquire(), "the scene's resources went with the item");
        Assert.IsNull(item.Rasterize(), "nothing left to draw");
    }

    [TestMethod]
    public async Task A_renderer_holding_the_scene_keeps_it_after_the_item_is_freed()
    {
        var item = await LoadHq();
        var renderer = item.Scene.TryAcquire()!; // as SvgVectorRenderer does while it draws

        item.Release();
        Assert.IsTrue(item.IsFreed);
        using (var bitmap = item.Rasterize())
            Assert.IsNotNull(bitmap, "the renderer's lease keeps the scene drawable");

        renderer.Dispose();
        Assert.IsNull(item.Scene.TryAcquire(), "the last owner released the scene");
    }

    [TestMethod]
    public async Task A_scene_lease_disposed_twice_drops_one_ownership()
    {
        var item = await LoadHq();
        var extra = item.Scene.TryAcquire()!;
        extra.Dispose();
        extra.Dispose();
        using (var bitmap = item.Rasterize())
            Assert.IsNotNull(bitmap, "the item's own ownership still holds the scene");

        item.Release();
        Assert.IsNull(item.Scene.TryAcquire());
    }

    [TestMethod]
    public async Task An_svg_preview_is_800_px_and_reports_the_hq_size()
    {
        var (ok, preview) = await SvgReader.GetResized(Ctrl, RedBlueSvg());
        Assert.IsTrue(ok);
        Assert.AreEqual(800u, preview.Bitmap.SizeInPixels.Width);
        Assert.AreEqual(400u, preview.Bitmap.SizeInPixels.Height);
        Assert.AreEqual(new ImageMetadata(2000, 1000), preview.Metadata);
        preview.Release();
        Assert.IsTrue(preview.IsFreed);
    }
}
