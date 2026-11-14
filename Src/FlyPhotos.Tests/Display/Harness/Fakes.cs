using System.Collections.Concurrent;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using Microsoft.Graphics.Canvas;
using NLog;
using NLog.Config;
using NLog.Targets;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace FlyPhotos.Tests.Display.Harness;

/// <summary>A headless software Win2D device: real bitmaps, no window, no GPU needed.</summary>
internal static class TestGpu
{
    public static readonly CanvasDevice Device = new(forceSoftwareRenderer: true);
    public static CanvasBitmap Bitmap() => CanvasBitmap.CreateFromColors(Device, new Color[4], 2, 2);
}

/// <summary>Thrown by a scripted decode, so the log check can tell it from a real bug.</summary>
internal sealed class ScriptedFailure() : Exception("scripted decode failure");

/// <summary>What a decode does when it is asked for.</summary>
internal enum Reply
{
    /// <summary>A fresh image, at once.</summary>
    Item,
    /// <summary>Nothing (light preview: needs the heavy lane; HQ: deferred; first preview: not found).</summary>
    Nothing,
    /// <summary>The decoder throws.</summary>
    Throw,
    /// <summary>The decoder reports the failure itself: the error item.</summary>
    Error,
    /// <summary>HQ: a tiled large image.</summary>
    Tiled,
    /// <summary>Waits until the test completes it (<see cref="Decode"/>).</summary>
    Pending,
    /// <summary>A fresh image a few milliseconds later, from the thread pool (stress runs).</summary>
    Async,
    /// <summary>Never returns.</summary>
    Hang,
}

internal enum DecodeKind { FirstPreview, PreviewLight, PreviewHeavy, Hq, DiskWrite }

/// <summary>One decode the pipeline asked for. Pending ones are completed by the test.</summary>
internal sealed class Decode(FakeDecoder owner, Photo photo, DecodeKind kind, bool allowLarge, CancellationToken ct)
{
    public Photo Photo { get; } = photo;
    public DecodeKind Kind { get; } = kind;
    public bool AllowLarge { get; } = allowLarge;
    public CancellationToken Ct { get; } = ct;
    public string Path { get; } = photo.FilePath;
    // Continuations run inline on the completing thread: the cache's job posts its result before Complete returns.
    internal readonly TaskCompletionSource<object?> Tcs = new();
    private int _done;
    public bool IsDone => Volatile.Read(ref _done) != 0;

    public void Complete(Reply reply)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        owner.Finished(this);
        // ConfigureAwait(false) continuations only run inline on a thread without a SynchronizationContext: step out
        // of the hand-pumped UI's, so the cache's job hands its result back before this returns.
        using var _ = OutsideUi();
        try
        {
            switch (reply)
            {
                case Reply.Throw: Tcs.SetException(new ScriptedFailure()); return;
                case Reply.Nothing: Tcs.SetResult(Kind == DecodeKind.FirstPreview ? false : null); return;
                case Reply.Error:
                    Tcs.SetResult(Kind switch
                    {
                        DecodeKind.Hq => owner.HqFailed(),
                        DecodeKind.PreviewLight or DecodeKind.PreviewHeavy => ((PreviewDisplayItem, Thumbnail?)?)(owner.PreviewFailed(), null),
                        _ => null,
                    });
                    return;
                default: Tcs.SetResult(owner.MakeResult(this, reply)); return;
            }
        }
        catch (Exception ex) when (ex is not ScriptedFailure)
        {
            Tcs.TrySetException(ex);
        }
    }

    public void Succeed() => Complete(Reply.Item);
    public void Cancelled()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        owner.Finished(this);
        using var _ = OutsideUi();
        Tcs.SetException(new OperationCanceledException(Ct));
    }

    private static Restore OutsideUi()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        return new Restore(previous);
    }

    private readonly struct Restore(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }

    public override string ToString() => $"{Kind} {System.IO.Path.GetFileName(Path)}";
}

/// <summary>
/// A scripted <see cref="IPhotoDecoder"/>. <see cref="Script"/> picks each decode's <see cref="Reply"/>; pending ones
/// wait in <see cref="Pending"/> for the test. Every item it makes is kept in <see cref="Created"/>, so a test can
/// check that each one is either held or freed, and it records how many decodes of each lane ran at once.
/// </summary>
internal sealed class FakeDecoder : IPhotoDecoder
{
    public Func<Decode, Reply> Script { get; set; } = _ => Reply.Item;
    /// <summary>Where light previews come from; <see cref="Origin.Disk"/> ones are then written to the disk cache.</summary>
    public Origin PreviewOrigin { get; set; } = Origin.Disk;

    public readonly ConcurrentQueue<DisplayItem> Created = new();
    public readonly ConcurrentQueue<Decode> All = new();
    private readonly List<Decode> _pending = [];
    private readonly Lock _lock = new();
    private readonly Random _asyncDelay = new(7);

    private readonly CanvasBitmap _errorBitmap = TestGpu.Bitmap();
    public DisplayItem LoadingIndicator { get; }
    public int WarmUps;
    public readonly ConcurrentQueue<string> DiskWrites = new();

    // In flight now, and the most at once: previews (both lanes), heavy, HQ, disk writes.
    private int _previews, _heavy, _hq, _writes;
    public int MaxPreviews, MaxHeavy, MaxHq, MaxWrites;
    public int InFlight => Volatile.Read(ref _previews) + Volatile.Read(ref _hq) + Volatile.Read(ref _writes);

    public FakeDecoder() => LoadingIndicator = new PreviewDisplayItem(_errorBitmap, Origin.ErrorScreen);

    public HqDisplayItem HqFailed() => new StaticHqDisplayItem(_errorBitmap, Origin.ErrorScreen);
    public PreviewDisplayItem PreviewFailed() => new(_errorBitmap, Origin.ErrorScreen);
    /// <summary>Opening the disk cache throws (set before the controller is made: the startup thread calls it).</summary>
    public bool WarmUpFails;

    public void WarmUp()
    {
        Interlocked.Increment(ref WarmUps);
        if (WarmUpFails) throw new ScriptedFailure();
    }

    public IReadOnlyList<Decode> Pending { get { lock (_lock) return [.. _pending]; } }
    public IReadOnlyList<Decode> PendingOf(DecodeKind kind) => [.. Pending.Where(d => d.Kind == kind)];

    /// <summary>Completes every pending decode with <paramref name="reply"/>.</summary>
    public void CompleteAll(Reply reply = Reply.Item)
    {
        foreach (var d in Pending) d.Complete(reply);
    }

    public async Task<bool> LoadFirstPreview(Photo photo)
    {
        var result = await Begin(photo, DecodeKind.FirstPreview, false, default).ConfigureAwait(false);
        return result is true;
    }

    public async Task<HqDisplayItem?> LoadHq(Photo photo, bool allowLarge, CancellationToken ct) =>
        (HqDisplayItem?)await Begin(photo, DecodeKind.Hq, allowLarge, ct).ConfigureAwait(false);

    public async Task<(PreviewDisplayItem Preview, Thumbnail? Thumbnail)?> LoadPreview(Photo photo, Purpose purpose)
    {
        var kind = purpose == Purpose.PreviewHeavy ? DecodeKind.PreviewHeavy : DecodeKind.PreviewLight;
        return ((PreviewDisplayItem, Thumbnail?)?)await Begin(photo, kind, false, default).ConfigureAwait(false);
    }

    public async Task PutPreviewInDiskCache(string path, PreviewDisplayItem preview, int width, int height)
    {
        // The real one writes under its own lease.
        if (!preview.TryAcquire()) return;
        try
        {
            await Begin(path, DecodeKind.DiskWrite).ConfigureAwait(false);
            DiskWrites.Enqueue(path);
        }
        finally { preview.Release(); }
    }

    private Task<object?> Begin(string path, DecodeKind kind) => Begin(new Photo(path), kind, false, default);

    private Task<object?> Begin(Photo photo, DecodeKind kind, bool allowLarge, CancellationToken ct)
    {
        var d = new Decode(this, photo, kind, allowLarge, ct);
        All.Enqueue(d);
        Started(d);
        var reply = Script(d);
        switch (reply)
        {
            case Reply.Pending:
                lock (_lock) _pending.Add(d);
                // Like the real large-image decode: cancelling it ends it with OperationCanceledException.
                if (kind == DecodeKind.Hq && allowLarge && ct.CanBeCanceled) ct.Register(d.Cancelled);
                break;
            case Reply.Hang:
                break;
            case Reply.Async:
                int delay;
                lock (_asyncDelay) delay = _asyncDelay.Next(0, 4);
                _ = Task.Run(async () => { await Task.Delay(delay); d.Succeed(); });
                break;
            default:
                d.Complete(reply);
                break;
        }
        return d.Tcs.Task;
    }

    internal object? MakeResult(Decode d, Reply reply)
    {
        switch (d.Kind)
        {
            case DecodeKind.FirstPreview:
                var first = Track(new PreviewDisplayItem(TestGpu.Bitmap(), PreviewOrigin), d);
                d.Photo.Images.TryAttachPreview(first, new Thumbnail(new byte[4]));
                return true;
            case DecodeKind.PreviewLight or DecodeKind.PreviewHeavy:
                var origin = d.Kind == DecodeKind.PreviewHeavy ? Origin.Disk : PreviewOrigin;
                return ((PreviewDisplayItem, Thumbnail?)?)(Track(new PreviewDisplayItem(TestGpu.Bitmap(), origin), d),
                    new Thumbnail(new byte[4]));
            case DecodeKind.Hq when reply == Reply.Tiled:
                var pyramid = TilePyramid.Build(new TileLevel(4, 4), DirectXPixelFormat.B8G8R8A8UIntNormalized);
                return Track(new TiledHqDisplayItem(TestGpu.Bitmap(), Origin.Disk, pyramid), d);
            case DecodeKind.Hq:
                return Track(new StaticHqDisplayItem(TestGpu.Bitmap(), Origin.Disk), d);
            default:
                return null;
        }
    }

    private T Track<T>(T item, Decode d) where T : DisplayItem
    {
        Created.Enqueue(item);
        MadeBy[item] = d;
        return item;
    }

    /// <summary>Which decode made each item, for failure messages.</summary>
    public readonly ConcurrentDictionary<DisplayItem, Decode> MadeBy = new(ReferenceEqualityComparer.Instance);

    public string Describe(DisplayItem item) =>
        $"{item.GetType().Name} from {(MadeBy.TryGetValue(item, out var d) ? d.ToString() : "?")}";

    private void Started(Decode d)
    {
        switch (d.Kind)
        {
            case DecodeKind.PreviewLight: Max(ref MaxPreviews, Interlocked.Increment(ref _previews)); break;
            case DecodeKind.PreviewHeavy:
                Max(ref MaxPreviews, Interlocked.Increment(ref _previews));
                Max(ref MaxHeavy, Interlocked.Increment(ref _heavy));
                break;
            case DecodeKind.Hq: Max(ref MaxHq, Interlocked.Increment(ref _hq)); break;
            case DecodeKind.DiskWrite: Max(ref MaxWrites, Interlocked.Increment(ref _writes)); break;
        }
    }

    internal void Finished(Decode d)
    {
        lock (_lock) _pending.Remove(d);
        switch (d.Kind)
        {
            case DecodeKind.PreviewLight: Interlocked.Decrement(ref _previews); break;
            case DecodeKind.PreviewHeavy: Interlocked.Decrement(ref _previews); Interlocked.Decrement(ref _heavy); break;
            case DecodeKind.Hq: Interlocked.Decrement(ref _hq); break;
            case DecodeKind.DiskWrite: Interlocked.Decrement(ref _writes); break;
        }
    }

    private static void Max(ref int max, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, value, seen) != seen) { }
    }
}

/// <summary>
/// The canvas, as the controller sees it: takes over the lease of each item it is given and releases the one it
/// replaces, like the real canvas. Records every install, and flags a freed or missing item.
/// </summary>
internal sealed class FakeCanvas : ICanvasController
{
    public sealed record Install(Photo Photo, DisplayItem Item, DisplayLevel Level);

    public readonly List<Install> History = [];
    public readonly List<string> Violations = [];
    public DisplayItem? Held { get; private set; }
    public Photo? HeldPhoto { get; private set; }
    public DisplayLevel HeldLevel { get; private set; }
    /// <summary>Installs that throw (the canvas could not draw the item); it releases the lease first.</summary>
    public Func<DisplayItem, bool>? Fails { get; set; }
    public int Failures;
    public readonly List<(string Old, string New)> Renames = [];

    public Task SetSource(Photo photo, DisplayItem? item, DisplayLevel level)
    {
        if (item == null)
        {
            Violations.Add($"no item for {photo.FilePath} at {level}");
            return Task.CompletedTask;
        }
        if (item.IsFreed) Violations.Add($"freed item handed over for {photo.FilePath} at {level}");
        _ = photo.Images.GetActualSize(); // the real canvas lays the photo out from it
        if (Fails?.Invoke(item) == true)
        {
            Failures++;
            item.Release();
            throw new InvalidOperationException("scripted canvas failure");
        }
        var old = Held;
        (Held, HeldPhoto, HeldLevel) = (item, photo, level);
        History.Add(new Install(photo, item, level));
        old?.Release();
        return Task.CompletedTask;
    }

    /// <summary>The pan/zoom animation a wait returns for (the open zoom); done at once by default.</summary>
    public Task Animation { get; set; } = Task.CompletedTask;
    public Task WaitForPanZoomAnimationAsync(int timeoutMs) => Animation;
    /// <summary>The page on screen of a multi-page photo; null for any other.</summary>
    public MultiPagePosition? CurrentPage { get; set; }
    public void OnPhotoRenamed(string oldPath, string newPath) => Renames.Add((oldPath, newPath));

    public void Dispose()
    {
        Held?.Release();
        Held = null;
    }
}

/// <summary>The thumbnail strip: records what the controller asks of it, and reads the list as the strip does.</summary>
internal sealed class FakeStrip : IThumbnailController
{
    public Func<int, Photo?>? GetPhoto;
    public Func<NavState>? GetNav;
    public readonly List<NavDirection> Bounces = [];
    public int RedrawRequests;
    public int SlotRedraws;

    public void RequestThumbnailRedraw() => RedrawRequests++;
    public void RedrawThumbNailsIfNeeded(int key) => Interlocked.Increment(ref SlotRedraws);
    public void SetPhotoSource(Func<int, Photo?> getPhoto, Func<NavState> getNav) => (GetPhoto, GetNav) = (getPhoto, getNav);
    public void BounceAtEdge(NavDirection direction) => Bounces.Add(direction);
    public void Dispose() { }
}

/// <summary>
/// The UI thread, pumped by hand on the test's own thread: posted work runs only in <see cref="RunPending"/>, so a
/// test decides exactly when the controller's queued work (listing publish, upgrades, await continuations) happens.
/// </summary>
internal sealed class ManualUi : SynchronizationContext, IDisposable
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly SynchronizationContext? _previous = Current;

    public ManualUi() => SetSynchronizationContext(this);

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>How much posted work waits to run.</summary>
    public int Queued => _queue.Count;

    /// <summary>Runs everything queued, including what that queues. Returns how much ran.</summary>
    public int RunPending()
    {
        int ran = 0;
        while (_queue.TryDequeue(out var work))
        {
            work.Callback(work.State);
            ran++;
        }
        return ran;
    }

    public void Dispose() => SetSynchronizationContext(_previous);
}

/// <summary>A clock that moves only when told.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = TimeSpan.TicksPerHour;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>Captures the app's NLog errors while alive, so a test can fail on any it did not script.</summary>
internal sealed class LogCapture : IDisposable
{
    public readonly ConcurrentQueue<(string Message, Exception? Exception)> Errors = new();
    private readonly LoggingConfiguration? _previous = LogManager.Configuration;

    public LogCapture()
    {
        var config = new LoggingConfiguration();
        var target = new MethodCallTarget("capture", (e, _) => Errors.Enqueue((e.FormattedMessage, e.Exception)));
        config.AddRule(LogLevel.Error, LogLevel.Fatal, target);
        LogManager.Configuration = config;
    }

    /// <summary>Errors not caused by a scripted decode failure.</summary>
    public IReadOnlyList<string> Unexpected => [.. Errors
        .Where(e => e.Exception is not (ScriptedFailure or OperationCanceledException))
        .Where(e => !(e.Exception is InvalidOperationException { Message: "scripted canvas failure" }))
        .Select(e => $"{e.Message} {e.Exception}")];

    public void Dispose() => LogManager.Configuration = _previous;
}
