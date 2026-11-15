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
/// <param name="Time">
/// The clock for the folder-switch quiet period (<see cref="PrefetchCache.HoldHqFor"/>). The app uses the system
/// clock; tests pass a manual one so they can move time on by hand.
/// </param>
/// <param name="Limits">How many jobs of each kind may run at once.</param>
/// <param name="OwnThread">
/// True: the cache runs its own prefetch thread. False: nothing runs until someone calls
/// <see cref="PrefetchCache.Step"/> (tests, which step the cache by hand on a manual clock).
/// </param>
internal sealed record PrefetchOptions(TimeProvider Time, JobLimits Limits, bool OwnThread = true)
{
    /// <summary>The app's settings: the system clock, limits sized to this machine's cores, its own thread.</summary>
    public static PrefetchOptions Default => new(TimeProvider.System, JobLimits.ForCores(Environment.ProcessorCount));
}

/// <summary>
/// Owns all background photo loading: keeps the <b>window</b> of photos around the <b>window centre</b> loaded
/// (previews for many, HQ for a few), unloads what leaves it, and writes fresh previews to the disk cache.
/// See CONTEXT.md for the vocabulary.
/// <para>
/// <b>Threading.</b> One dedicated <b>prefetch thread</b> owns every bit of loading state (the window, running jobs,
/// load hints) and is the only writer of the photos' <see cref="DisplaySlots"/> once started. Other threads talk to it
/// only through its inbox (<see cref="Post"/>): the UI thread posts window moves and settings, and decodes post their
/// results. So none of the fields under "Prefetch-thread state" below needs a lock: only the prefetch thread reads or
/// writes them. The UI thread never waits on any of this, and loading never waits on the UI thread.
/// </para>
/// <para>
/// <b>One round</b> (<see cref="Step"/>): run every queued message, ask <see cref="PrefetchPlanner"/> what to do
/// (a pure function of the window, what is loaded and what is running), then carry out its plan: cancel jobs, unload
/// photos, start jobs. The thread then sleeps until the next message.
/// </para>
/// <para>
/// <b>A job's life</b>:
/// <list type="number">
///   <item><see cref="StartJob"/> (prefetch thread) adds it to <see cref="_running"/> and starts the decode, which the
///     decoder moves to the thread pool at once.</item>
///   <item>The decode finishes on a thread-pool thread and writes its result into the job (<see cref="RunJob"/>).</item>
///   <item>It posts <see cref="Complete"/> back to the inbox. The prefetch thread removes the job from
///     <see cref="_running"/> and stores the result in the photo's slot, or releases it if it is no longer
///     wanted.</item>
/// </list>
/// A job the planner cancels (its photo left the window) is <i>abandoned</i>: it stays in <see cref="_running"/>,
/// and keeps counting against the job limits, until its decode returns. Only the tiled large-image decode stops
/// early on cancellation; any other decode runs to the end and its result is then released.
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

    // --- Set once by the constructor; read on any thread ---

    // The photo list's lookup (PhotoList.GetPhoto): lock-free, any thread. Null once the list has forgotten the key
    // (the photo was removed), which is how a job notices its photo is gone.
    private readonly Func<int, Photo?> _getPhoto;
    // Does every load: previews, HQs and disk-cache writes. Each call returns at once and decodes on the thread pool.
    private readonly IPhotoDecoder _decoder;
    // The clock, the job limits, and whether the cache runs its own thread.
    private readonly PrefetchOptions _options;

    // --- Inbox (any thread) ---

    // Messages for the prefetch thread, run in the order posted at the start of the next Step. Every change to the
    // prefetch-thread state below arrives this way, which is what keeps that state single-threaded.
    private readonly ConcurrentQueue<Action> _inbox = new();
    // Set by every Post: wakes the prefetch thread from its sleep in Run.
    private readonly AutoResetEvent _wake = new(false);
    // Cancelled by Dispose: Run's loop ends and the thread exits.
    private readonly CancellationTokenSource _stop = new();
    // The prefetch thread; null before Start, and always null in stepped mode (tests call Step themselves).
    private Thread? _thread;
    // Set by Dispose (UI thread), read by Step: once set, queued messages are dropped instead of run.
    private volatile bool _disposed;

    // --- Prefetch-thread state: read and written only on the prefetch thread (or by a test between Steps) ---

    /// <summary>
    /// One load in flight: what it is, for which photo, and what it brought back. Created by <see cref="StartJob"/>
    /// on the prefetch thread; its decode fills in the result on a thread-pool thread; <see cref="Complete"/> reads it
    /// back on the prefetch thread.
    /// </summary>
    private sealed class Job(RunningJob spec, Photo photo)
    {
        // The key and kind of load, as the planner asked for it. Also what the planner sees of this job next round.
        public readonly RunningJob Spec = spec;
        // The photo object it loads for. A photo removed (and its key forgotten) meanwhile no longer matches what
        // _getPhoto returns for the key, so Complete releases the result instead of storing it.
        public readonly Photo Photo = photo;
        // Cancelled when the job is abandoned or the cache is disposed. Only the tiled large-image decode observes it.
        public readonly CancellationTokenSource Cts = new();
        // Cancelled, or made stale by a RAW setting change: its result is released, not stored. Prefetch thread only,
        // except Orphan, which runs after that thread has stopped.
        public bool Abandoned;

        // The result, written by the decode before it hands the job back. The hand-off (TryReturn's interlocked
        // exchange, then the inbox) publishes these to whichever thread reads them next.
        public PreviewDisplayItem? Preview; // a preview load's image (holds the creator's lease)
        public Thumbnail? Thumbnail;        // the strip's pixels made from that preview
        public HqDisplayItem? Hq;           // an HQ load's image (holds the creator's lease)
        public bool Failed;                 // the decode threw

        // Who owns the result once the decode returns. While the cache lives: the prefetch thread (Returned, through
        // the inbox). Once the cache is disposed: whichever of the decode and Dispose comes second (Orphaned), which
        // releases it. The exchange in TryReturn and Orphan makes sure exactly one side does.
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

        /// <summary>Releases whatever the decode brought (nothing is stored).</summary>
        public void ReleaseResult()
        {
            Preview?.Release();
            Hq?.Release();
        }
    }

    // The window centre: the key list and position the cache loads around. Set by MoveWindow. It may lag the
    // controller's own _nav by a message, which is why the controller never asks the cache what is on screen.
    private NavState _nav = NavState.None;
    // A navigation burst is on (a key held down): no HQ starts until ResumeHq. Previews keep loading.
    private bool _holdHq;
    // The end of the folder-switch quiet period, on _options.Time: no HQ starts before it. Only ever moves later.
    private TimeSpan _holdHqUntil;
    // Every job started and not yet completed, including abandoned ones whose decode has not returned. The job limits
    // count all of them, so slow abandoned decodes can't pile up behind the planner's back.
    private readonly List<Job> _running = [];
    // _running's specs, rebuilt every Step for the planner (kept to avoid allocating a list each round).
    private readonly List<RunningJob> _runningSpecs = [];
    // Keys this cache has stored a preview or HQ for: the candidates for unloading. The planner scans these, not the
    // whole list, to find photos that left the window.
    private readonly HashSet<int> _loaded = [];
    // Keys whose light preview pass found nothing (no disk-cache entry, no embedded thumbnail): only a full-image
    // decode (the heavy lane) can make their preview. Cleared once the preview loads.
    private readonly HashSet<int> _needsHeavy = [];
    // Keys whose preview was decoded from the file and is not in the disk cache yet: a DiskWrite job will store it.
    private readonly HashSet<int> _needsDiskWrite = [];
    // Loads that failed (the heavy preview pass found nothing) or were deferred (a neighbour too large to tile). Not
    // retried while the photo is only a neighbour, so a broken file is not decoded on every key press; MoveWindow
    // clears a key's entries when it becomes the centre.
    private readonly HashSet<(int Key, LoadKind Kind)> _parked = [];
    // The planner's output, filled afresh every Step (kept to avoid allocating every round).
    private readonly PrefetchPlan _plan = new();
    // The counts last reported, so the status text is formatted and raised only when one changes.
    private (int CachedBefore, int Before, int CachedAfter, int After) _lastStatus = (-1, -1, -1, -1);

    /// <param name="getPhoto">Resolves a key to its photo (the photo list's lookup).</param>
    /// <param name="decoder">Does every load.</param>
    /// <param name="options">The clock, job limits and threading; <see cref="PrefetchOptions.Default"/> if null.</param>
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

    // Time on the cache's clock. Only differences matter (it is compared with _holdHqUntil), so the zero start is fine.
    private TimeSpan Now => _options.Time.GetElapsedTime(0);

    // -------------------------------------------------------------------------
    // Public API — any thread; every call only posts to the prefetch thread
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts loading around <paramref name="nav"/>, and starts the prefetch thread (unless the cache is stepped by
    /// hand). Called once, when the listing is published. Not called for secondary instances.
    /// </summary>
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

    /// <summary>
    /// Holds HQ loading for <paramref name="ms"/> from now (skipping through folders). The time is read on the calling
    /// thread, so the quiet period starts at the call, not when the message runs.
    /// </summary>
    public void HoldHqFor(int ms)
    {
        var until = Now + TimeSpan.FromMilliseconds(ms);
        Post(() => { if (until > _holdHqUntil) _holdHqUntil = until; });
    }

    /// <summary>
    /// Photos removed from the list: drops every note the cache keeps about their keys. Their images are released by
    /// <see cref="DisplaySlots.Retire"/>, and a load still running for one releases what it brings
    /// (<see cref="Complete"/> finds the key gone).
    /// </summary>
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
                photo.Slots.DetachHq()?.Release();
        foreach (var job in _running)
            if (job.Spec.Kind == LoadKind.Hq && job.Photo.IsRaw)
                Abandon(job);
        _parked.RemoveWhere(p => p.Kind == LoadKind.Hq);
    });

    /// <summary>A planning setting (a window size) changed, or the clock moved: plan again now.</summary>
    public void Replan() => Post(static () => { });

    // Any thread. Queues a message for the prefetch thread and wakes it. In stepped mode nothing is woken: the
    // message runs at the next Step.
    private void Post(Action message)
    {
        _inbox.Enqueue(message);
        try { _wake.Set(); }
        catch (ObjectDisposedException) { } // shutting down
    }

    // -------------------------------------------------------------------------
    // The prefetch thread
    // -------------------------------------------------------------------------

    // The prefetch thread's body: Step, then sleep until a message, Dispose, or the end of the folder-switch quiet
    // period (when HQ loading may start without any message arriving).
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
    /// One round: applies every queued message, then plans once and carries the plan out. The prefetch thread's loop
    /// body; with <see cref="PrefetchOptions.OwnThread"/> false the caller runs it, never two at once.
    /// <para>
    /// The order matters: cancel first, so a job abandoned this round frees its place in the limits for the next
    /// (it still counts until it returns); then unload; then start new jobs.
    /// </para>
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
            // One failing message (a bug) must not stop the rest of the batch.
            try { message(); }
            catch (Exception ex) { Logger.Error(ex); }
        }
        if (_nav.Position < 0) return; // not started yet: no window to plan

        _runningSpecs.Clear();
        foreach (var job in _running) _runningSpecs.Add(job.Spec);
        bool holdHq = _holdHq || Now < _holdHqUntil;
        var settings = AppConfig.Settings; // the window sizes are read fresh every round, so a change applies at once
        PrefetchPlanner.Make(_plan, _nav, _loaded, SlotOf, _runningSpecs, holdHq,
            settings.CacheSizeOneSidePreviews, settings.CacheSizeOneSideHqImages, _options.Limits);

        foreach (int i in _plan.Cancel) Abandon(_running[i]);
        foreach (int key in _plan.EvictAll)
        {
            if (_getPhoto(key) is { } photo)
            {
                photo.Slots.DetachHq()?.Release(); // the canvas leases what is on screen
                photo.Slots.DetachPreview()?.Release();
            }
            _loaded.Remove(key);
            _needsDiskWrite.Remove(key);
        }
        foreach (int key in _plan.EvictHq)
            _getPhoto(key)?.Slots.DetachHq()?.Release();
        foreach (var spec in _plan.Start) StartJob(spec);

        ReportStatus();
    }

    // Prefetch thread. The planner's view of one key: what its slots hold, plus this cache's own notes about it.
    private SlotState SlotOf(int key)
    {
        var slots = _getPhoto(key)?.Slots;
        return new SlotState(
            HasPreview: slots?.Preview != null,
            HasHq: slots?.Hq != null,
            TiledHq: slots?.Hq is TiledHqDisplayItem,
            NeedsHeavy: _needsHeavy.Contains(key),
            NeedsDiskWrite: _needsDiskWrite.Contains(key),
            PreviewParked: _parked.Contains((key, LoadKind.PreviewLight)) || _parked.Contains((key, LoadKind.PreviewHeavy)),
            HqParked: _parked.Contains((key, LoadKind.Hq)));
    }

    // Prefetch thread. The job's result is no longer wanted: it is released when it returns. The job stays in _running
    // (and counts against the limits) until then.
    private static void Abandon(Job job)
    {
        if (job.Abandoned) return;
        job.Abandoned = true;
        job.Cts.Cancel(); // only the large-image decode observes it; the others finish and are released
    }

    // -------------------------------------------------------------------------
    // Jobs — decode off the prefetch thread, store on it
    // -------------------------------------------------------------------------

    // Prefetch thread. Tracks the job as running and starts its load (skipped if the key is no longer listed).
    private void StartJob(RunningJob spec)
    {
        if (_getPhoto(spec.Key) is not { } photo) return;
        var job = new Job(spec, photo);
        _running.Add(job);
        _ = RunJob(job); // never awaited: RunJob catches everything and reports back through the inbox
    }

    // Starts on the prefetch thread; the decoder moves the work off it at once. Everything after the first await runs
    // on the thread pool (ConfigureAwait(false)), so the job's fields are written there; TryReturn and the inbox hand
    // them back to the prefetch thread.
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

    // Prefetch thread. Takes the job off the running list and stores what it brought, unless the job was abandoned or
    // its photo left the list meanwhile — then it is released. A photo that left the window is unloaded by the next plan.
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

    // Prefetch thread. Stores a preview over nothing or an error item, and notes what to do next for the key: the heavy
    // lane (light pass found nothing), parking (heavy pass found nothing), or a disk-cache write (decoded from the file).
    // keep is false for an abandoned job: its result is released and nothing is noted.
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
        var slots = job.Photo.Slots;
        // Replaces nothing but an error placeholder (shared, never freed).
        if (!keep || slots.Preview is { } existing && !existing.IsErrorOrUndefined())
        {
            preview.Release();
            return;
        }
        if (!slots.TryAttachPreview(preview, job.Thumbnail)) return; // retired: released by TryAttachPreview
        _loaded.Add(key);
        if (preview.Origin == Origin.Disk) _needsDiskWrite.Add(key);
        Loaded?.Invoke(key);
    }

    // Prefetch thread. Stores an HQ into an empty slot; a tiled large image only for the current centre (gigabytes of
    // tiles are kept for the photo on screen alone). No result (a deferred or cancelled large image) parks a neighbour
    // until it is the centre.
    private void CompleteHq(Job job, bool keep)
    {
        int key = job.Spec.Key;
        if (job.Hq is not { } hq)
        {
            // An oversized neighbour deferred, or a large decode cancelled. Retried when it is the centre.
            if (keep && key != _nav.Key) _parked.Add((key, LoadKind.Hq));
            return;
        }
        var slots = job.Photo.Slots;
        if (!keep || slots.Hq != null || hq is TiledHqDisplayItem && key != _nav.Key)
        {
            hq.Release();
            return;
        }
        if (!slots.TryAttachHq(hq)) return; // retired: released by TryAttachHq
        _loaded.Add(key);
        Loaded?.Invoke(key);
    }

    // Starts on the prefetch thread (from RunJob). Writes a preview decoded from the file (Origin.Disk) to the disk
    // cache with the photo's real pixel size. Nothing to do if the preview was unloaded or replaced meanwhile.
    private Task WriteToDiskCache(Photo photo)
    {
        if (photo.Slots.Preview is not { Origin: Origin.Disk } preview) return Task.CompletedTask;
        var (actualWidth, actualHeight) = photo.Slots.GetSourcePixelSize();
        // The path is read now, as the job starts: a rename meanwhile files it under the name it was decoded from.
        return _decoder.PutPreviewInDiskCache(photo.FilePath, preview,
            (int)Math.Round(actualWidth), (int)Math.Round(actualHeight));
    }

    // -------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------

    // Prefetch thread. The planner counted the cached previews on its pass over the window; formatted and raised only
    // when something changed.
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
    /// UI thread. Stops loading. Every decode still running, or returned but not yet stored, has its result released
    /// whenever it arrives. Images already stored stay with their photos (retired by the controller).
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
