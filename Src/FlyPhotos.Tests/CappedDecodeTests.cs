using System.Runtime.InteropServices.WindowsRuntime;
using FlyPhotos.Display.ImageReading;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class CappedDecodeTests
{
    [TestMethod]
    [DataRow(8256u, 5504u, 800u, 533u)]
    [DataRow(5504u, 8256u, 533u, 800u)]
    [DataRow(1024u, 1024u, 800u, 800u)]
    [DataRow(800u, 600u, 800u, 600u)]
    [DataRow(160u, 120u, 160u, 120u)]
    [DataRow(20000u, 10u, 800u, 1u)]
    public void Fit_caps_the_longest_side_and_keeps_the_aspect(uint w, uint h, uint ew, uint eh) =>
        Assert.AreEqual((ew, eh), CappedDecode.Fit(w, h, 800));

    [TestMethod]
    [DataRow(160u, 120u, false)]   // EXIF thumbnail
    [DataRow(1024u, 684u, false)]  // Microsoft RAW codec thumbnail
    [DataRow(1600u, 1067u, false)]
    [DataRow(1601u, 1067u, true)]
    [DataRow(6048u, 8064u, true)]  // DNG codec: the whole 48 MP ProRAW image
    public void Only_a_thumbnail_over_twice_the_preview_size_is_scaled(uint w, uint h, bool expected) =>
        Assert.AreEqual(expected, CappedDecode.IsTooLargeForThumbnail(w, h));

    [TestMethod]
    public async Task A_large_jpeg_is_decoded_at_preview_size()
    {
        var (bitmap, fw, fh) = await CappedDecode.DecodeAsync(await Jpeg(2400, 1200));
        using (bitmap) Assert.AreEqual((800, 400, 2400u, 1200u), (bitmap.PixelWidth, bitmap.PixelHeight, fw, fh));
    }

    [TestMethod]
    public async Task A_small_jpeg_is_decoded_as_is()
    {
        var (bitmap, fw, fh) = await CappedDecode.DecodeAsync(await Jpeg(160, 120));
        using (bitmap) Assert.AreEqual((160, 120, 160u, 120u), (bitmap.PixelWidth, bitmap.PixelHeight, fw, fh));
    }

    /// <summary>A portrait camera JPEG stored landscape with EXIF orientation 6 (rotate 90°) comes out upright.</summary>
    [TestMethod]
    public async Task An_exif_rotated_jpeg_is_decoded_upright()
    {
        var (bitmap, fw, fh) = await CappedDecode.DecodeAsync(await Jpeg(2400, 1200, orientation: 6));
        using (bitmap) Assert.AreEqual((400, 800, 1200u, 2400u), (bitmap.PixelWidth, bitmap.PixelHeight, fw, fh));
    }

    /// <summary>The stored image's top-left 300 px square lands top-right, still square: rotated, not squashed.</summary>
    [TestMethod]
    public async Task An_exif_rotated_jpeg_is_not_squashed()
    {
        var (bitmap, _, _) = await CappedDecode.DecodeAsync(await Jpeg(2400, 1200, orientation: 6, marker: true));
        using (bitmap)
        {
            var px = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyToBuffer(px.AsBuffer());
            int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
            for (int y = 0; y < bitmap.PixelHeight; y++)
                for (int x = 0; x < bitmap.PixelWidth; x++)
                    if (px[(y * bitmap.PixelWidth + x) * 4 + 2] > 128)
                        (x0, x1, y0, y1) = (Math.Min(x0, x), Math.Max(x1, x), Math.Min(y0, y), Math.Max(y1, y));
            // ±3 px for JPEG bleed; squashed, the square was 200 px wide.
            Assert.AreEqual((399, 0), (x1, y0));
            Assert.AreEqual(100, x1 - x0 + 1, 3);
            Assert.AreEqual(100, y1 - y0 + 1, 3);
        }
    }

    /// <summary>A grey JPEG of the given size in memory, optionally with an EXIF orientation tag and a red
    /// 300 px square in its stored top-left corner.</summary>
    private static async Task<IRandomAccessStream> Jpeg(uint w, uint h, ushort orientation = 0, bool marker = false)
    {
        var pixels = Enumerable.Repeat((byte)128, (int)(w * h * 4)).ToArray();
        if (marker)
            for (int y = 0; y < 300; y++)
                for (int x = 0; x < 300; x++)
                    (pixels[(y * w + x) * 4], pixels[(y * w + x) * 4 + 1], pixels[(y * w + x) * 4 + 2]) = ((byte)0, (byte)0, (byte)255);
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, pixels);
        if (orientation != 0)
        {
            var props = new Dictionary<string, BitmapTypedValue>
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue(orientation, PropertyType.UInt16)
            };
            await encoder.BitmapProperties.SetPropertiesAsync(props);
        }
        await encoder.FlushAsync();
        stream.Seek(0);
        return stream;
    }
}
