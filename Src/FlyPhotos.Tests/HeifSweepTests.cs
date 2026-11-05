using System.Runtime.InteropServices;
using FlyPhotos.Infra.Interop;

namespace FlyPhotos.Tests;

/// <summary>
/// Decodes every HEIF/AVIF file under FLY_HEIF_SWEEP_DIR and writes one TSV line per file to
/// FLY_HEIF_SWEEP_OUT, so two builds of the HEIF DLLs can be diffed (see Src\build_libheif\readme.md).
/// Inconclusive when the variables are not set.
/// </summary>
[TestClass]
public sealed class HeifSweepTests
{
    [TestMethod]
    public void Sweep()
    {
        string? dir = Environment.GetEnvironmentVariable("FLY_HEIF_SWEEP_DIR");
        string? outFile = Environment.GetEnvironmentVariable("FLY_HEIF_SWEEP_OUT");
        if (dir is null || outFile is null) Assert.Inconclusive("FLY_HEIF_SWEEP_DIR / FLY_HEIF_SWEEP_OUT not set");

        string[] exts = [".avif", ".avifs", ".heic", ".heif", ".hif", ".heics", ".heifs"];
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.OrdinalIgnoreCase);

        using var w = new StreamWriter(outFile) { AutoFlush = true };
        w.WriteLine("file\tprimary\tthumbnail\tframes");
        foreach (string f in files)
        {
            w.WriteLine($"{Path.GetRelativePath(dir, f)}\t{Primary(f)}\t{Thumb(f)}\t{Frames(f)}");
        }
    }

    private static string Primary(string f)
    {
        try
        {
            var img = NativeHeifWrapper.DecodePrimaryImage(f, out bool animated);
            return $"OK {img.Width}x{img.Height}{(animated ? " animated" : "")} sum={Checksum(img.Pixels)}";
        }
        catch (Exception e) { return "FAIL " + e.Message.ReplaceLineEndings(" "); }
    }

    private static string Thumb(string f)
    {
        try
        {
            var img = NativeHeifWrapper.DecodeThumbnail(f);
            return $"OK {img.Width}x{img.Height}";
        }
        catch (Exception e) { return "FAIL " + e.Message.ReplaceLineEndings(" "); }
    }

    /// <summary>Frame count and durations of the first pass (capped at 300 frames), as AvifAnimator plays it.</summary>
    private static string Frames(string f)
    {
        byte[] bytes = File.ReadAllBytes(f);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        IntPtr h = NativeAvifBridge.OpenAvifAnimation(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
        try
        {
            if (h == IntPtr.Zero) return "open-failed";
            if (!NativeAvifBridge.IsAvifAnimated(h)) return "still";
            var frame = new byte[NativeAvifBridge.GetAvifCanvasWidth(h) * NativeAvifBridge.GetAvifCanvasHeight(h) * 4];
            var fp = GCHandle.Alloc(frame, GCHandleType.Pinned);
            var durations = new List<int>();
            try
            {
                for (int i = 0; i < 300; i++)
                {
                    int d = NativeAvifBridge.DecodeNextAvifFrame(h, fp.AddrOfPinnedObject());
                    if (d <= 0) break;
                    durations.Add(d);
                }
            }
            finally { fp.Free(); }
            return $"{durations.Count} frames [{string.Join(",", durations.Take(12))}{(durations.Count > 12 ? ",..." : "")}]";
        }
        finally
        {
            NativeAvifBridge.CloseAvifAnimation(h);
            pin.Free();
        }
    }

    private static ulong Checksum(byte[] px)
    {
        ulong s = 0;
        for (int i = 0; i < px.Length; i += 97) s = s * 31 + px[i];
        return s;
    }
}
