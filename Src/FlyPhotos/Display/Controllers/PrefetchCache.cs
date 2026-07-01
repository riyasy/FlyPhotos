#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Owns all background photo loading: the preview, HQ and disk <b>tiers</b> (each a worker thread
/// with its own queue, in-flight set and completed-keys set), throttling, the sliding <b>window</b>
/// of neighbours kept loaded around the <b>window centre</b>, and disk write-back.
///
/// Nothing outside this class ever sees a tier. Callers drive it with <see cref="MoveWindow"/> when
/// navigation moves and ask it questions with <see cref="IsHqLoaded"/> / <see cref="IsPreviewLoaded"/>;
/// it announces completions through the ready events. See CONTEXT.md for the vocabulary.
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
    private readonly SemaphoreSlim _previewThrottler   = new(Environment.ProcessorCount);
    private readonly SemaphoreSlim _hqThrottler        = new(Environment.ProcessorCount);
    private readonly SemaphoreSlim _diskCacheThrottler = new(Environment.ProcessorCount);

    private readonly PhotoCacheTier       _previewTier     = new();
    private readonly PhotoCacheTier       _hqTier          = new();
    private readonly ConcurrentStack<int> _diskCacheQueue  = new();
    private readonly ManualResetEvent     _diskCacheSignal = new(false);

    // Reused across SyncCacheTier calls — only touched on the UI/STA thread (inside MoveWindow).
    private readonly HashSet<int> _syncKeysToKeep = new();

    // Bumped (on the UI/STA thread) whenever RAW decode settings change so any RAW HQ decode that was
    // already in flight is discarded on completion instead of committing a stale bitmap.
    private volatile int _hqGeneration;

    // Resolves a key to its Photo (or null). Read-only access into the controller's PhotoList; the
    // controller owns the collection and disposal timing.
    private readonly Func<int, Photo?> _getPhoto;
    private readonly ICanvasResourceCreatorWithDpi _device;

    // True while the user is holding a navigation key. The HQ worker re-parks while this is set so it
    // doesn't decode photos being skipped past. Supplied by the controller (IsContinuousKeyPress).
    private readonly Func<bool> _isBursting;

    // Latest window pushed in by MoveWindow. Replaces the old reach-ins to PhotoSessionState +
    // _sortedPhotoKeys. _windowKeys is the full sorted key list; _windowCentre is the position in it.
    private volatile IReadOnlyList<int> _windowKeys = Array.Empty<int>();
    private volatile int _windowCentre;

    /// <summary>Fired (on a ThreadPool thread) when a preview bitmap has finished loading for a key.</summary>
    public event Action<int>? PreviewReady;

    /// <summary>Fired (on a ThreadPool thread) when an HQ bitmap has finished loading for a key.</summary>
    public event Action<int>? HqReady;

    /// <summary>Fired with the formatted "left/total &lt; Cache &gt; right/total" status string.</summary>
    public event Action<string>? CacheStatusChanged;

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

    /// <summary>Spawns the three worker threads. Not called for secondary instances.</summary>
    public void Start()
    {
        var previewThread = new Thread(() => PreviewCacheBuilderDoWork(_cts.Token))
            { IsBackground = true, Name = "PreviewCacheBuilder" };
        previewThread.Start();
        var hqThread = new Thread(() => HqImageCacheBuilderDoWork(_cts.Token))
            { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "HqCacheBuilder" };
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
            FireProgress();
            return;
        }
        if (centrePosition < 0)
        {
            Logger.Warn($"MoveWindow: centre {centrePosition} out of range. Skipping cache update.");
            return;
        }

        var desiredHqKeys      = FindNeighborKeys(centrePosition, AppConfig.Settings.CacheSizeOneSideHqImages);
        var desiredPreviewKeys = FindNeighborKeys(centrePosition, AppConfig.Settings.CacheSizeOneSidePreviews);
        SyncCacheTier(desiredHqKeys,      _hqTier,      p => p.DisposeHqOnly());
        SyncCacheTier(desiredPreviewKeys, _previewTier, p => p.DisposePreviewOnly());

        FireProgress();
        _previewTier.Signal.Set();
        if (signalHqLoading) _hqTier.Signal.Set();
    }

    /// <summary>Wake the HQ worker (used by Brake once a burst has ended).</summary>
    public void SignalHqLoading() => _hqTier.Signal.Set();

    /// <summary>Mark a key as already HQ-loaded so the cache never re-decodes it (e.g. the first photo).</summary>
    public void SeedHqLoaded(int key) => _hqTier.Done[key] = 0;

    /// <summary>Drop a deleted key from the tiers. Does not dispose the Photo — the controller owns that.</summary>
    public void Evict(int key)
    {
        _hqTier.Done.TryRemove(key, out _);
        _previewTier.Done.TryRemove(key, out _);
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
                photo.DisposeHqOnly();
                _hqTier.Done.TryRemove(key, out _);
            }

        // Reuse the window sync to requeue the in-window keys we just dropped.
        MoveWindow(_windowCentre, _windowKeys, signalHqLoading: true);
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

                _previewThrottler.Wait(token);

                _ = Task.Run(() => LoadPreviewAsync(item), token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
    }

    private void HqImageCacheBuilderDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_hqTier.Queue.IsEmpty || _isBursting())
                {
                    WaitHandle.WaitAny([_hqTier.Signal, token.WaitHandle]);
                    if (token.IsCancellationRequested) break;
                }

                if (!_hqTier.Queue.TryPop(out var item)) continue;

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

    private async Task LoadPreviewAsync(int key)
    {
        try
        {
            _previewTier.InFlight[key] = 0;
            if (_getPhoto(key) is not { } photo) return;
            await photo.LoadPreview(_device);
            _previewTier.Done[key] = 0;
            _previewTier.InFlight.Remove(key, out _);
            if (photo.Preview?.Origin == Origin.Disk) _diskCacheQueue.Push(key);
            PreviewReady?.Invoke(key);
            FireProgress();
        }
        finally { _previewThrottler.Release(); }
    }

    private async Task LoadHqAsync(int key)
    {
        try
        {
            _hqTier.InFlight[key] = 0;
            int gen = _hqGeneration;
            if (_getPhoto(key) is not { } photo) return;
            await photo.LoadHq(_device);
            if (DiscardedStaleRawDecode(key, photo, gen)) return;
            _hqTier.Done[key] = 0;
            _hqTier.InFlight.Remove(key, out _);
            HqReady?.Invoke(key);
        }
        finally { _hqThrottler.Release(); }
    }

    /// <summary>
    /// If RAW decode settings changed while this RAW HQ decode was in flight, the bitmap used the old
    /// settings. Discards it (and requeues the key if still in the HQ window) and returns true, so the
    /// caller skips committing the stale result. Returns false for the normal case.
    /// </summary>
    private bool DiscardedStaleRawDecode(int key, Photo photo, int gen)
    {
        if (gen == _hqGeneration || !photo.IsRaw) return false;

        photo.DisposeHqOnly();
        _hqTier.InFlight.Remove(key, out _);
        if (IsInDesiredHqWindow(key))
        {
            _hqTier.Queue.Push(key);
            _hqTier.Signal.Set();
        }
        return true;
    }

    private async Task DiskCachePreviewAsync(int key)
    {
        try
        {
            if (_previewTier.Done.ContainsKey(key) &&
                _getPhoto(key) is { } image &&
                image.Preview?.Origin == Origin.Disk)
            {
                var (actualWidth, actualHeight) = image.GetActualSize();
                await DiskCacherWithSqlite.Instance.PutInCache(image.FilePath, image.Preview.Bitmap,
                    (int)Math.Round(actualWidth), (int)Math.Round(actualHeight), image.Preview.Rotation);
            }
        }
        finally { _diskCacheThrottler.Release(); }
    }

    // -------------------------------------------------------------------------
    // Window maintenance
    // -------------------------------------------------------------------------

    private void SyncCacheTier(List<int> desiredKeys, PhotoCacheTier tier, Action<Photo> disposeAction)
    {
        _syncKeysToKeep.Clear();
        _syncKeysToKeep.UnionWith(desiredKeys);

        foreach (int key in tier.Done.Keys)
            if (!_syncKeysToKeep.Contains(key))
            {
                if (_getPhoto(key) is { } photo)
                    disposeAction(photo);
                tier.Done.TryRemove(key, out _);
            }

        tier.Queue.Clear();
        foreach (int key in desiredKeys)
            if (!tier.Done.ContainsKey(key) && !tier.InFlight.ContainsKey(key))
                tier.Queue.Push(key);
    }

    // Membership test for the HQ window. Called from a ThreadPool thread (the stale-RAW-decode discard
    // path), so it snapshots the copy-on-write key list and centre once and scans only that snapshot —
    // never re-reading the shared fields or indexing a list that may have been swapped underneath it.
    private bool IsInDesiredHqWindow(int key)
    {
        var keys = _windowKeys;
        int centre = _windowCentre;
        if (centre < 0 || centre >= keys.Count) return false;
        int side = AppConfig.Settings.CacheSizeOneSideHqImages;
        int lo = Math.Max(0, centre - side);
        int hi = Math.Min(keys.Count - 1, centre + side);
        for (int pos = lo; pos <= hi; pos++)
            if (keys[pos] == key) return true;
        return false;
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
        if (centre < 0 || keys.Count == 0) return;
        int currentKey = keys[centre];
        int noOfFilesOnLeft = centre;
        int noOfFilesOnRight = keys.Count - 1 - centre;
        int noOfCachedItemsOnLeft = 0;
        int noOfCachedItemsOnRight = 0;
        foreach (int key in _previewTier.Done.Keys)
        {
            if (key < currentKey) noOfCachedItemsOnLeft++;
            else if (key > currentKey) noOfCachedItemsOnRight++;
        }
        CacheStatusChanged?.Invoke($"{noOfCachedItemsOnLeft}/{noOfFilesOnLeft} < Cache > {noOfCachedItemsOnRight}/{noOfFilesOnRight}");
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _cts.Cancel();
        _previewTier.Signal.Set();
        _hqTier.Signal.Set();
        _diskCacheSignal.Set();

        _cts.Dispose();
        _previewThrottler.Dispose();
        _hqThrottler.Dispose();
        _diskCacheThrottler.Dispose();

        _previewTier.Dispose();
        _hqTier.Dispose();
        _diskCacheSignal.Dispose();
    }
}
