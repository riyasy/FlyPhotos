using System.Diagnostics;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Tests;

/// <summary>
/// NativeImage holds a decoder's pixels in native memory until Dispose, and PixelUpload hands any span to Win2D
/// without a copy of our own. Checks the bytes arrive intact and the memory is really freed.
/// </summary>
[TestClass]
public sealed class NativeImageTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Heif", name);

    [TestMethod]
    public void Uploaded_bitmap_has_the_decoded_pixels()
    {
        using var img = NativeHeifWrapper.DecodePrimaryImage(Fixture("alpha.heic"));
        using var bitmap = img.ToBitmap(TestGpu.Device);

        Assert.AreEqual((uint)img.Width, bitmap.SizeInPixels.Width);
        Assert.AreEqual((uint)img.Height, bitmap.SizeInPixels.Height);
        CollectionAssert.AreEqual(img.Pixels.ToArray(), bitmap.GetPixelBytes());
    }

    [TestMethod]
    public void Managed_pixels_upload_unchanged()
    {
        byte[] pixels = Enumerable.Range(0, 3 * 2 * 4).Select(i => i % 4 == 3 ? (byte)255 : (byte)(i * 9)).ToArray();
        using var bitmap = PixelUpload.CreateBitmap(TestGpu.Device, pixels, 3, 2, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        CollectionAssert.AreEqual(pixels, bitmap.GetPixelBytes());
    }

    [TestMethod]
    public void A_disposed_image_refuses_reads_and_frees_once()
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(Fixture("quadrants.heic"));
        img.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => img.Pixels.Length);
        Assert.ThrowsExactly<ObjectDisposedException>(() => img.ToBitmap(TestGpu.Device));
        img.Dispose();
    }

    [TestMethod]
    public void Dispose_returns_the_native_memory()
    {
        // 1000x500 RGBA: 2 MB each, so 50 held images are 100 MB of native memory the GC never sees.
        const int Count = 50, ImageMb = 2;
        long start = CommitMb();
        var held = Enumerable.Range(0, Count).Select(_ => NativeHeifWrapper.DecodePrimaryImage(Fixture("large_no_thumbnail.heic"))).ToList();
        long whileHeld = CommitMb() - start;
        held.ForEach(i => i.Dispose());
        long afterDispose = CommitMb() - start;

        // The device keeps pools of its own that swing by 30-60 MB, as much for a byte[] upload; a leak of every
        // image would be 400 MB here.
        DecodeAndUpload(Count);
        long beforeLoop = CommitMb();
        DecodeAndUpload(4 * Count);
        long loopGrowth = CommitMb() - beforeLoop;

        Assert.IsGreaterThanOrEqualTo(Count * ImageMb * 8 / 10, whileHeld, "the measurement sees the held images");
        Assert.IsLessThan(Count * ImageMb / 5, afterDispose, $"held {whileHeld} MB, after Dispose {afterDispose} MB");
        Assert.IsLessThan(Count * ImageMb, loopGrowth, $"{4 * Count} decode + upload loops grew {loopGrowth} MB");
    }

    private static void DecodeAndUpload(int times)
    {
        for (int i = 0; i < times; i++)
        {
            using var img = NativeHeifWrapper.DecodePrimaryImage(Fixture("large_no_thumbnail.heic"));
            using var bitmap = img.ToBitmap(TestGpu.Device);
        }
    }

    /// <summary>Commit after a GC, and after a draw and Trim: a device frees disposed bitmaps only once it flushes.</summary>
    private static long CommitMb()
    {
        using (var target = new CanvasRenderTarget(TestGpu.Device, 1, 1, 96))
        using (target.CreateDrawingSession()) { }
        TestGpu.Device.Trim();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        return p.PagedMemorySize64 >> 20; // commit charge (psapi PagefileUsage)
    }
}
