#nullable enable
using System;
using System.Collections.Generic;
using FlyPhotos.Display.State;

namespace FlyPhotos.Display.Controllers;

/// <summary>What a prefetch job does.</summary>
internal enum LoadKind
{
    /// <summary>A preview from the cheap sources (disk cache, embedded thumbnail), at full parallelism.</summary>
    PreviewLight,
    /// <summary>A preview that needs a full-image decode: the heavy lane, few at once (#276).</summary>
    PreviewHeavy,
    /// <summary>The full-quality image (a tiled large image only for the window centre).</summary>
    Hq,
    /// <summary>Writes a freshly decoded preview to the disk cache.</summary>
    DiskWrite
}

/// <summary>What the prefetch cache knows about one photo, as the planner needs it.</summary>
/// <param name="HasPreview">A preview is stored.</param>
/// <param name="HasHq">An HQ is stored.</param>
/// <param name="TiledHq">The HQ is a tiled large image (gigabytes of CPU tiles).</param>
/// <param name="NeedsHeavy">Its light preview pass found nothing: only the heavy lane can make its preview.</param>
/// <param name="NeedsDiskWrite">Its preview was decoded from the file and is not in the disk cache yet.</param>
/// <param name="PreviewParked">A preview load failed; not retried until the photo is the window centre.</param>
/// <param name="HqParked">An HQ load failed or was deferred (too large for a neighbour); as above.</param>
internal readonly record struct SlotState(bool HasPreview, bool HasHq, bool TiledHq = false, bool NeedsHeavy = false,
    bool NeedsDiskWrite = false, bool PreviewParked = false, bool HqParked = false);

/// <summary>A job to start, or one the cache is running (or cancelled and not yet returned).</summary>
/// <param name="AllowLarge">An HQ job that may load a tiled large image: only the centre's.</param>
internal readonly record struct RunningJob(int Key, LoadKind Kind, bool AllowLarge = false);

/// <summary>How many jobs of each kind may run at once. Previews (light and heavy together) share one budget.</summary>
internal readonly record struct JobLimits(int Previews, int Heavy, int Hq, int DiskWrites)
{
    /// <summary>
    /// Previews leave a core spare for the on-screen HQ decode; the heavy lane is capped whatever the core count,
    /// since each full decode can take hundreds of MB.
    /// </summary>
    public static JobLimits ForCores(int cores) => new(Math.Max(1, cores - 1), 4, cores, cores);
}

/// <summary>
/// The planner's decisions for one round. Reused across rounds (the planner runs after every batch of cache
/// messages, hundreds of times a second while a window fills): <see cref="PrefetchPlanner.Make"/> clears it.
/// </summary>
internal sealed class PrefetchPlan
{
    /// <summary>Jobs to start, in priority order.</summary>
    public readonly List<RunningJob> Start = [];
    /// <summary>Indexes into the running list of jobs to cancel; their results are discarded.</summary>
    public readonly List<int> Cancel = [];
    /// <summary>Photos to unload completely (preview and HQ).</summary>
    public readonly List<int> EvictAll = [];
    /// <summary>Photos to unload the HQ of, keeping the preview.</summary>
    public readonly List<int> EvictHq = [];
    /// <summary>Photos in the preview window that have their preview, before and after the centre.</summary>
    public int CachedBefore, CachedAfter;

    // Scratch for Make.
    internal readonly HashSet<(int, LoadKind)> Busy = [];
    internal readonly Dictionary<int, int> Positions = []; // key -> position, for the window around the centre
    internal readonly List<int> DiskWrites = [];

    /// <summary>Empties every list for the next round.</summary>
    public void Clear()
    {
        Start.Clear();
        Cancel.Clear();
        EvictAll.Clear();
        EvictHq.Clear();
        CachedBefore = CachedAfter = 0;
        Busy.Clear();
        Positions.Clear();
        DiskWrites.Clear();
    }
}

/// <summary>
/// Every prefetch decision, as one pure function of the window, what is loaded and what is running. The prefetch
/// cache runs it after each batch of events and carries out the result; nothing here touches a photo or a thread.
/// <list type="bullet">
///   <item>Work goes nearest the window centre first; at equal distance the photo before the centre wins.</item>
///   <item>The centre's HQ goes first. Neighbour HQs start only once it is no longer loading, so they don't slow the
///     photo on screen. While HQ is held (a burst, a folder-switch quiet period) no HQ starts at all.</item>
///   <item>Only the centre may load (and keep) a tiled large image; a neighbour's large HQ is deferred.</item>
///   <item>Previews needing a full decode go to the heavy lane; it shares the preview budget.</item>
///   <item>Disk-cache writes run only when no light preview is waiting, so they never slow the window filling.</item>
///   <item>A photo leaving a window is unloaded, and a job for it is cancelled.</item>
/// </list>
/// </summary>
internal static class PrefetchPlanner
{
    /// <summary>Fills <paramref name="plan"/> with this round's decisions.</summary>
    /// <param name="nav">The window centre: the viewer's key list and position.</param>
    /// <param name="loaded">Keys that hold a preview or HQ (the candidates for unloading).</param>
    /// <param name="slotOf">What a key has loaded and what the cache knows about it.</param>
    /// <param name="running">The jobs in flight; <see cref="PrefetchPlan.Cancel"/> indexes into this list.</param>
    /// <param name="holdHq">A burst or folder-switch quiet period: start no HQ.</param>
    /// <param name="previewHalf">Photos each side of the centre that keep a preview.</param>
    /// <param name="hqHalf">Photos each side of the centre that keep an HQ.</param>
    /// <param name="limits">How many jobs of each kind may run at once.</param>
    public static void Make(PrefetchPlan plan, NavState nav, HashSet<int> loaded,
        Func<int, SlotState> slotOf, IReadOnlyList<RunningJob> running, bool holdHq,
        int previewHalf, int hqHalf, JobLimits limits)
    {
        plan.Clear();
        var (keys, centre) = nav;
        if (centre < 0 || centre >= keys.Count) return;
        int centreKey = keys[centre];
        var (lo, hi) = KeyWindow.Range(keys.Count, centre, Math.Max(previewHalf, hqHalf));
        for (int pos = lo; pos <= hi; pos++) plan.Positions[keys[pos]] = pos;

        // --- What is running, and what to cancel ---
        int previews = 0, heavy = 0, hqs = 0, diskWrites = 0;
        for (int i = 0; i < running.Count; i++)
        {
            var job = running[i];
            plan.Busy.Add((job.Key, job.Kind));
            switch (job.Kind)
            {
                case LoadKind.PreviewLight: previews++; break;
                case LoadKind.PreviewHeavy: previews++; heavy++; break;
                case LoadKind.Hq: hqs++; break;
                case LoadKind.DiskWrite: diskWrites++; continue; // writes always finish
            }
            bool leftWindow = !Within(plan, job.Key, centre, job.Kind == LoadKind.Hq ? hqHalf : previewHalf);
            bool largeLeftCentre = job.Kind == LoadKind.Hq && job.AllowLarge && job.Key != centreKey;
            if (leftWindow || largeLeftCentre) plan.Cancel.Add(i);
        }

        // --- Evictions ---
        // A photo in either window keeps what it has: with an HQ window wider than the preview window (a hand-edited
        // setting) a neighbour's HQ would otherwise be unloaded right after it loads, and loaded again, forever.
        int keepHalf = Math.Max(previewHalf, hqHalf);
        foreach (int key in loaded)
        {
            if (!Within(plan, key, centre, keepHalf))
            {
                plan.EvictAll.Add(key);
                continue;
            }
            var slot = slotOf(key);
            if (slot.HasHq && (!Within(plan, key, centre, hqHalf) || slot.TiledHq && key != centreKey))
                plan.EvictHq.Add(key);
        }

        // --- HQ: the centre first, neighbours once it is done ---
        if (!holdHq)
        {
            var centreSlot = slotOf(centreKey);
            bool centreLoading = plan.Busy.Contains((centreKey, LoadKind.Hq));
            if (!centreSlot.HasHq && !centreLoading && !centreSlot.HqParked && hqs < limits.Hq)
            {
                plan.Start.Add(new RunningJob(centreKey, LoadKind.Hq, AllowLarge: true));
                hqs++;
                centreLoading = true;
            }
            if (!centreLoading)
                foreach (int pos in KeyWindow.NearestFirst(keys.Count, centre, hqHalf))
                {
                    if (hqs >= limits.Hq) break;
                    if (pos == centre) continue;
                    int key = keys[pos];
                    if (plan.Busy.Contains((key, LoadKind.Hq))) continue;
                    var slot = slotOf(key);
                    if (slot.HasHq || slot.HqParked) continue;
                    plan.Start.Add(new RunningJob(key, LoadKind.Hq));
                    hqs++;
                }
        }

        // --- Previews (light and heavy lanes, one budget); also counts the cached ones and finds disk writes ---
        bool lightWaiting = false;
        foreach (int pos in KeyWindow.NearestFirst(keys.Count, centre, previewHalf))
        {
            int key = keys[pos];
            var slot = slotOf(key);
            if (slot.HasPreview)
            {
                if (pos < centre) plan.CachedBefore++;
                else if (pos > centre) plan.CachedAfter++;
                if (slot.NeedsDiskWrite && !plan.Busy.Contains((key, LoadKind.DiskWrite))) plan.DiskWrites.Add(key);
                continue;
            }
            if (slot.PreviewParked || plan.Busy.Contains((key, LoadKind.PreviewLight))
                                   || plan.Busy.Contains((key, LoadKind.PreviewHeavy))) continue;
            if (slot.NeedsHeavy)
            {
                if (previews >= limits.Previews || heavy >= limits.Heavy) continue;
                plan.Start.Add(new RunningJob(key, LoadKind.PreviewHeavy));
                previews++;
                heavy++;
            }
            else
            {
                lightWaiting = true;
                if (previews >= limits.Previews) continue;
                plan.Start.Add(new RunningJob(key, LoadKind.PreviewLight));
                previews++;
            }
        }

        // --- Disk-cache writes, once no light preview is waiting for a slot ---
        if (lightWaiting) return;
        foreach (int key in plan.DiskWrites)
        {
            if (diskWrites >= limits.DiskWrites) break;
            plan.Start.Add(new RunningJob(key, LoadKind.DiskWrite));
            diskWrites++;
        }
    }

    /// <summary>Whether <paramref name="key"/> lies within <paramref name="half"/> of the centre.</summary>
    private static bool Within(PrefetchPlan plan, int key, int centre, int half) =>
        plan.Positions.TryGetValue(key, out int pos) && Math.Abs(pos - centre) <= half;
}
