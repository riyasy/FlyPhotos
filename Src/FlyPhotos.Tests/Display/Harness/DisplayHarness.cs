using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;

namespace FlyPhotos.Tests.Display.Harness;

/// <summary>
/// The whole display pipeline — the real controller, photo list and prefetch cache — on a folder of empty files,
/// with a scripted decoder, a recording canvas and strip, a hand-pumped UI thread and a manual clock. By default the
/// prefetch cache is stepped by hand too, so a test is deterministic: nothing happens until it pumps.
/// <para>
/// <see cref="CheckInvariants"/> checks the rules every moment must keep; <see cref="Dispose"/> then checks that
/// every image the decoder made was freed.
/// </para>
/// </summary>
internal sealed class DisplayHarness : IDisposable
{
    public readonly string Folder;
    public readonly ManualUi Ui = new();
    public readonly ManualClock Clock = new();
    public readonly FakeDecoder Decoder = new();
    public readonly FakeCanvas Canvas = new();
    public readonly FakeStrip Strip = new();
    public readonly LogCapture Log = new();
    public readonly PhotoDisplayController Controller;
    public readonly JobLimits Limits;
    public readonly bool OwnThread;
    private bool _disposed;

    /// <param name="photos">How many files the folder holds (img_00000.jpg …).</param>
    /// <param name="openAt">Which one Fly is opened with.</param>
    /// <param name="ownThread">Run the prefetch cache on its own thread (stress runs) instead of stepping it.</param>
    public DisplayHarness(int photos = 20, int openAt = 0, int previewHalf = 5, int hqHalf = 2,
        ListSource source = ListSource.Explorer, bool ownThread = false, JobLimits? limits = null,
        Func<Decode, Reply>? script = null, string extension = ".jpg", Action<string>? beforeStart = null,
        bool warmUpFails = false)
    {
        Decoder.WarmUpFails = warmUpFails;
        Folder = Path.Combine(Path.GetTempPath(), "FlyHarness_" + Guid.NewGuid().ToString("N"));
        var sub = Path.Combine(Folder, "b_photos");
        Directory.CreateDirectory(sub);
        for (int i = 0; i < photos; i++) File.WriteAllBytes(PathOf(i, sub, extension), []);
        beforeStart?.Invoke(sub); // the controller starts listing as soon as it is made

        AppConfig.Settings = new AppSettings
        {
            CacheSizeOneSidePreviews = previewHalf,
            CacheSizeOneSideHqImages = hqHalf,
            ListSource = source,
            OpenExitZoom = false,
            ShowImageDimensions = true,
        };
        if (script != null) Decoder.Script = script;
        Limits = limits ?? new JobLimits(Previews: 3, Heavy: 2, Hq: 2, DiskWrites: 2);
        OwnThread = ownThread;
        PhotoFolder = sub;
        Extension = extension;
        Controller = new PhotoDisplayController(Canvas, Strip, Decoder, Ui, PathOf(openAt), launchedExternally: false,
            new PrefetchOptions(Clock, Limits, ownThread));
    }

    /// <summary>The folder the photos are in (inside <see cref="Folder"/>, so sibling folders can sit next to it).</summary>
    public string PhotoFolder { get; }
    public string Extension { get; }

    public string PathOf(int i) => PathOf(i, PhotoFolder, Extension);
    private static string PathOf(int i, string folder, string extension) => Path.Combine(folder, $"img_{i:D5}{extension}");

    public PrefetchCache Cache => Controller.Cache;
    public NavState Nav => Controller.Nav;
    public Photo Current => Controller.Photos.GetPhoto(Nav.Key)!;
    public Photo PhotoAt(int position) => Controller.Photos.GetPhoto(Nav.Keys[position])!;
    public string CurrentName => Path.GetFileName(Current.FilePath);

    // --- Driving ---

    /// <summary>Loads the first photo and waits for the listing to be published.</summary>
    public DisplayHarness Open()
    {
        var first = Controller.LoadFirstPhoto();
        RunUntil(() => first.IsCompleted && Controller.ListingPublished, "the first photo and the listing");
        first.GetAwaiter().GetResult();
        Settle();
        return this;
    }

    /// <summary>Runs an async controller call to completion, pumping the UI thread (not the cache) meanwhile.</summary>
    public void Run(Func<Task> call)
    {
        var task = call();
        RunUntil(() => task.IsCompleted, "the call", stepCache: false);
        task.GetAwaiter().GetResult();
    }

    public void Press(NavDirection direction) => Run(() => Controller.Fly(direction));
    public void Next() => Press(NavDirection.Next);
    public void Prev() => Press(NavDirection.Prev);
    public void Brake() => Run(Controller.Brake);

    /// <summary>One step of the cache (stepped mode), then the UI work it caused.</summary>
    public void StepCache()
    {
        Cache.Step();
        Ui.RunPending();
    }

    /// <summary>Moves the clock and lets the cache see it.</summary>
    public void Advance(int ms)
    {
        Clock.Advance(TimeSpan.FromMilliseconds(ms));
        Cache.Replan();
    }

    /// <summary>
    /// Stepped mode: pumps the UI thread and the cache until neither has anything left to do (pending decodes stay
    /// pending). With the cache on its own thread, pumps the UI thread only.
    /// </summary>
    public void Settle()
    {
        if (OwnThread)
        {
            Ui.RunPending(); // the cache runs by itself; wait for what you need with RunUntil
            return;
        }
        for (int round = 0; round < 100_000; round++)
        {
            int work = Ui.RunPending();
            if (!OwnThread)
            {
                // Always plan once: a message may have been handled meanwhile without leaving one behind.
                Cache.Step();
                while (Cache.HasMessages)
                {
                    Cache.Step();
                    work++;
                }
            }
            if (work == 0 && !Cache.HasMessages && Ui.RunPending() == 0) return;
        }
        throw new AssertFailedException("The pipeline never settled");
    }

    /// <summary>Pumps (and steps the cache, in stepped mode) until <paramref name="done"/>, or fails.</summary>
    public void RunUntil(Func<bool> done, string what, int timeoutMs = 20_000, bool stepCache = true)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            int work = Ui.RunPending();
            if (stepCache && !OwnThread && Cache.HasMessages) { Cache.Step(); work++; }
            if (Environment.TickCount64 > deadline) throw new AssertFailedException($"Timed out waiting for {what}");
            if (work == 0) Thread.Sleep(1);
        }
    }

    /// <summary>Applies a list change through the controller, as Folder mode does.</summary>
    public void ChangeList(ListChangeBatch batch, bool goToFirst = false) =>
        Run(() => Controller.ChangeListAsync(batch, goToFirst));

    public void Remove(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        ChangeList(new ListChangeBatch(set.Contains, null, [], null));
    }

    public void Add(params string[] paths) =>
        ChangeList(new ListChangeBatch(_ => false, null, paths, StringComparer.OrdinalIgnoreCase));

    // --- Checking ---

    /// <summary>
    /// The rules that hold at every quiet moment (stepped mode, after <see cref="Settle"/>):
    /// <list type="bullet">
    ///   <item>the canvas was never handed a freed or missing item, and shows the photo the viewer is on;</item>
    ///   <item>no stored image is freed (no use-after-free), and nothing is alive that nobody holds (no leak);</item>
    ///   <item>no more photos hold previews and HQs than the windows allow;</item>
    ///   <item>no lane ever ran more decodes at once than its limit;</item>
    ///   <item>the strip sees one consistent list and position;</item>
    ///   <item>nothing was logged but scripted failures.</item>
    /// </list>
    /// </summary>
    public void CheckInvariants(bool expectSettled = true)
    {
        Assert.AreEqual(0, Canvas.Violations.Count, string.Join("\n", Canvas.Violations));
        var nav = Nav;
        Assert.IsTrue(nav.Position >= 0 && nav.Position < nav.Keys.Count, $"position {nav.Position} of {nav.Keys.Count}");
        Assert.AreSame(Controller.Photos.Keys, nav.Keys, "the viewer's key list is the published list");
        if (Canvas.Held is { } drawn) Assert.IsFalse(drawn.IsFreed, "the canvas draws a freed item");
        if (Controller.ListingPublished)
            Assert.AreSame(Current, Canvas.HeldPhoto, $"the screen shows {Canvas.HeldPhoto?.FilePath}, the viewer is on {Current.FilePath}");

        var stripNav = Strip.GetNav!();
        Assert.IsNotNull(Strip.GetPhoto!(stripNav.Key), "the strip's current key resolves to a photo");

        var held = new HashSet<DisplayItem>(ReferenceEqualityComparer.Instance);
        int previews = 0, hqs = 0;
        foreach (int key in nav.Keys)
        {
            var slots = Controller.Photos.GetPhoto(key)!.Slots;
            if (slots.Preview is { } p) { Assert.IsFalse(p.IsFreed, "a stored preview is freed"); held.Add(p); previews++; }
            if (slots.Hq is { } h) { Assert.IsFalse(h.IsFreed, "a stored HQ is freed"); held.Add(h); hqs++; }
        }
        if (Canvas.Held is { } shown) held.Add(shown);

        var settings = AppConfig.Settings;
        if (expectSettled)
        {
            // A photo in either window keeps its preview.
            int previewWindow = 2 * Math.Max(settings.CacheSizeOneSidePreviews, settings.CacheSizeOneSideHqImages) + 1;
            int hqWindow = 2 * settings.CacheSizeOneSideHqImages + 1;
            Assert.IsTrue(previews <= previewWindow, $"{previews} previews held, window {previewWindow}");
            Assert.IsTrue(hqs <= hqWindow + 1, $"{hqs} HQs held, window {hqWindow}"); // + the first photo, until evicted
            if (Decoder.Pending.Count == 0 && Decoder.InFlight == 0)
            {
                var leaked = Decoder.Created.Where(i => !i.IsFreed && !held.Contains(i)).ToList();
                Assert.AreEqual(0, leaked.Count, $"{leaked.Count} images alive that nobody holds: " +
                    string.Join("; ", leaked.Select(Decoder.Describe)) + $" (on {CurrentName})");
            }
        }

        Assert.IsTrue(Decoder.MaxPreviews <= Limits.Previews, $"{Decoder.MaxPreviews} previews at once, limit {Limits.Previews}");
        Assert.IsTrue(Decoder.MaxHeavy <= Limits.Heavy, $"{Decoder.MaxHeavy} heavy at once, limit {Limits.Heavy}");
        // + the first photo's HQ, which the controller loads itself before the cache starts.
        Assert.IsTrue(Decoder.MaxHq <= Limits.Hq + 1, $"{Decoder.MaxHq} HQs at once, limit {Limits.Hq}");
        Assert.IsTrue(Decoder.MaxWrites <= Limits.DiskWrites, $"{Decoder.MaxWrites} writes at once, limit {Limits.DiskWrites}");

        var unexpected = Log.Unexpected;
        Assert.AreEqual(0, unexpected.Count, string.Join("\n", unexpected));
    }

    /// <summary>The level the canvas shows the photo the viewer is on at.</summary>
    public DisplayLevel ShownLevel => Canvas.HeldLevel;

    /// <summary>
    /// Disposes the pipeline as the window does, lets every decode still out return, and checks that every image
    /// the decoder made is freed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Controller.Dispose();
            Canvas.Dispose();
            Decoder.CompleteAll();
            if (OwnThread)
            {
                var deadline = Environment.TickCount64 + 10_000;
                while (Decoder.InFlight > 0 && Environment.TickCount64 < deadline) Thread.Sleep(5);
            }
            Ui.RunPending();
            // A decode on the thread pool releases its orphaned result just after it reports finishing.
            var settleBy = Environment.TickCount64 + (OwnThread ? 5_000 : 0);
            while (Decoder.Created.Any(i => !i.IsFreed) && Environment.TickCount64 < settleBy) Thread.Sleep(5);
            var alive = Decoder.Created.Where(i => !i.IsFreed).ToList();
            Assert.AreEqual(0, alive.Count, $"{alive.Count} images still alive after dispose: " +
                string.Join("; ", alive.Select(Decoder.Describe)));
            var unexpected = Log.Unexpected;
            Assert.AreEqual(0, unexpected.Count, string.Join("\n", unexpected));
        }
        finally
        {
            Ui.Dispose();
            Log.Dispose();
            try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
