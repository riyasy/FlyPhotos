using FlyPhotos.Display.ImageReading;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace FlyPhotos.Tests.Display;

/// <summary>MagicScaler's resized preview: the pixels it pulls reach Win2D as premultiplied BGRA.</summary>
[TestClass]
public sealed class MagicScalerWrapTests
{
    private static readonly CanvasRenderTarget Ctrl = new(TestGpu.Device, 1, 1, 96);

    [TestMethod]
    public async Task ResizedJpegKeepsItsColourAndShape()
    {
        var path = Path.Combine(Path.GetTempPath(), "FlyMagicScaler_" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            using (var target = new CanvasRenderTarget(TestGpu.Device, 1600, 1000, 96))
            {
                using (var ds = target.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 200, 60, 30));
                await target.SaveAsync(path, CanvasBitmapFileFormat.Jpeg, 0.95f);
            }

            using var file = new ImageFile(path);
            var (ok, item) = await MagicScalerWrap.GetResized(Ctrl, file);

            Assert.IsTrue(ok);
            Assert.AreEqual(800u, item.Bitmap.SizeInPixels.Width);
            Assert.AreEqual(500u, item.Bitmap.SizeInPixels.Height);
            // A row-stride mix-up would smear channels across the image; check a far corner too.
            foreach (var c in new[] { item.Bitmap.GetPixelColors(0, 0, 1, 1)[0], item.Bitmap.GetPixelColors(799, 499, 1, 1)[0] })
            {
                Assert.AreEqual(200d, c.R, 6d);
                Assert.AreEqual(60d, c.G, 6d);
                Assert.AreEqual(30d, c.B, 6d);
                Assert.AreEqual((byte)255, c.A);
            }
            item.Release();
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ExpandsGreyAndBgrAndPremultipliesBgra()
    {
        CollectionAssert.AreEqual(new byte[] { 7, 7, 7, 255 }, MagicScalerWrap.ToPremultipliedBgra([7], 1));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 255 }, MagicScalerWrap.ToPremultipliedBgra([1, 2, 3], 3));
        CollectionAssert.AreEqual(new byte[] { 100, 50, 0, 128, 9, 9, 9, 255 },
            MagicScalerWrap.ToPremultipliedBgra([200, 100, 0, 128, 9, 9, 9, 255], 4));
    }
}
