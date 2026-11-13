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
using NLog;

namespace FlyPhotos.Display.Controllers;

/// <summary>How a <see cref="PrefetchCache"/> runs.</summary>
/// <param name="Time">The clock for the folder-switch quiet period.</param>
/// <param name="Limits">How many jobs of each kind may run at once.</param>
/// <param name="OwnThread">
/// True: the cache runs its own prefetch thread. False: nothing runs until someone calls
/// <see cref="PrefetchCache.Step"/> (tests, which step the cache by hand on a manual clock).
/// </param>
internal sealed record PrefetchOptions(TimeProvider Time, JobLimits Limits, bool OwnThread = true)
{
    public static PrefetchOptions Default => new(TimeProvider.System, JobLimits.ForCores(Environment.ProcessorCount));
}

/// <summary>
/// Owns all background photo loading: keeps the <b>window</b> of photos around the <b>window centre</b> loaded
/// (previews for many, HQ for a few), unloads what leaves it, and writes fresh previews to the disk cache.
/// See CONTEXT.md for the vocabulary.
/// <para>
/// Threading: one dedicated <b>prefetch thread</b> owns every bit of loading state (the window, running jobs, load
/// hints) and is the only writer of the photos' <see cref="PhotoImages"/> slots once started. Other threads talk to
/// it only through its inbox: the UI posts window moves and settings, and decodes post their results. After each
/// batch of messages the prefetch thread asks <see cref="PrefetchPlanner"/> what to do and does it (<see cref="Step"/>).
/// The UI thread never waits on any of this, and loading never waits on the UI thread.
/// </para>
/// <para>
/// The class does not own the photo list: it resolves keys to photos through a delegate, and is told where the
/// viewer is with every <see cref="MoveWindow"/>. It announces each stored image through <see cref="Loaded"/>; the
/// controller decides whether the screen should change. Decoding goes through <see cref="IPhotoDecoder"/>.
/// </para>
/// </summary>
internal sealed class PrefetchCache : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Fired on the prefetch thread when a preview or HQ was stored for a key. The photo may or may not be on
    /// screen — the controller checks. Not fired for the first photo (the controller loads that itself).
    /// </summary>
    public event Action<int>? Loaded;

    /// <summary>Fired on the prefetch thread with the formatted "left/total &lt; Cache &gt; right/total" status.</summary>
    public event Action<string>? CacheStatusChanged;

    private readonly Func<int, Photo?> _getPhoto;
    private readonly IPhotoDecoder _decoder;
    private readonly PrefetchOptions _options;

    // --- Inbox (any thread) ---
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private volatile bool _disposed;

    // --- Prefetch-thread state ---
    private sealed class Job(RunningJob spec, Photo photo)
    {
        public readonly RunningJob Spec = spec;
        public readonly Photo Photo = photo;
        public readonly CancellationTokenSource Cts = new();
        // Cancelled, or made stale by a RAW setting change: its result is released, not stored.
        public bool Abandoned;

        // The result, written by the decode before it hands the job back (the hand-off publishes them).
        public PreviewDisplayItem? Preview;
        public Thumbnail? Thumbnail;
        public HqDisplayItem? Hq;
        public bool Failed;

        // Who owns the result once the decode returns: the prefetch thread (Returned, through the inbox), or, once
        // the cache is disposed, whichever of the decode and Dispose comes second (Orphaned), which releases it.
        private const int Running = 0, Returned = 1, Orphaned = 2;
        private int _state = Running;

        /// <summary>The decode returned. False: the cache was disposed and the result has been released.</summary>
        public bool TryReturn()
        {
            if (Interlocked.Exchange(ref _state, Returned) != Orphaned) return true;
            ReleaseResult();
            return false;
        }

        /// <summary>The cache is gone (its thread stopped): releases a result already returned, or one still to come.</summary>
        public void Orphan()
        {
            Abandoned = true;
            Cts.Cancel();
            if (Interlocked.Exchange(ref _state, Orphaned) == Returned) ReleaseResult();
        }

        public void ReleaseResult()
        {
            Preview?.Release();
            Hq?.Release();
        }
    }

    private NavState _nav = NavState.None;
    private bool _holdHq;
    private TimeSpan _holdHqUntil;                     // on _options.Time; the folder-switch quiet period
    private readonly List<Job> _running = [];
    private readonly List<RunningJob> _runningSpecs = [];
    private readonly HashSet<int> _loaded = [];        // keys this cache has stored images for
    private readonly HashSet<int> _needsHeavy = [];    // light preview pass found nothing (until the preview loads)
    private readonly HashSet<int> _needsDiskWrite = []; // preview decoded from the file, not yet in the disk cache
    private readonly HashSet<(int Key, LoadKind Kind)> _parked = []; // failed or deferred; retried once it is the centre
    private readonly PrefetchPlan _plan = new();
    private (int CachedBefore, int Before, int CachedAfter, int After) _lastStatus = (-1, -1, -1, -1);

    public PrefetchCache(Func<int, Photo?> getPhoto, IPhotoDecoder decoder, PrefetchOptions? options = null)
    {
        _getPhoto = getPhoto;
        _decoder = decoder;
        _options = options ?? PrefetchOptions.Default;
    }

    /// <summary>The job limits the planner works to.</summary>
    public JobLimits Limits => _options.Limits;

    /// <summary>
    /// What the cache tracks, for tests: a key the list has forgotten must not linger in any of these. Read on the
    /// prefetch thread, or between <see cref="Step"/>s.
    /// </summary>
    internal (int Loaded, int NeedsHeavy, int NeedsDiskWrite, int Parked, int Running) Counts =>
        (_loaded.Count, _needsHeavy.Count, _needsDiskWrite.Count, _parked.Count, _running.Count);

    /// <summary>Whether messages are waiting for the next <see cref="Step"/>.</summary>
    internal bool HasMessages => !_inbox.IsEmpty;

    // Time on the cache's clock (only differences matter).
    private TimeSpan Now => _options.Time.GetElapsedTime(0);

    // -------------------------------------------------------------------------
    // Public API — any thread; every call only posts to the prefetch thread
    // -------------------------------------------------------------------------

    /// <summary>Starts loading around <paramref name="nav"/>. Not called for secondary instances.</summary>
    public void Start(NavState nav)
    {
        MoveWindow(nav);
        Post(() => _loaded.Add(nav.Key)); // the photo on screen was loaded by the controller (the first photo)
        if (!_options.OwnThread) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "PrefetchCache" };
        _thread.Start();
    }

    /// <summary>The viewer moved (navigation, or a list change): re-centre the window on <paramref name="nav"/>.</summary>
    public void MoveWindow(NavState nav) => Post(() =>
    {
        // The photo now on screen gets another try. Only it: a file that keeps failing is not decoded again on
        // every key press while it stays in the window.
        _parked.RemoveWhere(p => p.Key == nav.Key);
        _nav = nav;
    });

    /// <summary>Holds HQ loading for a burst, until <see cref="ResumeHq"/>. Previews keep loading.</summary>
    public void HoldHq() => Post(() => _holdHq = true);

    /// <summary>The burst ended: HQ loading goes on.</summary>
    public void ResumeHq() => Post(() => _holdHq = false);

    /// <summary>Holds HQ loading for <paramref name="ms"/> from now (skipping through folders).</summary>
    public void HoldHqFor(int ms)
    {
        var until = Now + TimeSpan.FromMilliseconds(ms);
        Post(() => { if (until > _holdHqUntil) _holdHqUntil = until; });
    }

    /// <summary>Photos removed from the list. Their images are released by <see cref="PhotoImages.Retire"/>.</summary>
    public void Forget(IReadOnlyList<int> keys) => Post(() =>
    {
        foreach (int key in keys)
        {
            _loaded.Remove(key);
            _needsHeavy.Remove(key);
            _needsDiskWrite.Remove(key);
            foreach (var kind in Enum.GetValues<LoadKind>()) _parked.Remove((key, kind));
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
        _parked.RemoveWhere(p => p.Kind == LoadKind.Hq);
    });

    /// <summary>A planning setting (a window size) changed, or the clock moved: plan again now.</summary>
    public void Replan() => Post(static () => { });

    private void Post(Action message)
    {
        _inbox.Enqueue(message);
        try { _wake.Set(); }
        catch (ObjectDisposedException) { } // shutting down
    }

    // -------------------------------------------------------------------------
    // The prefetch thread
    // -------------------------------------------------------------------------

    private void Run()
    {
        var handles = new WaitHandle[] { _wake, _stop.Token.WaitHandle };
        while (!_stop.IsCancellationRequested)
        {
            // One bad step must not stop loading for the rest of the session: log it and plan again next wake-up.
            try { Step(); }
            catch (Exception ex) { Logger.Error(ex, "Prefetch step failed"); }
            var wait = _holdHqUntil - Now;
            try { WaitHandle.WaitAny(handles, wait > TimeSpan.Zero ? (int)wait.TotalMilliseconds + 1 : Timeout.Infinite); }
            catch (ObjectDisposedException) { return; } // shutting down
        }
    }

    /// <summary>
    /// Applies every queued message, then plans once and carries the plan out. The prefetch thread's loop body; with
    /// <see cref="PrefetchOptions.OwnThread"/> false the caller runs it, never two at once.
    /// </summary>
    internal void Step()
    {
        if (_disposed)
        {
            _inbox.Clear(); // Dispose released whatever the queued results hold
            return;
        }
        while (_inbox.TryDequeue(out var message))
        {
            try { message(); }
            catch (Exception ex) { Logger.Error(ex); }
        }
        if (_nav.Position < 0) return;

        _runningSpecs.Clear();
        foreach (var job in _running) _runningSpecs.Add(job.Spec);
        bool holdHq = _holdHq || Now < _holdHqUntil;
        var settings = AppConfig.Settings;
        PrefetchPlanner.Make(_plan, _nav, _loaded, SlotOf, _runningSpecs, holdHq,
            settings.CacheSizeOneSidePreviews, settings.CacheSizeOneSideHqImages, _options.Limits);

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
        foreach (var spec in _plan.Start) StartJob(spec);

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
    // Jobs — decode off the prefetch thread, store on it
    // -------------------------------------------------------------------------

    private void StartJob(RunningJob spec)
    {
        if (_getPhoto(spec.Key) is not { } photo) return;
        var job = new Job(spec, photo);
        _running.Add(job);
        _ = RunJob(job);
    }

    // Starts on the prefetch thread; the decoder moves the work off it. The rest runs wherever the decode finished.
    private async Task RunJob(Job job)
    {
        var (spec, photo) = (job.Spec, job.Photo);
        try
        {
            switch (spec.Kind)
            {
                case LoadKind.PreviewLight or LoadKind.PreviewHeavy:
                    var purpose = spec.Kind == LoadKind.PreviewHeavy ? Purpose.PreviewHeavy : Purpose.PreviewLight;
                    if (await _decoder.LoadPreview(photo, purpose).ConfigureAwait(false) is { } loaded)
                        (job.Preview, job.Thumbnail) = loaded;
                    break;
                case LoadKind.Hq:
                    job.Hq = await _decoder.LoadHq(photo, spec.AllowLarge, job.Cts.Token).ConfigureAwait(false);
                    break;
                case LoadKind.DiskWrite:
                    await WriteToDiskCache(photo).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"{spec.Kind} load failed for {photo.FilePath}");
            job.Failed = true;
        }
        if (job.TryReturn()) Post(() => Complete(job));
    }

    // Prefetch thread. Stores what the job brought, unless the job was abandoned or its photo left the list
    // meanwhile — then it is released. A photo that left the window is unloaded by the next plan.
    private void Complete(Job job)
    {
        _running.Remove(job);
        int key = job.Spec.Key;
        if (!ReferenceEquals(_getPhoto(key), job.Photo))
        {
            job.ReleaseResult(); // removed meanwhile: nothing about it is kept
            return;
        }
        if (job.Failed)
        {
            // A cancelled large decode throws too: nothing to show for it. Otherwise the decode threw, which shows
            // the error screen, the same as a decoder that reports the failure.
            if (job.Abandoned)
            {
                job.ReleaseResult();
                return;
            }
            switch (job.Spec.Kind)
            {
                case LoadKind.DiskWrite: _needsDiskWrite.Remove(key); return; // not retried
                case LoadKind.Hq: job.Hq = _decoder.HqFailed(); break;
                default: job.Preview = _decoder.PreviewFailed(); break;
            }
        }
        bool keep = !job.Abandoned;
        switch (job.Spec.Kind)
        {
            case LoadKind.DiskWrite:
                _needsDiskWrite.Remove(key);
                break;
            case LoadKind.Hq:
                CompleteHq(job, keep);
                break;
            default:
                CompletePreview(job, keep);
                break;
        }
    }

    private void CompletePreview(Job job, bool keep)
    {
        int key = job.Spec.Key;
        if (job.Preview is not { } preview)
        {
            // Light pass: only a full-image decode can make this preview, so the planner hands it to the heavy
            // lane. Heavy pass: nothing found at all.
            if (!keep) return;
            if (job.Spec.Kind == LoadKind.PreviewLight) _needsHeavy.Add(key);
            else _parked.Add((key, job.Spec.Kind));
            return;
        }
        _needsHeavy.Remove(key);
        var images = job.Photo.Images;
        // Replaces nothing but an error placeholder (shared, never freed).
        if (!keep || images.Preview is { } existing && !existing.IsErrorOrUndefined())
        {
            preview.Release();
            return;
        }
        if (!images.TryAttachPreview(preview, job.Thumbnail)) return; // retired: released by TryAttachPreview
        _loaded.Add(key);
        if (preview.Origin == Origin.Disk) _needsDiskWrite.Add(key);
        Loaded?.Invoke(key);
    }

    private void CompleteHq(Job job, bool keep)
    {
        int key = job.Spec.Key;
        if (job.Hq is not { } hq)
        {
            // An oversized neighbour deferred, or a large decode cancelled. Retried when it is the centre.
            if (keep && key != _nav.Key) _parked.Add((key, LoadKind.Hq));
            return;
        }
        var images = job.Photo.Images;
        if (!keep || images.Hq != null || hq is TiledHqDisplayItem && key != _nav.Key)
        {
            hq.Release();
            return;
        }
        if (!images.TryAttachHq(hq)) return; // retired: released by TryAttachHq
        _loaded.Add(key);
        Loaded?.Invoke(key);
    }

    private Task WriteToDiskCache(Photo photo)
    {
        if (photo.Images.Preview is not { Origin: Origin.Disk } preview) return Task.CompletedTask;
        var (actualWidth, actualHeight) = photo.Images.GetSourcePixelSize();
        // The path is read now, as the job starts: a rename meanwhile files it under the name it was decoded from.
        return _decoder.PutPreviewInDiskCache(photo.FilePath, preview,
            (int)Math.Round(actualWidth), (int)Math.Round(actualHeight));
    }

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    // The planner counted the cached previews on its pass over the window; formatted only when something changed.
    private void ReportStatus()
    {
        int left = _nav.Position, right = _nav.Keys.Count - 1 - _nav.Position;
        var status = (_plan.CachedBefore, left, _plan.CachedAfter, right);
        if (status == _lastStatus) return;
        _lastStatus = status;
        CacheStatusChanged?.Invoke($"{_plan.CachedBefore}/{left} < Cache > {_plan.CachedAfter}/{right}");
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stops loading. Every decode still running, or returned but not yet stored, has its result released whenever
    /// it arrives. Images already stored stay with their photos (retired by the controller).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_thread == null || _thread.Join(2000))
        {
            // The prefetch thread is gone: nothing else reads the running list now.
            foreach (var job in _running) job.Orphan();
            _running.Clear();
        }
        else Logger.Warn("The prefetch thread did not stop; results of its running decodes may not be released");
        _wake.Dispose();
    }
}
