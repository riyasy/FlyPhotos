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
/// <param name="Key">The photo's key in the photo list.</param>
/// <param name="Kind">What the job loads or writes.</param>
/// <param name="AllowLarge">An HQ job that may load a tiled large image: only the centre's.</param>
internal readonly record struct RunningJob(int Key, LoadKind Kind, bool AllowLarge = false);

/// <summary>How many jobs of each kind may run at once. Previews (light and heavy together) share one budget.</summary>
/// <param name="Previews">Preview jobs, light and heavy together.</param>
/// <param name="Heavy">Of those, how many may be full-image decodes (the heavy lane).</param>
/// <param name="Hq">HQ jobs. The centre's own HQ may start up to twice this (see <see cref="PrefetchPlanner"/>).</param>
/// <param name="DiskWrites">Disk-cache writes.</param>
internal readonly record struct JobLimits(int Previews, int Heavy, int Hq, int DiskWrites)
{
    /// <summary>
    /// Previews leave a core spare for the on-screen HQ decode, and stop at 8: a read benchmark (NVMe, SATA SSD,
    /// internal and USB hard disks) found small reads peak at 8 at once on every drive, and hard disks slow down
    /// past it. The heavy lane is capped whatever the core count, since each full decode can take hundreds of MB.
    /// HQ stops at 4 (the centre's own HQ may go up to twice that): the HQ window rarely needs more, and whole-file
    /// reads on a hard disk get slower in parallel.
    /// Disk-cache writes insert one at a time behind the cache's lock, so two keep one encoding while one inserts.
    /// </summary>
    public static JobLimits ForCores(int cores) => new(Math.Clamp(cores - 1, 1, 8), 4, Math.Min(cores, 4), 2);
}

/// <summary>
/// The planner's decisions for one round. Reused across rounds (the planner runs after every batch of cache
/// messages, hundreds of times a second while a window fills): <see cref="PrefetchPlanner.Make"/> clears it.
/// The prefetch cache carries the lists out in this order: <see cref="Cancel"/>, <see cref="EvictAll"/>,
/// <see cref="EvictHq"/>, <see cref="Start"/>.
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
    /// <summary>Photos in the preview window that have their preview, before and after the centre (the status text).</summary>
    public int CachedBefore, CachedAfter;

    // --- Scratch for Make, kept here so a round allocates nothing ---

    // (key, kind) of every running job, so "is this load already in flight?" is one lookup.
    internal readonly HashSet<(int, LoadKind)> Busy = [];
    // key -> position, for every key within the wider of the two windows around the centre. A key not in here is
    // outside both windows. Built once per round because keys are not ordered (see KeyWindow).
    internal readonly Dictionary<int, int> Positions = [];
    // Keys with a preview not yet in the disk cache, nearest the centre first; started only if no light preview waits.
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
/// cache runs it after each batch of events and carries out the result; nothing here touches a photo or a thread,
/// so every rule is unit tested directly (PrefetchPlannerTests).
/// <list type="bullet">
///   <item>Work goes nearest the window centre first; at equal distance the photo before the centre wins.</item>
///   <item>The centre's HQ goes first, and may use up to twice the HQ limit. Neighbour HQs start only once it is no
///     longer loading, so they don't slow the photo on screen. While HQ is held (a burst, a folder-switch quiet
///     period) no HQ starts at all.</item>
///   <item>Only the centre may load (and keep) a tiled large image; a neighbour's large HQ is deferred.</item>
///   <item>Previews needing a full decode go to the heavy lane; it shares the preview budget.</item>
///   <item>Disk-cache writes run only when no light preview is waiting, so they never slow the window filling.</item>
///   <item>A photo leaving a window is unloaded, and a job for it is cancelled.</item>
/// </list>
/// There are two windows around the centre: the <b>preview window</b> (<c>previewHalf</c> photos each side) and the
/// smaller <b>HQ window</b> (<c>hqHalf</c>). Both sizes come from the settings.
/// </summary>
internal static class PrefetchPlanner
{
    /// <summary>Fills <paramref name="plan"/> with this round's decisions.</summary>
    /// <param name="plan">Cleared, then filled.</param>
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
        // The counts include cancelled jobs whose decode has not returned: they still use a core and memory.
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
            // Up to twice limits.Hq: the slots may all be taken by slow decodes of photos already left behind (a
            // cancelled decode keeps its slot until it returns), and the photo on screen must not wait for them. The
            // ceiling still stops a race through slow files from piling decodes up.
            if (!centreSlot.HasHq && !centreLoading && !centreSlot.HqParked && hqs < limits.Hq * 2)
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
        // lightWaiting: some photo in the window still needs a light preview (started now or waiting for a place).
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
                // A full decode skipped for want of a place doesn't block the photos behind it: their light previews
                // may still fit, so the loop goes on rather than stopping.
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
