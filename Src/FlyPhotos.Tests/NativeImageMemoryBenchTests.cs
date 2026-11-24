using System.Diagnostics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Tests;

/// <summary>
/// HQ load time, peak commit and leftover commit for one file through the real reader, on the GPU. Set
/// FLY_NATIVE_BENCH to "heif|avif|dds|raw|large;path" and FLY_NATIVE_BENCH_OUT to a file the TSV line is appended
/// to; inconclusive otherwise. One file per process, so the peak is that file's.
/// </summary>
[TestClass]
public sealed class NativeImageMemoryBenchTests
{
    [TestMethod]
    public async Task Bench()
    {
        string? spec = Environment.GetEnvironmentVariable("FLY_NATIVE_BENCH");
        string? outFile = Environment.GetEnvironmentVariable("FLY_NATIVE_BENCH_OUT");
        if (spec is null || outFile is null) Assert.Inconclusive("FLY_NATIVE_BENCH / FLY_NATIVE_BENCH_OUT not set");
        var (kind, path) = (spec.Split(';')[0], spec.Split(';')[1]);
        AppConfig.Settings = new AppSettings { DecodeRawData = true };

        using var device = new CanvasDevice();
        using var ctrl = new CanvasRenderTarget(device, 1, 1, 96);
        await Load(kind, path, ctrl); // warm-up
        Collect();
        long afterWarmUp = Commit().current;

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await Load(kind, path, ctrl);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Collect();
        }
        var (current, peak) = Commit();
        times.Sort();
        File.AppendAllText(outFile, $"{kind}\t{Path.GetFileName(path)}\t{times[2]:F1} ms\tpeak {peak >> 20} MB" +
                                    $"\tleft after 5 loads {(current - afterWarmUp) >> 20} MB{Environment.NewLine}");
    }

    private static async Task Load(string kind, string path, CanvasRenderTarget ctrl)
    {
        DisplayItem? item = kind switch
        {
            "heif" => (await NativeHeifReader.GetHq(ctrl, new ImageFile(path))).Item2,
            "avif" => (await NativeAvifReader.GetHq(ctrl, new ImageFile(path))).Item2,
            "dds" => NativeDdsReader.GetHq(ctrl, path).Item2,
            "raw" => RawlerWrapper.GetHq(ctrl, path).Item2,
            "large" => LargeImageReader.DecodeHeif(ctrl, path, CancellationToken.None),
            _ => throw new ArgumentException(kind),
        };
        Assert.IsNotNull(item?.Bitmap);
        item.Release();
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Commit charge (psapi PagefileUsage / PeakPagefileUsage) under .NET's names.
    private static (long current, long peak) Commit()
    {
        using var p = Process.GetCurrentProcess();
        return (p.PagedMemorySize64, p.PeakPagedMemorySize64);
    }
}
