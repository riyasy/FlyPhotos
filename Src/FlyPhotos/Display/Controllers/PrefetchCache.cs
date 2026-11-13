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
/// Threading: one dedicated <b>prefetch thread</b> owns every bit of loading state (the window, running jobs, load
/// hints) and is the only writer of the photos' <see cref="PhotoImages"/> slots once started. Other threads talk to
/// it only through its inbox: the UI posts window moves and settings, and decodes running on the thread pool post
/// their results. After each batch of messages the prefetch thread asks <see cref="PrefetchPlanner"/> what to do and does it.
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
    /// Fired on the prefetch thread when a preview or HQ was stored for a key. The photo may or may not be on
    /// screen — the controller checks. Not fired for the first photo (the controller loads that itself).
    /// </summary>
    public event Action<int>? Loaded;

    /// <summary>Fired on the prefetch thread with the formatted "left/total &lt; Cache &gt; right/total" status.</summary>
    public event Action<string>? CacheStatusChanged;

    private readonly Func<int, Photo?> _getPhoto;
    private readonly ICanvasResourceCreatorWithDpi _device;

    // --- Inbox (any thread) ---
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    // --- Prefetch-thread state ---
    private sealed class Job(RunningJob spec, Photo photo)
    {
        public readonly RunningJob Spec = spec;
        public readonly Photo Photo = photo;
        public readonly CancellationTokenSource Cts = new();
        // Cancelled, or made stale by a RAW setting change: its result is released, not stored.
        public bool Abandoned;

        // The result, written by the decode before it posts the job back (the post publishes them).
        public PreviewDisplayItem? Preview;
        public Thumbnail? Thumbnail;
        public HqDisplayItem? Hq;
        public bool Failed;

        public void ReleaseResult()
        {
            Preview?.Release();
            Hq?.Release();
        }
    }

    private NavState _nav = NavState.None;
    private bool _holdHq;
    private long _holdHqUntil;                         // Environment.TickCount64; the folder-switch quiet period
    private readonly List<Job> _running = [];
    private readonly List<RunningJob> _runningSpecs = [];
    private readonly HashSet<int> _loaded = [];        // keys this cache has stored images for
    private readonly HashSet<int> _needsHeavy = [];    // light preview pass found nothing (until the preview loads)
    private readonly HashSet<int> _needsDiskWrite = []; // preview decoded from the file, not yet in the disk cache
    private readonly HashSet<(int Key, LoadKind Kind)> _parked = []; // failed or deferred; retried once it is the centre
    private readonly PrefetchPlan _plan = new();
    private readonly JobLimits _limits = JobLimits.ForCores(Environment.ProcessorCount);
    private (int CachedBefore, int Before, int CachedAfter, int After) _lastStatus = (-1, -1, -1, -1);

    public PrefetchCache(Func<int, Photo?> getPhoto, ICanvasResourceCreatorWithDpi device)
    {
        _getPhoto = getPhoto;
        _device = device;
    }

    // -------------------------------------------------------------------------
    // Public API — any thread; every call only posts to the prefetch thread
    // -------------------------------------------------------------------------

    /// <summary>Starts the prefetch thread around <paramref name="nav"/>. Not called for secondary instances.</summary>
    public void Start(NavState nav)
    {
        MoveWindow(nav);
        Post(() => _loaded.Add(nav.Key)); // the photo on screen was loaded by the controller (the first photo)
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

    /// <summary>A planning setting (a window size) changed: plan again now. The planner reads it from the settings.</summary>
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
            long wait = _holdHqUntil - Environment.TickCount64;
            try { WaitHandle.WaitAny(handles, wait > 0 ? (int)wait + 1 : Timeout.Infinite); }
            catch (ObjectDisposedException) { return; } // shutting down
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
        PrefetchPlanner.Make(_plan, _nav, _loaded, SlotOf, _runningSpecs, holdHq,
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
    // Jobs — decode on the thread pool, store on the prefetch thread
    // -------------------------------------------------------------------------

    private void StartJob(RunningJob spec)
    {
        if (_getPhoto(spec.Key) is not { } photo) return;
        var job = new Job(spec, photo);
        _running.Add(job);
        _ = Task.Run(async () =>
        {
            try
            {
                switch (spec.Kind)
                {
                    case LoadKind.PreviewLight or LoadKind.PreviewHeavy:
                        var purpose = spec.Kind == LoadKind.PreviewHeavy ? Purpose.PreviewHeavy : Purpose.PreviewLight;
                        if (await PhotoDecoder.LoadPreview(photo, _device, purpose) is { } loaded)
                            (job.Preview, job.Thumbnail) = loaded;
                        break;
                    case LoadKind.Hq:
                        job.Hq = await PhotoDecoder.LoadHq(photo, _device, spec.AllowLarge, job.Cts.Token);
                        break;
                    case LoadKind.DiskWrite:
                        await WriteToDiskCache(photo);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"{spec.Kind} load failed for {photo.FilePath}");
                job.Failed = true;
            }
            if (_stop.IsCancellationRequested) job.ReleaseResult();
            else Post(() => Complete(job));
        });
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
            if (job.Abandoned) return;
            switch (job.Spec.Kind)
            {
                case LoadKind.DiskWrite: _needsDiskWrite.Remove(key); return; // not retried
                case LoadKind.Hq: job.Hq = ImageReader.GetHqFailedIndicator(); break;
                default: job.Preview = ImageReader.GetPreviewFailedIndicator(); break;
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
            if (!job.Abandoned && key != _nav.Key) _parked.Add((key, LoadKind.Hq));
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

    private static async Task WriteToDiskCache(Photo photo)
    {
        if (photo.Images.Preview is not { Origin: Origin.Disk } preview) return;
        var (actualWidth, actualHeight) = photo.Images.GetSourcePixelSize();
        await PhotoDecoder.PutPreviewInDiskCache(photo.FilePath, preview,
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

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(2000);
        // The prefetch thread is gone: nothing reads these now. Results still on their way are released by their tasks.
        foreach (var job in _running) job.Cts.Cancel();
        _wake.Dispose();
    }
}
