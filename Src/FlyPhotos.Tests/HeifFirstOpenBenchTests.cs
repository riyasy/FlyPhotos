using System.Diagnostics;
using FlyPhotos.Infra.Interop;

namespace FlyPhotos.Tests;

/// <summary>
/// Times the first open of every HEIF/AVIF file under FLY_HEIF_BENCH_DIR, as the decoder chain runs it: the
/// FirstPreview step (embedded thumbnail), then the full decode (LoadHqFirstPhoto). Files are read in place,
/// never copied. Writes one TSV line per file to FLY_HEIF_BENCH_OUT; inconclusive when the variables are not set.
/// Optional FLY_HEIF_BENCH_RUNS (default 3) sets the timed runs per file; the median is reported.
/// FLY_HEIF_BENCH_MODE=before times the old embedded step for comparison.
/// </summary>
[TestClass]
public sealed class HeifFirstOpenBenchTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Bench()
    {
        string? dir = Environment.GetEnvironmentVariable("FLY_HEIF_BENCH_DIR");
        string? outFile = Environment.GetEnvironmentVariable("FLY_HEIF_BENCH_OUT");
        if (dir is null || outFile is null) Assert.Inconclusive("FLY_HEIF_BENCH_DIR / FLY_HEIF_BENCH_OUT not set");
        int runs = int.TryParse(Environment.GetEnvironmentVariable("FLY_HEIF_BENCH_RUNS"), out int r) ? r : 3;

        string[] exts = [".avif", ".heic", ".heif", ".hif"];
        var files = dir.Split(';').SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

        using var w = new StreamWriter(outFile) { AutoFlush = true };
        w.WriteLine("file\tprimary\tthumb\tpreview_ms\thq_ms\ttotal_ms");
        double sumTotal = 0, sumNoThumb = 0;
        int noThumb = 0;
        foreach (string f in files)
        {
            var t = FirstOpen(f); // warm-up: file cache, decoder init
            if (t is null) { w.WriteLine($"{f}\tFAIL"); continue; }
            var timed = Enumerable.Range(0, runs).Select(_ => FirstOpen(f)!.Value).ToList();
            double preview = Median(timed.Select(x => x.previewMs)), hq = Median(timed.Select(x => x.hqMs));
            var (primary, thumb, hasThumb) = Describe(f);
            w.WriteLine($"{f}\t{primary}\t{thumb}\t{preview:F1}\t{hq:F1}\t{preview + hq:F1}");
            sumTotal += preview + hq;
            if (!hasThumb) { sumNoThumb += preview + hq; noThumb++; }
        }
        string summary = $"{files.Count} files: total {sumTotal:F0} ms; {noThumb} without thumbnail: {sumNoThumb:F0} ms";
        w.WriteLine("# " + summary);
        TestContext.WriteLine(summary);
    }

    /// <summary>
    /// The first-open sequence: the FirstPreview embedded step, then the full decode. When the embedded step
    /// returns nothing, FirstPreview falls through to the full decode itself and LoadHqFirstPhoto is skipped,
    /// so the full decode is counted once either way.
    /// </summary>
    private static (double previewMs, double hqMs)? FirstOpen(string f)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            Embedded(f);
            double preview = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            NativeHeifWrapper.DecodePrimaryImage(f, out _);
            return (preview, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>FLY_HEIF_BENCH_MODE=before times the old first-photo step (HeifEmbedded, which generates a preview).</summary>
    private static readonly bool Before = Environment.GetEnvironmentVariable("FLY_HEIF_BENCH_MODE") == "before";

    /// <summary>What the first photo's embedded step decodes; null when it yields nothing.</summary>
    private static NativeHeifWrapper.HeifImage? Embedded(string f)
    {
        try { return NativeHeifWrapper.DecodeThumbnail(f, embeddedOnly: !Before); }
        catch (Exception) { return null; }
    }

    private static (string primary, string thumb, bool hasThumb) Describe(string f)
    {
        var p = NativeHeifWrapper.DecodeThumbnail(f);
        bool hasThumb = NativeHeifWrapper.DecodeThumbnail(f, embeddedOnly: true) != null;
        return ($"{p.PrimaryImageWidth}x{p.PrimaryImageHeight}", hasThumb ? $"{p.Width}x{p.Height}" : "none", hasThumb);
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.Order().ToArray();
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
}
