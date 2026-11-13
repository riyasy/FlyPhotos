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
    Hq,
    /// <summary>Writes a freshly decoded preview to the disk cache.</summary>
    DiskWrite
}

/// <summary>What the prefetch cache knows about one photo, as the planner needs it.</summary>
/// <param name="TiledHq">The HQ is a tiled large image (gigabytes of CPU tiles).</param>
/// <param name="NeedsHeavy">Its light preview pass found nothing: only the heavy lane can make its preview.</param>
/// <param name="NeedsDiskWrite">Its preview was decoded from the file and is not in the disk cache yet.</param>
/// <param name="PreviewParked">A preview load failed; not retried until the window centre moves.</param>
/// <param name="HqParked">An HQ load failed or was deferred (too large for a neighbour); as above.</param>
internal readonly record struct SlotState(bool HasPreview, bool HasHq, bool TiledHq = false, bool NeedsHeavy = false,
    bool NeedsDiskWrite = false, bool PreviewParked = false, bool HqParked = false);

/// <summary>A job the cache is running (or one that was cancelled and has not returned yet).</summary>
internal readonly record struct RunningJob(int Key, LoadKind Kind, bool AllowLarge);

/// <summary>How many jobs of each kind may run at once. Previews (light and heavy together) share one budget.</summary>
internal readonly record struct JobLimits(int Previews, int Heavy, int Hq, int DiskWrites)
{
    /// <summary>
    /// Previews leave a core spare for the on-screen HQ decode; the heavy lane is capped whatever the core count,
    /// since each full decode can take hundreds of MB.
    /// </summary>
    public static JobLimits ForCores(int cores) => new(Math.Max(1, cores - 1), 4, cores, cores);
}

/// <summary>The planner's decisions for one round. Reused across rounds: <see cref="PrefetchPlanner.Make"/> clears it.</summary>
internal sealed class PrefetchPlan
{
    public readonly List<(int Key, LoadKind Kind, bool AllowLarge)> Start = [];
    /// <summary>Indexes into the running list of jobs to cancel; their results are discarded.</summary>
    public readonly List<int> Cancel = [];
    /// <summary>Photos to unload completely (preview and HQ).</summary>
    public readonly List<int> EvictAll = [];
    /// <summary>Photos to unload the HQ of, keeping the preview.</summary>
    public readonly List<int> EvictHq = [];

    public void Clear()
    {
        Start.Clear();
        Cancel.Clear();
        EvictAll.Clear();
        EvictHq.Clear();
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
    public static void Make(PrefetchPlan plan, IReadOnlyList<int> keys, int centre, IEnumerable<int> loaded,
        Func<int, SlotState> slotOf, IReadOnlyList<RunningJob> running, bool holdHq,
        int previewHalf, int hqHalf, JobLimits limits)
    {
        plan.Clear();
        if (centre < 0 || centre >= keys.Count) return;
        int centreKey = keys[centre];

        // --- What is running, and what to cancel ---
        int previews = 0, heavy = 0, hqs = 0, diskWrites = 0;
        var busy = new HashSet<(int, LoadKind)>();
        for (int i = 0; i < running.Count; i++)
        {
            var job = running[i];
            busy.Add((job.Key, job.Kind));
            switch (job.Kind)
            {
                case LoadKind.PreviewLight: previews++; break;
                case LoadKind.PreviewHeavy: previews++; heavy++; break;
                case LoadKind.Hq: hqs++; break;
                case LoadKind.DiskWrite: diskWrites++; break;
            }
            int half = job.Kind == LoadKind.Hq ? hqHalf : previewHalf;
            bool leftWindow = KeyWindow.IndexOf(keys, job.Key, centre, half) < 0;
            bool largeLeftCentre = job.Kind == LoadKind.Hq && job.AllowLarge && job.Key != centreKey;
            if ((leftWindow || largeLeftCentre) && job.Kind != LoadKind.DiskWrite) plan.Cancel.Add(i);
        }

        // --- Evictions ---
        foreach (int key in loaded)
        {
            if (KeyWindow.IndexOf(keys, key, centre, previewHalf) < 0)
            {
                plan.EvictAll.Add(key);
                continue;
            }
            var slot = slotOf(key);
            if (!slot.HasHq) continue;
            if (KeyWindow.IndexOf(keys, key, centre, hqHalf) < 0 || slot.TiledHq && key != centreKey)
                plan.EvictHq.Add(key);
        }

        // --- HQ: the centre first, neighbours once it is done ---
        if (!holdHq)
        {
            var centreSlot = slotOf(centreKey);
            bool centreLoading = busy.Contains((centreKey, LoadKind.Hq));
            if (!centreSlot.HasHq && !centreLoading && !centreSlot.HqParked && hqs < limits.Hq)
            {
                plan.Start.Add((centreKey, LoadKind.Hq, true));
                hqs++;
                centreLoading = true;
            }
            if (!centreLoading)
                foreach (int key in NearestFirst(keys, centre, hqHalf))
                {
                    if (hqs >= limits.Hq) break;
                    if (key == centreKey || busy.Contains((key, LoadKind.Hq))) continue;
                    var slot = slotOf(key);
                    if (slot.HasHq || slot.HqParked) continue;
                    plan.Start.Add((key, LoadKind.Hq, false));
                    hqs++;
                }
        }

        // --- Previews: light and heavy lanes, one budget ---
        bool lightWaiting = false;
        foreach (int key in NearestFirst(keys, centre, previewHalf))
        {
            if (busy.Contains((key, LoadKind.PreviewLight)) || busy.Contains((key, LoadKind.PreviewHeavy))) continue;
            var slot = slotOf(key);
            if (slot.HasPreview || slot.PreviewParked) continue;
            if (slot.NeedsHeavy)
            {
                if (previews >= limits.Previews || heavy >= limits.Heavy) continue;
                plan.Start.Add((key, LoadKind.PreviewHeavy, false));
                previews++;
                heavy++;
            }
            else
            {
                lightWaiting = true;
                if (previews >= limits.Previews) continue;
                plan.Start.Add((key, LoadKind.PreviewLight, false));
                previews++;
            }
        }

        // --- Disk-cache writes, once no light preview is waiting for a slot ---
        if (lightWaiting) return;
        foreach (int key in NearestFirst(keys, centre, previewHalf))
        {
            if (diskWrites >= limits.DiskWrites) break;
            if (busy.Contains((key, LoadKind.DiskWrite)) || !slotOf(key).NeedsDiskWrite) continue;
            plan.Start.Add((key, LoadKind.DiskWrite, false));
            diskWrites++;
        }
    }

    /// <summary>The keys within <paramref name="half"/> of the centre: the centre, then before/after at each distance.</summary>
    private static IEnumerable<int> NearestFirst(IReadOnlyList<int> keys, int centre, int half)
    {
        yield return keys[centre];
        for (int d = 1; d <= half; d++)
        {
            int before = centre - d, after = centre + d;
            if (before < 0 && after >= keys.Count) yield break;
            if (before >= 0) yield return keys[before];
            if (after < keys.Count) yield return keys[after];
        }
    }
}
