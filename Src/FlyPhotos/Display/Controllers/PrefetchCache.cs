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
/// Owns all background photo loading: keeps the <b>window</b> of photos around the <b>window centre</b> loaded
/// (previews for many, HQ for a few), unloads what leaves it, and writes fresh previews to the disk cache.
/// See CONTEXT.md for the vocabulary.
/// <para>
/// Threading: one dedicated <b>loader thread</b> owns every bit of loading state (the window, running jobs, load
/// hints) and is the only writer of the photos' <see cref="PhotoImages"/> slots once started. Other threads talk to
/// it only through its inbox: the UI posts window moves and settings, and decodes running on the thread pool post
/// their results. After each batch of messages the loader asks <see cref="PrefetchPlanner"/> what to do and does it.
/// The UI thread never waits on any of this, and loading never waits on the UI thread.
/// </para>
/// <para>
/// The class does not own the photo list: it resolves keys to photos through a delegate, and is told where the
/// viewer is with every <see cref="MoveWindow"/>. It announces each stored image through <see cref="Loaded"/>; the
/// controller decides whether the screen should change.
/// </para>
/// </summary>
internal sealed class PrefetchCache : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Fired on the loader thread when a preview or HQ was stored for a key. The photo may or may not be on
    /// screen — the controller checks. Not fired for the first photo (the controller loads that itself).
    /// </summary>
    public event Action<int>? Loaded;

    /// <summary>Fired on the loader thread with the formatted "left/total &lt; Cache &gt; right/total" status.</summary>
    public event Action<string>? CacheStatusChanged;

    private readonly Func<int, Photo?> _getPhoto;
    private readonly ICanvasResourceCreatorWithDpi _device;

    // --- Inbox (any thread) ---
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    // --- Loader-thread state ---
    private sealed class Job(int key, LoadKind kind, bool allowLarge, Photo photo)
    {
        public readonly RunningJob Spec = new(key, kind, allowLarge);
        public readonly Photo Photo = photo;
        public readonly CancellationTokenSource Cts = new();
        // Cancelled, or made stale by a RAW setting change: its result is released, not stored.
        public bool Abandoned;
    }

    private NavState _nav = NavState.None;
    private bool _holdHq;
    private long _holdHqUntil;                         // Environment.TickCount64; the folder-switch quiet period
    private readonly List<Job> _running = [];
    private readonly List<RunningJob> _runningSpecs = [];
    private readonly HashSet<int> _loaded = [];        // keys this cache has stored images for
    private readonly HashSet<int> _needsHeavy = [];    // light preview pass found nothing (until the preview loads)
    private readonly HashSet<int> _needsDiskWrite = []; // preview decoded from the file, not yet in the disk cache
    private readonly HashSet<(int, LoadKind)> _parked = []; // failed or deferred; retried when the centre moves
    private readonly PrefetchPlan _plan = new();
    private readonly JobLimits _limits = JobLimits.ForCores(Environment.ProcessorCount);
    private string? _lastStatus;

    public PrefetchCache(Func<int, Photo?> getPhoto, ICanvasResourceCreatorWithDpi device)
    {
        _getPhoto = getPhoto;
        _device = device;
    }

    // -------------------------------------------------------------------------
    // Public API — any thread; every call only posts to the loader thread
    // -------------------------------------------------------------------------

    /// <summary>Starts the loader thread around <paramref name="nav"/>. Not called for secondary instances.</summary>
    public void Start(NavState nav)
    {
        MoveWindow(nav);
        _thread = new Thread(Run) { IsBackground = true, Name = "PrefetchLoader" };
        _thread.Start();
    }

    /// <summary>The viewer moved (navigation, or a list change): re-centre the window on <paramref name="nav"/>.</summary>
    public void MoveWindow(NavState nav) => Post(() =>
    {
        if (nav.Key != _nav.Key) _parked.Clear(); // a new centre gets another try at everything
        _nav = nav;
        if (nav.Position >= 0) _loaded.Add(nav.Key); // the first photo was loaded by the controller
    });

    /// <summary>Holds HQ loading (a burst) or lets it go (the burst ended). Previews keep loading either way.</summary>
    public void HoldHq(bool hold) => Post(() => _holdHq = hold);

    /// <summary>Holds HQ loading for <paramref name="ms"/> from now (skipping through folders).</summary>
    public void HoldHqFor(int ms)
    {
        long until = Environment.TickCount64 + ms;
        Post(() => _holdHqUntil = Math.Max(_holdHqUntil, until));
    }

    /// <summary>Photos removed from the list. Their images are released by <see cref="PhotoImages.Retire"/>.</summary>
    public void Forget(IReadOnlyList<int> keys) => Post(() =>
    {
        foreach (int key in keys)
        {
            _loaded.Remove(key);
            _needsHeavy.Remove(key);
            _needsDiskWrite.Remove(key);
        }
    });

    /// <summary>
    /// RAW decode settings changed: drops every RAW HQ so the new settings take effect, and discards RAW HQ decodes
    /// still running. The window then reloads them. The canvas keeps its own lease on the one on screen until the
    /// new one replaces it.
    /// </summary>
    public void InvalidateHqForRawFiles() => Post(() =>
    {
        foreach (int key in _loaded)
            if (_getPhoto(key) is { IsRaw: true } photo)
                photo.Images.DetachHq()?.Release();
        foreach (var job in _running)
            if (job.Spec.Kind == LoadKind.Hq && job.Photo.IsRaw)
                Abandon(job);
        _parked.RemoveWhere(p => p.Item2 == LoadKind.Hq);
    });

    /// <summary>A planning setting (window sizes) changed.</summary>
    public void Replan() => Post(static () => { });

    private void Post(Action message)
    {
        _inbox.Enqueue(message);
        try { _wake.Set(); }
        catch (ObjectDisposedException) { } // shutting down
    }

    // -------------------------------------------------------------------------
    // The loader thread
    // -------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            var handles = new WaitHandle[] { _wake, _stop.Token.WaitHandle };
            while (!_stop.IsCancellationRequested)
            {
                Step();
                long wait = _holdHqUntil - Environment.TickCount64;
                WaitHandle.WaitAny(handles, wait > 0 ? (int)wait + 1 : Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Prefetch loader stopped");
        }
    }

    /// <summary>Applies every queued message, then plans once and carries the plan out.</summary>
    private void Step()
    {
        while (_inbox.TryDequeue(out var message))
        {
            try { message(); }
            catch (Exception ex) { Logger.Error(ex); }
        }
        if (_nav.Position < 0) return;

        _runningSpecs.Clear();
        foreach (var job in _running) _runningSpecs.Add(job.Spec);
        bool holdHq = _holdHq || Environment.TickCount64 < _holdHqUntil;
        var settings = AppConfig.Settings;
        PrefetchPlanner.Make(_plan, _nav.Keys, _nav.Position, _loaded, SlotOf, _runningSpecs, holdHq,
            settings.CacheSizeOneSidePreviews, settings.CacheSizeOneSideHqImages, _limits);

        foreach (int i in _plan.Cancel) Abandon(_running[i]);
        foreach (int key in _plan.EvictAll)
        {
            if (_getPhoto(key) is { } photo)
            {
                photo.Images.DetachHq()?.Release(); // the canvas leases what is on screen
                photo.Images.DetachPreview()?.Release();
            }
            _loaded.Remove(key);
            _needsDiskWrite.Remove(key);
        }
        foreach (int key in _plan.EvictHq)
            _getPhoto(key)?.Images.DetachHq()?.Release();
        foreach (var (key, kind, allowLarge) in _plan.Start)
            StartJob(key, kind, allowLarge);

        ReportStatus();
    }

    private SlotState SlotOf(int key)
    {
        var images = _getPhoto(key)?.Images;
        return new SlotState(
            HasPreview: images?.Preview != null,
            HasHq: images?.Hq != null,
            TiledHq: images?.Hq is TiledHqDisplayItem,
            NeedsHeavy: _needsHeavy.Contains(key),
            NeedsDiskWrite: _needsDiskWrite.Contains(key),
            PreviewParked: _parked.Contains((key, LoadKind.PreviewLight)) || _parked.Contains((key, LoadKind.PreviewHeavy)),
            HqParked: _parked.Contains((key, LoadKind.Hq)));
    }

    private static void Abandon(Job job)
    {
        if (job.Abandoned) return;
        job.Abandoned = true;
        job.Cts.Cancel(); // only the large-image decode observes it; the others finish and are released
    }

    // -------------------------------------------------------------------------
    // Jobs — decode on the thread pool, store on the loader thread
    // -------------------------------------------------------------------------

    private void StartJob(int key, LoadKind kind, bool allowLarge)
    {
        if (_getPhoto(key) is not { } photo) return;
        var job = new Job(key, kind, allowLarge, photo);
        _running.Add(job);
        _ = Task.Run(async () =>
        {
            object? result = null;
            try
            {
                result = kind switch
                {
                    LoadKind.PreviewLight => await PhotoLoader.LoadPreview(photo, _device, Purpose.PreviewLight),
                    LoadKind.PreviewHeavy => await PhotoLoader.LoadPreview(photo, _device, Purpose.PreviewHeavy),
                    LoadKind.Hq => await PhotoLoader.LoadHq(photo, _device, allowLarge, job.Cts.Token),
                    _ => await WriteToDiskCache(photo)
                };
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"{kind} load failed for {photo.FilePath}");
                result = ex;
            }
            if (_stop.IsCancellationRequested) ReleaseResult(result);
            else Post(() => Complete(job, result));
        });
    }

    // Loader thread. Stores what the job brought, unless the job was abandoned or its photo left the list or the
    // window meanwhile — then it is released.
    private void Complete(Job job, object? result)
    {
        _running.Remove(job);
        int key = job.Spec.Key;
        bool keep = !job.Abandoned && ReferenceEquals(_getPhoto(key), job.Photo);
        var images = job.Photo.Images;

        switch (result)
        {
            case Exception:
                _parked.Add((key, job.Spec.Kind));
                return;

            case ValueTuple<PreviewDisplayItem, Thumbnail> (var preview, var thumbnail):
                _needsHeavy.Remove(key);
                // Replaces nothing but an error placeholder (shared, never freed).
                if (!keep || images.Preview is { } existing && !existing.IsErrorOrUndefined())
                {
                    preview.Release();
                    return;
                }
                if (!images.TryAttachPreview(preview, thumbnail)) return; // retired: released by TryAttachPreview
                _loaded.Add(key);
                if (preview.Origin == Origin.Disk) _needsDiskWrite.Add(key);
                Loaded?.Invoke(key);
                return;

            case HqDisplayItem hq:
                if (!keep || images.Hq != null || hq is TiledHqDisplayItem && key != _nav.Key)
                {
                    hq.Release();
                    return;
                }
                if (!images.TryAttachHq(hq)) return; // retired: released by TryAttachHq
                _loaded.Add(key);
                Loaded?.Invoke(key);
                return;

            case bool: // a disk-cache write finished
                _needsDiskWrite.Remove(key);
                return;

            case null when job.Spec.Kind == LoadKind.PreviewLight:
                // Only a full-image decode can make this preview: the planner hands it to the heavy lane.
                _needsHeavy.Add(key);
                return;

            case null when job.Spec.Kind == LoadKind.Hq:
                // An oversized neighbour deferred, or a large decode cancelled. Retried when it is the centre.
                if (!job.Abandoned && key != _nav.Key) _parked.Add((key, LoadKind.Hq));
                return;

            case null:
                _parked.Add((key, job.Spec.Kind)); // a heavy pass that found nothing
                return;
        }
    }

    private static void ReleaseResult(object? result)
    {
        switch (result)
        {
            case ValueTuple<PreviewDisplayItem, Thumbnail> (var preview, _): preview.Release(); break;
            case HqDisplayItem hq: hq.Release(); break;
        }
    }

    private static async Task<bool> WriteToDiskCache(Photo photo)
    {
        if (photo.Images.Preview is not { Origin: Origin.Disk } preview || !preview.TryAcquire()) return true;
        try
        {
            var (actualWidth, actualHeight) = photo.Images.GetSourcePixelSize();
            await DiskCacherWithSqlite.Instance.PutInCache(photo.FilePath, preview.Bitmap,
                (int)Math.Round(actualWidth), (int)Math.Round(actualHeight), preview.Rotation);
        }
        finally { preview.Release(); }
        return true;
    }

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    private void ReportStatus()
    {
        var keys = _nav.Keys;
        int centre = _nav.Position;
        int left = centre, right = keys.Count - 1 - centre;

        // Counted by position, not by key value (keys are not ordered). Previews are only kept inside the window.
        var (lo, hi) = KeyWindow.Range(keys.Count, centre, AppConfig.Settings.CacheSizeOneSidePreviews);
        int cachedLeft = 0, cachedRight = 0;
        for (int pos = lo; pos < centre; pos++)
            if (_getPhoto(keys[pos])?.Images.Preview != null) cachedLeft++;
        for (int pos = centre + 1; pos <= hi; pos++)
            if (_getPhoto(keys[pos])?.Images.Preview != null) cachedRight++;
        var status = $"{cachedLeft}/{left} < Cache > {cachedRight}/{right}";
        if (status == _lastStatus) return;
        _lastStatus = status;
        CacheStatusChanged?.Invoke(status);
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(2000);
        // The loader is gone: nothing reads these now. Results still on their way are released by their tasks.
        foreach (var job in _running) job.Cts.Cancel();
        _wake.Dispose();
    }
}
