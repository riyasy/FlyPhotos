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
        var (pixels, w, h, fw, fh) = await CappedDecode.DecodeAsync(await Jpeg(2400, 1200));
        Assert.AreEqual((800u, 400u, 2400u, 1200u), (w, h, fw, fh));
        Assert.AreEqual(800 * 400 * 4, pixels.Length);
    }

    [TestMethod]
    public async Task A_small_jpeg_is_decoded_as_is()
    {
        var (pixels, w, h, fw, fh) = await CappedDecode.DecodeAsync(await Jpeg(160, 120));
        Assert.AreEqual((160u, 120u, 160u, 120u), (w, h, fw, fh));
        Assert.AreEqual(160 * 120 * 4, pixels.Length);
    }

    /// <summary>A portrait camera JPEG stored landscape with EXIF orientation 6 (rotate 90°) comes out upright.</summary>
    [TestMethod]
    public async Task An_exif_rotated_jpeg_is_decoded_upright()
    {
        var (pixels, w, h, fw, fh) = await CappedDecode.DecodeAsync(await Jpeg(2400, 1200, orientation: 6));
        Assert.AreEqual((400u, 800u, 1200u, 2400u), (w, h, fw, fh));
        Assert.AreEqual(400 * 800 * 4, pixels.Length);
    }

    /// <summary>A grey JPEG of the given size in memory, optionally with an EXIF orientation tag.</summary>
    private static async Task<IRandomAccessStream> Jpeg(uint w, uint h, ushort orientation = 0)
    {
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96,
            Enumerable.Repeat((byte)128, (int)(w * h * 4)).ToArray());
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
