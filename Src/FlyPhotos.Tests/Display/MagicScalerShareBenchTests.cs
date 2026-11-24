using System.Diagnostics;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Tests.Display.Harness;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// MagicScaler preview time for one file read locally and from a share, and whether both give the same pixels.
/// Set FLY_MS_BENCH to "localPath;sharePath" and FLY_MS_BENCH_OUT to a file the TSV line is appended to;
/// inconclusive otherwise.
/// </summary>
[TestClass]
public sealed class MagicScalerShareBenchTests
{
    private static readonly CanvasRenderTarget Ctrl = new(TestGpu.Device, 1, 1, 96);

    [TestMethod]
    public async Task Bench()
    {
        string? spec = Environment.GetEnvironmentVariable("FLY_MS_BENCH");
        string? outFile = Environment.GetEnvironmentVariable("FLY_MS_BENCH_OUT");
        if (spec is null || outFile is null) Assert.Inconclusive("FLY_MS_BENCH / FLY_MS_BENCH_OUT not set");
        var (local, share) = (spec.Split(';')[0], spec.Split(';')[1]);

        var (localMs, localPixels) = await Time(local);
        var (shareMs, sharePixels) = await Time(share);
        CollectionAssert.AreEqual(localPixels, sharePixels, "local and share previews differ");
        File.AppendAllText(outFile, $"{Path.GetFileName(local)}\tlocal {localMs:F1} ms\tshare {shareMs:F1} ms{Environment.NewLine}");
    }

    private static async Task<(double, byte[])> Time(string path)
    {
        byte[] pixels = await Load(path); // warm-up
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await Load(path);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return (times[2], pixels);
    }

    private static async Task<byte[]> Load(string path)
    {
        using var file = new ImageFile(path);
        var (ok, item) = await MagicScalerWrap.GetResized(Ctrl, file);
        Assert.IsTrue(ok, path);
        var pixels = item.Bitmap.GetPixelBytes();
        item.Release();
        return pixels;
    }
}
