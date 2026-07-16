using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

// ponytail: instrumented mirror of ImageReader routing; keep in sync if ImageReader's chains change.
// Reports which reader won, whether it fell back through more than one reader, and how long it took.
// Disk cache is intentionally bypassed so timings reflect real decode cost.
internal static class ProfilingImageReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal readonly record struct ProbeResult(bool Success, long ElapsedMs, string FunctionUsed, bool Fallback, bool? Locked = null);

    private sealed class Attempt
    {
        public int Count;
        public string LastName = "FileNotFound";
        public DisplayItem WinningItem; // kept alive so the file-lock check can run before disposal
    }

    private static async Task<bool> Try<T>(Attempt a, string name, Func<Task<(bool, T)>> f) where T : DisplayItem
    {
        a.Count++;
        a.LastName = name;
        var (ok, item) = await f();
        if (ok) a.WinningItem = item; // held; the probe disposes it after the (optional) lock check
        else item?.Dispose();
        return ok;
    }

    public static async Task<ProbeResult> ProbeHq(ICanvasResourceCreatorWithDpi d, string path, bool checkLock = false)
    {
        var a = new Attempt();
        var sw = Stopwatch.StartNew();
        bool ok;
        try { ok = await RunHq(a, d, path); }
        catch (Exception ex) { Logger.Error(ex); ok = false; }
        sw.Stop();

        bool? locked = null;
        if (checkLock && ok)
            locked = IsFileLocked(path); // performed while the HQ item is still alive

        a.WinningItem?.Dispose();
        return new ProbeResult(ok, sw.ElapsedMilliseconds, a.LastName, a.Count > 1, locked);
    }

    public static async Task<ProbeResult> ProbePreview(ICanvasResourceCreatorWithDpi d, string path)
    {
        var a = new Attempt();
        var sw = Stopwatch.StartNew();
        bool ok;
        try { ok = await RunPreview(a, d, path); }
        catch (Exception ex) { Logger.Error(ex); ok = false; }
        sw.Stop();
        a.WinningItem?.Dispose();
        return new ProbeResult(ok, sw.ElapsedMilliseconds, a.LastName, a.Count > 1);
    }

    // Renames the file to ren_<name> and back while the decoded item is still held.
    // Rename succeeds => the decoder released its handle => not locked (false).
    // ponytail: if a ren_<name> file already exists this reports a false positive; rare enough to ignore.
    private static bool IsFileLocked(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null) return false;
        var renamed = Path.Combine(dir, "ren_" + Path.GetFileName(path));
        try
        {
            File.Move(path, renamed);
            File.Move(renamed, path);
            return false;
        }
        catch
        {
            try { if (File.Exists(renamed) && !File.Exists(path)) File.Move(renamed, path); } catch { /* best-effort restore */ }
            return true;
        }
    }

    private static async Task<bool> RunHq(Attempt a, ICanvasResourceCreatorWithDpi d, string path)
    {
        if (!File.Exists(path)) return false;

        var extension = Path.GetExtension(path).ToUpperInvariant();
        switch (extension)
        {
            case ".HEIC":
            case ".HEIF":
            case ".HIF":
                if (await Try(a, "NativeHeifReader.GetHq", () => Task.FromResult(NativeHeifReader.GetHq(d, path)))) return true;
                if (CodecDiscovery.IsWicSupported(extension))
                    if (await Try(a, "WicReader.GetHq", () => WicReader.GetHq(d, path))) return true;
                if (CodecDiscovery.IsMagickSupported(extension))
                    if (await Try(a, "MagickNetWrap.GetHq", () => MagickNetWrap.GetHq(d, path))) return true;
                return false;
            case ".AVIF":
                if (await Try(a, "NativeAvifReader.GetHq", () => NativeAvifReader.GetHq(d, path))) return true;
                if (CodecDiscovery.IsWicSupported(extension))
                    if (await Try(a, "WicReader.GetHq", () => WicReader.GetHq(d, path))) return true;
                if (CodecDiscovery.IsMagickSupported(extension))
                    if (await Try(a, "MagickNetWrap.GetHq", () => MagickNetWrap.GetHq(d, path))) return true;
                return false;
            case ".PSD":
                if (await Try(a, "MagickNetWrap.GetHq", () => MagickNetWrap.GetHq(d, path))) return true;
                return false;
            case ".SVG":
                if (await Try(a, "ResvgWrap.GetHq", () => Task.FromResult(ResvgWrap.GetHq(d, path)))) return true;
                return false;
            case ".GIF":
                if (await Try(a, "GifReader.GetHq", () => GifReader.GetHq(d, path))) return true;
                return false;
            case ".WEBP":
                if (await Try(a, "WebpReader.GetHq", () => WebpReader.GetHq(d, path))) return true;
                return false;
            case ".PNG":
                if (await Try(a, "PngReader.GetHq", () => PngReader.GetHq(d, path))) return true;
                return false;
            case ".ICO":
            case ".ICON":
                if (await Try(a, "IcoReader.GetHq", () => IcoReader.GetHq(d, path))) return true;
                return false;
            case ".TIF":
            case ".TIFF":
                if (await Try(a, "TiffReader.GetHq", () => TiffReader.GetHq(d, path))) return true;
                return false;
            default:
                if (await RunRawDecoderPipeline(a, d, path, extension)) return true;
                if (CodecDiscovery.IsWicNonRaw(extension))
                    if (await Try(a, "WicReader.GetHq", () => WicReader.GetHq(d, path))) return true;
                if (CodecDiscovery.IsMagickNonRaw(extension))
                    if (await Try(a, "MagickNetWrap.GetHq", () => MagickNetWrap.GetHq(d, path))) return true;
                return false;
        }
    }

    private static async Task<bool> RunPreview(Attempt a, ICanvasResourceCreatorWithDpi d, string path)
    {
        if (!File.Exists(path)) return false;

        var extension = Path.GetExtension(path).ToUpperInvariant();
        switch (extension)
        {
            case ".HEIC":
            case ".HEIF":
            case ".HIF":
            case ".AVIF":
                if (await Try(a, "NativeHeifReader.GetEmbedded", () => Task.FromResult(NativeHeifReader.GetEmbedded(d, path)))) return true;
                if (CodecDiscovery.IsMagickSupported(extension))
                    if (await Try(a, "MagickNetWrap.GetResized", () => MagickNetWrap.GetResized(d, path))) return true;
                return false;
            case ".PSD":
                if (await Try(a, "PsdReader.GetEmbedded", () => PsdReader.GetEmbedded(d, path))) return true;
                return false;
            case ".SVG":
                if (await Try(a, "ResvgWrap.GetResized", () => Task.FromResult(ResvgWrap.GetResized(d, path)))) return true;
                return false;
            case ".GIF":
            case ".PNG":
            case ".BMP":
            case ".WEBP":
                if (await Try(a, "WicReader.GetResized", () => WicReader.GetResized(d, path))) return true;
                return false;
            case ".ICO":
            case ".ICON":
                if (await Try(a, "IcoReader.GetPreview", () => IcoReader.GetPreview(d, path))) return true;
                return false;
            case ".TIF":
            case ".TIFF":
                if (await Try(a, "WicReader.GetEmbedded", () => WicReader.GetEmbedded(d, path))) return true;
                if (await Try(a, "WicReader.GetResized", () => WicReader.GetResized(d, path))) return true;
                return false;
            default:
                if (CodecDiscovery.IsWicSupported(extension))
                    if (await Try(a, "WicReader.GetEmbedded", () => WicReader.GetEmbedded(d, path))) return true;
                if (CodecDiscovery.IsMagickRaw(extension))
                    if (await Try(a, "MagickNetWrap.GetEmbeddedForRawFile", () => MagickNetWrap.GetEmbeddedForRawFile(d, path))) return true;
                if (CodecDiscovery.IsWicSupported(extension))
                    if (await Try(a, "MagicScalerWrap.GetResized", () => MagicScalerWrap.GetResized(d, path))) return true;
                if (CodecDiscovery.IsMagickSupported(extension))
                    if (await Try(a, "MagickNetWrap.GetResized", () => MagickNetWrap.GetResized(d, path))) return true;
                return false;
        }
    }

    private static async Task<bool> RunRawDecoderPipeline(Attempt a, ICanvasResourceCreatorWithDpi d, string path, string extension)
    {
        foreach (var decoder in AppConfig.Settings.RawDecoderPriority)
        {
            switch (decoder)
            {
                case RawDecoder.Rawler:
                    if (CodecDiscovery.IsRawlerRaw(extension))
                        if (await Try(a, "RawlerWrapper.GetHq", () => Task.FromResult(RawlerWrapper.GetHq(d, path)))) return true;
                    break;
                case RawDecoder.WIC:
                    if (CodecDiscovery.IsWicRaw(extension))
                        if (await Try(a, "WicReader.GetHq", () => WicReader.GetHq(d, path, true))) return true;
                    break;
                case RawDecoder.ImageMagick:
                    if (CodecDiscovery.IsMagickRaw(extension))
                        if (await Try(a, "MagickNetWrap.GetHq", () => MagickNetWrap.GetHq(d, path, true))) return true;
                    break;
            }
        }
        return false;
    }
}
