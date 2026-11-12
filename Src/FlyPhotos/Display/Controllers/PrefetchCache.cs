#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Owns all background photo loading: the preview, HQ and disk <b>tiers</b> (each a worker thread
/// with its own queue, in-flight set and completed-keys set), throttling, the sliding <b>window</b>
/// of neighbours kept loaded around the <b>window centre</b>, and disk write-back. Previews that need a
/// full-image decode load in a <b>heavy lane</b> of the preview tier, with few slots (#276).
///
/// Nothing outside this class ever sees a tier. Callers drive it with <see cref="MoveWindow"/> when
/// navigation moves and ask it questions with <see cref="IsHqLoaded"/> / <see cref="IsPreviewLoaded"/>;
/// it announces completions through the ready events. See CONTEXT.md for the vocabulary.
///
/// Load order: the window centre (the photo on screen) gets its HQ decode first. Neighbour HQ decodes
/// start only once it has loaded (<see cref="NeighbourMustWait"/>), and previews leave a core spare, so
/// background work doesn't slow the image the user is waiting for. The UI thread never waits on any of it.
///
/// The class does not own the photo list: it resolves keys to photos through a delegate handed in at
/// construction, and is told the key snapshot + centre on every <see cref="MoveWindow"/> call.
/// </summary>
internal sealed class PrefetchCache : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>One quality track: its work queue, wake signal, in-flight set and completed-keys set.</summary>
    private sealed class PhotoCacheTier : IDisposable
    {
        public readonly ConcurrentStack<int>            Queue    = new();
        public readonly AutoResetEvent                  Signal   = new(false);
        public readonly ConcurrentDictionary<int, byte> InFlight = new();
        public readonly ConcurrentDictionary<int, byte> Done     = new();

        public void Dispose() => Signal.Dispose();
    }

    private readonly CancellationTokenSource _cts = new();
    // One fewer than the cores, so preview decodes leave some room for the on-screen HQ decode.
    private readonly SemaphoreSlim _previewThrottler   = new(Math.Max(1, Environment.ProcessorCount - 1));
    private readonly SemaphoreSlim _hqThrottler        = new(Environment.ProcessorCount);
    private readonly SemaphoreSlim _diskCacheThrottler = new(Environment.ProcessorCount);

    private readonly PhotoCacheTier       _previewTier     = new();
    private readonly PhotoCacheTier       _hqTier          = new();
    private readonly ConcurrentStack<int> _diskCacheQueue  = new();
    private readonly ManualResetEvent     _diskCacheSignal = new(false);

    // The heavy lane: previews that need a full-image decode (hundreds of MB each, #276). A preview load
    // first tries only its light steps; when those find nothing, the key is marked heavy and queued here,
    // where few run at once whatever the core count. A heavy load holds a preview slot as well as a heavy
    // one, so the preview tier as a whole still never runs more than _previewThrottler allows (a core stays
    // free for the on-screen HQ decode). Claims and completions are recorded in _previewTier: to the rest
    // of the class a heavy preview is just a preview.
    private const int MaxHeavyPreviewDecodes = 4;
    private readonly SemaphoreSlim _heavyPreviewThrottler = new(MaxHeavyPreviewDecodes);
    // Not a stack like the other queues: keys arrive here in the order their light attempts end, nearest first,
    // so a stack would load the far end of the window first. The worker picks the pending key nearest the centre.
    private readonly ConcurrentDictionary<int, byte> _heavyPreviewPending = new();
    private readonly AutoResetEvent                  _heavyPreviewSignal = new(false);
    // Keys whose light attempt found nothing and whose preview hasn't loaded since: a window move queues them
    // straight to the heavy lane. Cleared when the preview loads, so a key that comes back into the window
    // later tries the light pass again, which finds its thumbnail in the disk cache at full parallelism.
    private readonly ConcurrentDictionary<int, byte> _heavyKeys          = new();

    // Reused across SyncCacheTier calls — only touched on the UI/STA thread (inside MoveWindow).
    private readonly HashSet<int> _syncKeysToKeep = new();

    // Items detached by the current window move, disposed together by FlushRetiring. UI/STA thread only.
    private readonly List<DisplayItem> _retiring = new();

    // Bumped (on the UI/STA thread) whenever RAW decode settings change so any RAW HQ decode that was
    // already in flight is discarded on completion instead of committing a stale bitmap.
    private volatile int _hqGeneration;

    // Resolves a key to its Photo (or null). Read-only access into the controller's PhotoList; the
    // controller owns the collection and disposal timing.
    private readonly Func<int, Photo?> _getPhoto;
    private readonly ICanvasResourceCreatorWithDpi _device;

    // True while the user is holding a navigation key or skipping through folders. The HQ worker re-parks
    // while this is set so it doesn't decode photos being skipped past. Supplied by the controller.
    private readonly Func<bool> _isBursting;

    // Latest window pushed in by MoveWindow. Replaces the old reach-ins to PhotoSessionState +
    // _sortedPhotoKeys. _windowKeys is the full position-ordered key list (key values are NOT ordered);
    // _windowCentre is the position in it.
    private volatile IReadOnlyList<int> _windowKeys = Array.Empty<int>();
    private volatile int _windowCentre;

    // Cancelled whenever the centre photo changes, so a multi-second large-image decode for a photo the user
    // has left stops early (only the large-image path observes it). Replaced on the UI/STA thread only.
    // Replaced sources are cancelled, not disposed — they hold no timer, and an in-flight decode may
    // still register on its token; the GC reclaims them.
    private volatile CancellationTokenSource _centreCts = new();
    private int _centreKey = int.MinValue;

    /// <summary>Fired (on a ThreadPool thread) when a preview bitmap has finished loading for a key.</summary>
    public event Action<int>? PreviewReady;

    /// <summary>Fired (on a ThreadPool thread) when an HQ bitmap has finished loading for a key.</summary>
    public event Action<int>? HqReady;

    /// <summary>Fired with the formatted "left/total &lt; Cache &gt; right/total" status string.</summary>
    public event Action<string>? CacheStatusChanged;

    /// <summary>
    /// Runs an action once the canvas has applied the renderer swaps already queued. A photo leaving the window
    /// may still be on screen until then (SetSource only queues its swap), so its bitmaps are disposed through
    /// this. Wired by the controller; when unset they are disposed at once.
    /// </summary>
    public Action<Action>? RunAfterQueuedRenderWork { get; set; }

    public PrefetchCache(Func<int, Photo?> getPhoto,
                         ICanvasResourceCreatorWithDpi device,
                         Func<bool> isBursting)
    {
        _getPhoto = getPhoto;
        _device = device;
        _isBursting = isBursting;
    }

    // -------------------------------------------------------------------------
    // Public API — the only door in
    // -------------------------------------------------------------------------

    /// <summary>Spawns the four worker threads. Not called for secondary instances.</summary>
    public void Start()
    {
        var previewThread = new Thread(() => PreviewCacheBuilderDoWork(_cts.Token))
            { IsBackground = true, Name = "PreviewCacheBuilder" };
        previewThread.Start();
        var heavyPreviewThread = new Thread(() => HeavyPreviewBuilderDoWork(_cts.Token))
            { IsBackground = true, Name = "HeavyPreviewBuilder" };
        heavyPreviewThread.Start();
        var hqThread = new Thread(() => HqImageCacheBuilderDoWork(_cts.Token))
            { IsBackground = true, Name = "HqCacheBuilder" };
        hqThread.Start();
        var diskThread = new Thread(() => PreviewDiskCacherDoWork(_cts.Token))
            { IsBackground = true, Name = "DiskCacheBuilder" };
        diskThread.Start();
    }

    /// <summary>
    /// Re-centre the window. Disposes photos that fall outside it and queues those inside it that are
    /// not already loaded or in flight. <paramref name="signalHqLoading"/> mirrors the old
    /// <c>if (triggerHqCaching) _hqTier.Signal.Set()</c> — it decides whether to kick the HQ worker;
    /// whether the worker then proceeds is decided separately by the burst predicate.
    /// </summary>
    public void MoveWindow(int centrePosition, IReadOnlyList<int> keys, bool signalHqLoading)
    {
        _windowKeys   = keys;
        _windowCentre = centrePosition;

        if (keys.Count == 0)
        {
            _hqTier.Done.Clear();
            _previewTier.Done.Clear();
            _heavyKeys.Clear();
            FireProgress();
            return;
        }
        if (centrePosition < 0)
        {
            Logger.Warn($"MoveWindow: centre {centrePosition} out of range. Skipping cache update.");
            return;
        }

        var centreKey = keys[centrePosition];
        if (centreKey != _centreKey)
        {
            _centreKey = centreKey;
            var left = _centreCts;
            _centreCts = new CancellationTokenSource();
            left.Cancel();
            ReleaseTiledHqExcept(centreKey);
        }

        var desiredHqKeys      = FindNeighborKeys(centrePosition, AppConfig.Settings.CacheSizeOneSideHqImages);
        var desiredPreviewKeys = FindNeighborKeys(centrePosition, AppConfig.Settings.CacheSizeOneSidePreviews);
        SyncCacheTier(desiredHqKeys,      _hqTier,      static p => p.DetachHq());
        SyncCacheTier(desiredPreviewKeys, _previewTier, static p => p.DetachPreview());
        FlushRetiring();

        FireProgress();
        _previewTier.Signal.Set();
        _heavyPreviewSignal.Set();
        if (signalHqLoading) _hqTier.Signal.Set();
    }

    /// <summary>Wake the HQ worker once navigation stops holding it back (a burst ended, a folder-switch quiet period passed).</summary>
    public void SignalHqLoading() => _hqTier.Signal.Set();

    /// <summary>Mark a key as already HQ-loaded so the cache never re-decodes it (e.g. the first photo).</summary>
    public void SeedHqLoaded(int key) => _hqTier.Done[key] = 0;

    /// <summary>Drop a deleted key from the tiers. Does not dispose the Photo — the controller owns that.</summary>
    public void Evict(int key)
    {
        _hqTier.Done.TryRemove(key, out _);
        _previewTier.Done.TryRemove(key, out _);
        _heavyKeys.TryRemove(key, out _);
    }

    public bool IsHqLoaded(int key)      => _hqTier.Done.ContainsKey(key);
    public bool IsPreviewLoaded(int key) => _previewTier.Done.ContainsKey(key);

    /// <summary>
    /// Drop every cached RAW-file HQ bitmap so the new decoder settings take effect, and bump the
    /// generation so any in-flight RAW decode is discarded on completion. Requeues the in-window keys
    /// via <see cref="MoveWindow"/>. Called on the UI/STA thread.
    /// </summary>
    public void InvalidateHqForRawFiles()
    {
        _hqGeneration++;

        foreach (int key in _hqTier.Done.Keys)
            if (_getPhoto(key) is { IsRaw: true } photo)
            {
                // The controller has queued the on-screen photo's step-down; disposed after it.
                if (photo.DetachHq() is { } hq) _retiring.Add(hq);
                _hqTier.Done.TryRemove(key, out _);
            }

        // Reuse the window sync to requeue the in-window keys we just dropped.
        MoveWindow(_windowCentre, _windowKeys, signalHqLoading: true);
        FlushRetiring(); // MoveWindow flushes too, unless it returned early
    }

    // -------------------------------------------------------------------------
    // Worker threads
    // -------------------------------------------------------------------------

    private void PreviewCacheBuilderDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_previewTier.Queue.IsEmpty)
                {
                    // Preview queue is empty — hand off to the disk-cache thread, which writes
                    // the just-loaded previews (Origin.Disk) to the SQLite thumbnail store.
                    _diskCacheSignal.Set();
                    WaitHandle.WaitAny([_previewTier.Signal, token.WaitHandle]);
                    if (token.IsCancellationRequested) break;
                    _diskCacheSignal.Reset();
                }

                if (!_previewTier.Queue.TryPop(out var item)) continue;

                // Claimed before the wait for a slot: a window move during the wait would otherwise see the key
                // neither queued nor in flight, queue it again, and two loads would race for the same photo.
                // Every exit of the load releases the claim. A key already claimed is being loaded; skip it.
                if (!_previewTier.InFlight.TryAdd(item, 0)) continue;

                _previewThrottler.Wait(token);

                _ = Task.Run(() => LoadPreviewAsync(item), token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
    }

    // The preview loop for the heavy lane: its own pending set and slots, and no hand-off to the disk-cache
    // thread (a heavy load wakes that thread itself when it finishes).
    private void HeavyPreviewBuilderDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_heavyPreviewPending.IsEmpty)
                {
                    WaitHandle.WaitAny([_heavyPreviewSignal, token.WaitHandle]);
                    continue;
                }

                // Slots first, pick second (unlike the preview loop): a heavy decode holds its slots for a second
                // or more, and picking only once they are free means the pick is nearest the centre as it is now,
                // not where it was before the user moved on. Heavy slot before preview slot, never the other way
                // round, and the light lane never waits for a heavy slot, so the two waits can't deadlock.
                _heavyPreviewThrottler.Wait(token);
                _previewThrottler.Wait(token);

                // None in the window: everything pending was left behind by a window move. The next move drops it.
                if (NextHeavyPreview() is not { } item)
                {
                    ReleaseHeavySlots();
                    WaitHandle.WaitAny([_heavyPreviewSignal, token.WaitHandle]);
                    continue;
                }

                // Claimed in the preview tier, so the key can't load in both lanes at once. Taken out of the
                // pending set before the claim; when the claim fails the key is already loading in some lane.
                if (!_heavyPreviewPending.TryRemove(item, out _) || !_previewTier.InFlight.TryAdd(item, 0))
                {
                    ReleaseHeavySlots();
                    continue;
                }

                _ = Task.Run(() => LoadPreviewAsync(item, heavy: true), token);
            }
        }
        // Expected on shutdown. Disposed too: Dispose may free the throttler between this thread's Wait and
        // Release, and an exception escaping a thread would end the process.
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }

    // A heavy load holds both slots; see _heavyPreviewThrottler.
    private void ReleaseHeavySlots()
    {
        _previewThrottler.Release();
        _heavyPreviewThrottler.Release();
    }

    // Heavy-lane worker thread: the pending key nearest the window centre, or null. Same snapshot discipline as
    // IsInDesiredHqWindow. Scans at most the preview window; pending keys outside it are never picked (the next
    // window sync drops them).
    private int? NextHeavyPreview()
    {
        if (_heavyPreviewPending.IsEmpty) return null;
        return KeyWindow.Nearest(_windowKeys, _windowCentre, AppConfig.Settings.CacheSizeOneSidePreviews,
            _heavyPreviewPending.ContainsKey);
    }

    private void HqImageCacheBuilderDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Re-checked after every wake-up: a signal sent while navigation is still holding HQ back
                // (a window move during a burst or a folder-switch quiet period) must not start a decode.
                // Neighbours also wait while the centre is loading, so they don't compete with the photo on
                // screen; every finished load wakes this thread. The centre is popped first because the window
                // sync and the requeue paths push it last, on top of the stack.
                while (_hqTier.Queue.IsEmpty || _isBursting() || NeighbourMustWait())
                {
                    WaitHandle.WaitAny([_hqTier.Signal, token.WaitHandle]);
                    token.ThrowIfCancellationRequested();
                }

                if (!_hqTier.Queue.TryPop(out var item)) continue;

                // Claimed here, before the wait for a slot, not inside the load: a window move during the wait
                // must not queue the key again (two loads would race for the same photo), and the next pass
                // already sees the centre as loading. A key already claimed is being loaded; skip it.
                if (!_hqTier.InFlight.TryAdd(item, 0)) continue;

                _hqThrottler.Wait(token);

                _ = Task.Run(() => LoadHqAsync(item), token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
    }

    private void PreviewDiskCacherDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                WaitHandle.WaitAny([_diskCacheSignal, token.WaitHandle]);
                if (token.IsCancellationRequested) break;

                if (_diskCacheQueue.IsEmpty)
                {
                    _diskCacheSignal.Reset();
                    continue;
                }

                if (!_diskCacheQueue.TryPop(out var key)) continue;

                _diskCacheThrottler.Wait(token);

                _ = Task.Run(() => DiskCachePreviewAsync(key), token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
    }

    // -------------------------------------------------------------------------
    // Per-key loaders
    // -------------------------------------------------------------------------

    // Every loader holds a use on its photo (Photo.TryBeginUse) for as long as it touches the photo's bitmaps,
    // so a photo that leaves the list mid-load is disposed by whichever of the owner and the load finishes last.

    // The preview worker (or, with heavy set, the heavy-lane worker) marks the key in flight before starting
    // this; every exit below clears it.
    private async Task LoadPreviewAsync(int key, bool heavy = false)
    {
        Photo? photo = null;
        try
        {
            if (_getPhoto(key) is not { } p || !p.TryBeginUse())
            {
                _previewTier.InFlight.Remove(key, out _);
                return;
            }
            photo = p;
            // Two passes: the light steps here at full parallelism, and only on their failure the heavy steps in
            // the heavy lane. The heavy pass doesn't repeat the light steps, so it starts its full decode on this
            // thread-pool thread rather than on the callback thread of a failed WIC thumbnail lookup.
            bool loaded = await photo.LoadPreview(_device, heavy ? Purpose.PreviewHeavy : Purpose.PreviewLight);
            if (DiscardIfRemoved(key, photo, _previewTier)) return;
            if (!loaded)
            {
                // Only a full-image decode can make this preview: hand the key to the heavy lane. Marked
                // heavy before the claim is released, so a window move in between queues it to the heavy lane
                // too, never back to the light queue. Out of the window by now: not queued; the mark sends it
                // to the heavy lane if it comes back.
                _heavyKeys[key] = 0;
                _previewTier.InFlight.Remove(key, out _);
                if (IsInPreviewWindow(key))
                {
                    _heavyPreviewPending[key] = 0;
                    _heavyPreviewSignal.Set();
                }
                return;
            }
            _previewTier.Done[key] = 0;
            _heavyKeys.TryRemove(key, out _);
            _previewTier.InFlight.Remove(key, out _);
            if (photo.Preview?.Origin == Origin.Disk)
            {
                _diskCacheQueue.Push(key);
                // The disk-cache thread is woken when the preview queue drains. The heavy lane runs on after that,
                // so its previews would stay unsaved until the next navigation; wake it, but only once the light
                // queue is empty too, so disk writes still don't compete with light loads.
                if (heavy && _previewTier.Queue.IsEmpty) _diskCacheSignal.Set();
            }
            PreviewReady?.Invoke(key);
            FireProgress();
        }
        catch (Exception ex)
        {
            // A key left in flight would never reload. Released here, not in finally: an exit above may have
            // released it already and the worker claimed it again for a new load, whose claim must survive.
            _previewTier.InFlight.Remove(key, out _);
            Logger.Error(ex, $"Preview load failed for key {key}");
        }
        finally
        {
            photo?.EndUse();
            if (heavy) ReleaseHeavySlots();
            else _previewThrottler.Release();
        }
    }

    // The HQ worker marks the key in flight before starting this; every exit below clears it.
    private async Task LoadHqAsync(int key)
    {
        Photo? photo = null;
        try
        {
            int gen = _hqGeneration;
            if (_getPhoto(key) is not { } p || !p.TryBeginUse())
            {
                _hqTier.InFlight.Remove(key, out _);
                return;
            }
            photo = p;
            var isCentre = IsWindowCentre(key);
            var loaded = await photo.LoadHq(_device, allowLarge: isCentre,
                isCentre ? _centreCts.Token : CancellationToken.None);
            if (DiscardIfRemoved(key, photo, _hqTier)) return;

            // A large image finished just as the user left it: don't keep gigabytes for a neighbour.
            if (loaded && photo.Hq is TiledHqDisplayItem && !IsWindowCentre(key))
            {
                photo.ReleaseTiledHq();
                loaded = false;
            }
            if (!loaded)
            {
                // Oversized neighbour deferred, or its decode cancelled. MoveWindow re-queues it when it becomes
                // the centre — unless that already happened while this load was in flight (MoveWindow skips
                // in-flight keys), so check again now. Removed before the push: a window move in between then
                // queues the key itself rather than skipping it as in flight and clearing this push.
                _hqTier.InFlight.Remove(key, out _);
                if (IsWindowCentre(key)) _hqTier.Queue.Push(key);
                return;
            }
            if (DiscardedStaleRawDecode(key, photo, gen)) return;
            _hqTier.Done[key] = 0;
            _hqTier.InFlight.Remove(key, out _);
            HqReady?.Invoke(key);
        }
        catch (Exception ex)
        {
            // A key left in flight would never reload, and as the centre it would hold back every neighbour.
            _hqTier.InFlight.Remove(key, out _);
            Logger.Error(ex, $"HQ load failed for key {key}");
        }
        finally
        {
            photo?.EndUse();
            _hqThrottler.Release();
            _hqTier.Signal.Set(); // wakes the HQ worker: a neighbour may be waiting, or a key was requeued
        }
    }

    /// <summary>
    /// The photo left the list while it was loading (deleted, or removed by a folder change/switch): record
    /// nothing for the key. Its bitmaps — including any this load just attached — are disposed when the photo
    /// is retired and its last use ends. Returns true when the load was discarded.
    /// </summary>
    private bool DiscardIfRemoved(int key, Photo photo, PhotoCacheTier tier)
    {
        if (ReferenceEquals(_getPhoto(key), photo)) return false;
        tier.InFlight.Remove(key, out _);
        return true;
    }

    /// <summary>
    /// If RAW decode settings changed while this RAW HQ decode was in flight, the bitmap used the old
    /// settings. Discards it (and requeues the key if still in the HQ window) and returns true, so the
    /// caller skips committing the stale result. Returns false for the normal case.
    /// </summary>
    private bool DiscardedStaleRawDecode(int key, Photo photo, int gen)
    {
        if (gen == _hqGeneration || !photo.IsRaw) return false;

        if (photo.DetachHq() is { } hq) DisposeAfterQueuedRenderWork([hq]);
        _hqTier.InFlight.Remove(key, out _);
        if (IsInDesiredHqWindow(key)) _hqTier.Queue.Push(key); // the caller's finally wakes the worker
        return true;
    }

    private async Task DiskCachePreviewAsync(int key)
    {
        try
        {
            if (_previewTier.Done.ContainsKey(key) &&
                _getPhoto(key) is { Preview.Origin: Origin.Disk } image && image.TryBeginUse())
            {
                try
                {
                    if (image.Preview is { } preview)
                    {
                        var (actualWidth, actualHeight) = image.GetSourcePixelSize();
                        await DiskCacherWithSqlite.Instance.PutInCache(image.FilePath, preview.Bitmap,
                            (int)Math.Round(actualWidth), (int)Math.Round(actualHeight), preview.Rotation);
                    }
                }
                finally { image.EndUse(); }
            }
        }
        finally { _diskCacheThrottler.Release(); }
    }

    // -------------------------------------------------------------------------
    // Window maintenance
    // -------------------------------------------------------------------------

    // Disposes the items it is given, never "the photo's current item": the photo may reload before this runs.
    // One pump job for the batch, so a far jump that drops hundreds of previews wakes the canvas once.
    private void DisposeAfterQueuedRenderWork(DisplayItem[] items)
    {
        if (RunAfterQueuedRenderWork is { } run) run(() => DisposeAll(items));
        else DisposeAll(items);

        static void DisposeAll(DisplayItem[] items)
        {
            foreach (var item in items) item.Dispose();
        }
    }

    // UI/STA thread: disposes what this window move detached, after the renderer swap it follows.
    private void FlushRetiring()
    {
        if (_retiring.Count == 0) return;
        var items = _retiring.ToArray();
        _retiring.Clear();
        DisposeAfterQueuedRenderWork(items);
    }

    private void SyncCacheTier(List<int> desiredKeys, PhotoCacheTier tier, Func<Photo, DisplayItem?> detach)
    {
        _syncKeysToKeep.Clear();
        _syncKeysToKeep.UnionWith(desiredKeys);

        foreach (int key in tier.Done.Keys)
            if (!_syncKeysToKeep.Contains(key))
            {
                if (_getPhoto(key) is { } photo && detach(photo) is { } item)
                    _retiring.Add(item);
                tier.Done.TryRemove(key, out _);
            }

        // Previews have two queues: keys already found to need a full-image decode go to the heavy lane.
        bool previews = ReferenceEquals(tier, _previewTier);
        tier.Queue.Clear();
        if (previews) _heavyPreviewPending.Clear();
        foreach (int key in desiredKeys)
            if (!tier.Done.ContainsKey(key) && !tier.InFlight.ContainsKey(key))
            {
                if (previews && _heavyKeys.ContainsKey(key)) _heavyPreviewPending[key] = 0;
                else tier.Queue.Push(key);
            }
    }

    // Same snapshot discipline as IsInDesiredHqWindow — called from a ThreadPool thread.
    private bool IsInPreviewWindow(int key)
    {
        var keys = _windowKeys;
        int centre = _windowCentre;
        if (centre < 0 || centre >= keys.Count) return false;
        return KeyWindow.IndexOf(keys, key, centre, AppConfig.Settings.CacheSizeOneSidePreviews) >= 0;
    }

    // Membership test for the HQ window. Called from a ThreadPool thread (the stale-RAW-decode discard
    // path), so it snapshots the copy-on-write key list and centre once and scans only that snapshot —
    // never re-reading the shared fields or indexing a list that may have been swapped underneath it.
    private bool IsInDesiredHqWindow(int key)
    {
        var keys = _windowKeys;
        int centre = _windowCentre;
        if (centre < 0 || centre >= keys.Count) return false;
        return KeyWindow.IndexOf(keys, key, centre, AppConfig.Settings.CacheSizeOneSideHqImages) >= 0;
    }

    // A tiled HQ holds gigabytes of CPU tiles, so only the centre photo keeps one; a neighbour reloads on
    // return. Runs on the UI/STA thread inside MoveWindow.
    private void ReleaseTiledHqExcept(int centreKey)
    {
        foreach (var key in _hqTier.Done.Keys)
            if (key != centreKey && _getPhoto(key) is { Hq: TiledHqDisplayItem } photo)
            {
                photo.ReleaseTiledHq();
                _hqTier.Done.TryRemove(key, out _);
            }
    }

    // Same snapshot discipline as IsInDesiredHqWindow — called from a ThreadPool thread.
    private bool IsWindowCentre(int key)
    {
        var keys = _windowKeys;
        int centre = _windowCentre;
        return centre >= 0 && centre < keys.Count && keys[centre] == key;
    }

    // HQ worker thread: the next queued key is a neighbour and the current centre is still loading.
    // Never holds the centre itself, and releases as soon as the centre moves (a new key) or its load ends
    // (every exit of LoadHqAsync clears the in-flight mark and wakes the worker). Neighbour decodes already
    // running are not stopped. A centre decode that never returns holds neighbours until the user moves on.
    // Racy by design: the queue or window can change between this peek and the pop, letting at most one
    // neighbour start early.
    private bool NeighbourMustWait()
    {
        if (!_hqTier.Queue.TryPeek(out var next)) return false;
        var keys = _windowKeys;
        int centre = _windowCentre;
        return centre >= 0 && centre < keys.Count && keys[centre] != next
               && _hqTier.InFlight.ContainsKey(keys[centre]);
    }

    private List<int> FindNeighborKeys(int currentPosition, int cacheSizeOneSide)
    {
        var keys = _windowKeys;
        var desiredKeys = new List<int>(cacheSizeOneSide * 2 + 1);
        for (int i = cacheSizeOneSide; i >= 1; i--)
        {
            int nextPos = currentPosition + i;
            if (nextPos < keys.Count)
                desiredKeys.Add(keys[nextPos]);

            int prevPos = currentPosition - i;
            if (prevPos >= 0)
                desiredKeys.Add(keys[prevPos]);
        }
        desiredKeys.Add(keys[currentPosition]);
        return desiredKeys;
    }

    private void FireProgress()
    {
        var keys = _windowKeys;
        int centre = _windowCentre;
        if (centre < 0 || centre >= keys.Count) return;
        int noOfFilesOnLeft = centre;
        int noOfFilesOnRight = keys.Count - 1 - centre;

        // Counted by position, not by key value (keys are not ordered). Previews are only kept inside the
        // window, so scanning it finds every cached one.
        var (lo, hi) = KeyWindow.Range(keys.Count, centre, AppConfig.Settings.CacheSizeOneSidePreviews);
        int noOfCachedItemsOnLeft = 0;
        int noOfCachedItemsOnRight = 0;
        for (int pos = lo; pos < centre; pos++)
            if (_previewTier.Done.ContainsKey(keys[pos])) noOfCachedItemsOnLeft++;
        for (int pos = centre + 1; pos <= hi; pos++)
            if (_previewTier.Done.ContainsKey(keys[pos])) noOfCachedItemsOnRight++;
        CacheStatusChanged?.Invoke($"{noOfCachedItemsOnLeft}/{noOfFilesOnLeft} < Cache > {noOfCachedItemsOnRight}/{noOfFilesOnRight}");
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _cts.Cancel();
        _centreCts.Cancel();
        _previewTier.Signal.Set();
        _heavyPreviewSignal.Set();
        _hqTier.Signal.Set();
        _diskCacheSignal.Set();

        _cts.Dispose();
        _previewThrottler.Dispose();
        _heavyPreviewThrottler.Dispose();
        _heavyPreviewSignal.Dispose();
        _hqThrottler.Dispose();
        _diskCacheThrottler.Dispose();

        _previewTier.Dispose();
        _hqTier.Dispose();
        _diskCacheSignal.Dispose();
    }
}
