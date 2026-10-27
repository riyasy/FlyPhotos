#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

// Runs ImageReader's own decoder chains, so the profiler can't drift from what the viewer does.
// Reports which reader won, whether it fell back through more than one, and how long it took.
// Disk cache is intentionally bypassed so timings reflect real decode cost.
internal static class ProfilingImageReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal readonly record struct ProbeResult(bool Success, long ElapsedMs, string FunctionUsed, bool Fallback, bool? Locked = null);

    public static Task<ProbeResult> ProbeHq(ICanvasResourceCreatorWithDpi d, string path, bool checkLock = false) =>
        Probe(d, path, Purpose.Hq, checkLock);

    public static Task<ProbeResult> ProbePreview(ICanvasResourceCreatorWithDpi d, string path) =>
        Probe(d, path, Purpose.Preview, checkLock: false);

    /// <summary>Runs a single reader on a file, for the per-function timings; disposes what it decoded.</summary>
    public static async Task<bool> RunOne(ICanvasResourceCreatorWithDpi d, string path, DecodeStep step)
    {
        using var file = new ImageFile(path);
        var item = await ImageReader.Run(step, d, file);
        item?.Dispose();
        return item != null;
    }

    /// <summary>Times one decode through ImageReader's chains, counting and naming the readers it tried.</summary>
    private static async Task<ProbeResult> Probe(ICanvasResourceCreatorWithDpi d, string path, Purpose purpose, bool checkLock)
    {
        if (!File.Exists(path)) return new ProbeResult(false, 0, "FileNotFound", false);

        var attempts = 0;
        var lastReader = "";
        DisplayItem? item = null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var file = new ImageFile(path);
            item = await ImageReader.Decode(d, file, purpose, step =>
            {
                attempts++;
                lastReader = step.ToString();
            });
        }
        catch (Exception ex) { Logger.Error(ex); }
        sw.Stop();

        // Done while the item is still alive, after the file's own stream is closed.
        bool? locked = checkLock && item != null ? IsFileLocked(path) : null;
        item?.Dispose();
        return new ProbeResult(item != null, sw.ElapsedMilliseconds, lastReader, attempts > 1, locked);
    }

    // Renames the file to ren_<name> and back while the decoded item is still held.
    // Rename succeeds => the decoder released its handle => not locked (false).
    // If a ren_<name> file already exists this reports a false positive; rare enough to ignore.
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
}
