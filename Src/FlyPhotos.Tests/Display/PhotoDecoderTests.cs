using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The real <see cref="PhotoDecoder"/> on the headless software device, with real PNG files and the SQLite disk cache
/// in a temp folder (<see cref="TestSetup.DiskCacheFolder"/>): the first photo's two ways in, the thumbnail crop, the
/// disk-cache round trip, and an image over the device's maximum bitmap size.
/// </summary>
[TestClass]
public sealed class PhotoDecoderTests
{
    // What the decoder draws with: a render target is a resource creator with a DPI, like the app's canvas.
    private static readonly CanvasRenderTarget Ctrl = new(TestGpu.Device, 1, 1, 96);
    private string _folder = null!;
    private PhotoDecoder _decoder = null!;
    private readonly List<Photo> _photos = [];

    [TestInitialize]
    public void Setup()
    {
        AppConfig.Settings = new AppSettings();
        _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FlyDecoder_" + Guid.NewGuid().ToString("N"))).FullName;
        _decoder = new PhotoDecoder(Ctrl);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var photo in _photos) photo.Slots.Retire();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Photo NewPhoto(string path)
    {
        var photo = new Photo(path);
        _photos.Add(photo);
        return photo;
    }

    // A PNG of width x height whose pixel colour is colorAt(x): red, green or blue.
    private async Task<string> Png(string name, int width, int height, Func<int, (byte R, byte G, byte B)>? colorAt = null)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = colorAt?.Invoke(x) ?? ((byte)0, (byte)128, (byte)0);
                int i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        var path = Path.Combine(_folder, name);
        using (var file = File.Create(path))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        return path;
    }

    [TestMethod]
    public async Task A_png_with_nothing_cached_opens_with_its_hq_straight_away()
    {
        var photo = NewPhoto(await Png("plain.png", 300, 100));
        Assert.IsFalse(await _decoder.LoadFirstPreview(photo), "no preview step: the HQ is all there is");
        Assert.IsNotNull(photo.Slots.Hq);
        Assert.IsNull(photo.Slots.Preview);
        Assert.AreEqual(300u, photo.Slots.Hq.Bitmap.SizeInPixels.Width);
    }

    [TestMethod]
    public async Task A_cached_preview_opens_first_with_its_thumbnail_and_the_real_size()
    {
        var path = await Png("cached.png", 300, 100);
        var loaded = await _decoder.LoadPreview(NewPhoto(path), Purpose.Preview);
        Assert.IsNotNull(loaded);
        var preview = loaded.Value.Preview;
        await _decoder.PutPreviewInDiskCache(path, preview, 300, 100);
        preview.Release();

        var photo = NewPhoto(path);
        Assert.IsTrue(await _decoder.LoadFirstPreview(photo), "a preview on screen, the HQ still to load");
        Assert.AreEqual(Origin.DiskCache, photo.Slots.Preview!.Origin);
        Assert.AreEqual(new ImageMetadata(300, 100), photo.Slots.Preview.Metadata);
        Assert.IsNotNull(photo.Slots.Thumbnail);
        Assert.IsNull(photo.Slots.Hq);
    }

    [TestMethod]
    public async Task A_preview_released_before_its_disk_cache_write_is_not_written()
    {
        var path = await Png("released.png", 300, 100);
        var preview = (await _decoder.LoadPreview(NewPhoto(path), Purpose.Preview))!.Value.Preview;
        preview.Release();
        Assert.IsTrue(preview.IsFreed);

        await _decoder.PutPreviewInDiskCache(path, preview, 300, 100); // must not touch the freed bitmap

        Assert.IsFalse(await _decoder.LoadFirstPreview(NewPhoto(path)), "nothing cached: the HQ straight away");
    }

    [TestMethod]
    public async Task The_thumbnail_is_the_centre_square()
    {
        // Thirds: red, green, blue. The centre square of a 300 x 100 image is the green third.
        var path = await Png("thirds.png", 300, 100, x => x < 100 ? ((byte)255, (byte)0, (byte)0)
            : x < 200 ? ((byte)0, (byte)255, (byte)0) : ((byte)0, (byte)0, (byte)255));
        var (preview, thumbnail) = (await _decoder.LoadPreview(NewPhoto(path), Purpose.Preview))!.Value;
        preview.Release();

        int size = Constants.ThumbnailPixelBufferSize;
        Assert.AreEqual(size * size * 4, thumbnail!.Pixels.Length);
        foreach (var (x, y) in new[] { (4, size / 2), (size / 2, size / 2), (size - 5, size / 2) })
        {
            int i = (y * size + x) * 4; // BGRA
            var (b, g, r) = (thumbnail.Pixels[i], thumbnail.Pixels[i + 1], thumbnail.Pixels[i + 2]);
            Assert.IsTrue(g > 200 && r < 60 && b < 60, $"({x},{y}) is green: r{r} g{g} b{b}");
        }
    }

    [TestMethod]
    public async Task An_oversized_image_is_marked_deferred_for_a_neighbour_and_heals_a_stale_cached_size()
    {
        // The software device takes bitmaps up to 8 million pixels a side; a GPU's limit (16384 typically) is what
        // makes an image oversized in the app, and an image just past it is cheap to make.
        using var gpu = new CanvasDevice();
        int max = gpu.MaximumBitmapSizeInPixels;
        if (max > 65536) Assert.Inconclusive($"This device takes bitmaps up to {max} px: too big to test cheaply");
        using var gpuCtrl = new CanvasRenderTarget(gpu, 1, 1, 96);
        _decoder = new PhotoDecoder(gpuCtrl);

        var path = await Png("wide.png", max + 16, 1);
        // An older build cached this photo's preview with a wrong size.
        using (var bitmap = CanvasBitmap.CreateFromColors(gpuCtrl, new Windows.UI.Color[16], 4, 4))
        {
            var stale = new PreviewDisplayItem(bitmap, Origin.Disk);
            await _decoder.PutPreviewInDiskCache(path, stale, 600, 600);
        }

        var photo = NewPhoto(path);
        Assert.IsTrue(await _decoder.LoadFirstPreview(photo), "the cached preview first");
        Assert.AreEqual(new ImageMetadata(600, 600), photo.Slots.Preview!.Metadata);

        Assert.IsNull(await _decoder.LoadHq(photo, allowLarge: false, CancellationToken.None), "deferred as a neighbour");
        Assert.IsTrue(photo.Slots.KnownOversized, "next time the doomed normal decode is skipped");

        var hq = await _decoder.LoadHq(photo, allowLarge: true, CancellationToken.None);
        Assert.IsInstanceOfType<TiledHqDisplayItem>(hq);
        Assert.AreEqual(max + 16, ((TiledHqDisplayItem)hq).Pyramid.FullWidth);
        hq.Release();

        var reopened = NewPhoto(path);
        Assert.IsTrue(await _decoder.LoadFirstPreview(reopened));
        Assert.AreEqual(new ImageMetadata(max + 16, 1), reopened.Slots.Preview!.Metadata, "the cached size healed");

        // Free the GPU bitmaps before the device goes.
        foreach (var p in _photos) p.Slots.Retire();
        _photos.Clear();
    }
}
