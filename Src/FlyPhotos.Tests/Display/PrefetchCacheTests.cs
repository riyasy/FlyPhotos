using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The prefetch cache's message and commit rules, stepped by hand over a real photo list with a scripted decoder:
/// what it stores, releases, parks, retries and cancels, and that nothing it touched is leaked at the end.
/// </summary>
[TestClass]
public sealed class PrefetchCacheTests
{
    private sealed class Rig : IDisposable
    {
        public readonly PhotoList List = new();
        public readonly FakeDecoder Decoder = new();
        public readonly ManualClock Clock = new();
        public readonly PrefetchCache Cache;
        public readonly List<int> LoadedKeys = [];
        public readonly List<string> Statuses = [];
        public readonly LogCapture Log = new();
        public readonly JobLimits Limits;
        private int _position = -1;
        private readonly bool _ownThread;

        public Rig(int photos = 11, int previewHalf = 5, int hqHalf = 2, JobLimits? limits = null,
            Func<Decode, Reply>? script = null, string extension = ".jpg", bool ownThread = false)
        {
            AppConfig.Settings = new AppSettings { CacheSizeOneSidePreviews = previewHalf, CacheSizeOneSideHqImages = hqHalf };
            List.Initialize([.. Enumerable.Range(0, photos).Select(i => $@"c:\nowhere\p{i:D5}{extension}")]);
            Decoder.Script = script ?? (_ => Reply.Pending);
            Limits = limits ?? new JobLimits(Previews: 3, Heavy: 2, Hq: 2, DiskWrites: 2);
            _ownThread = ownThread;
            Cache = new PrefetchCache(List.GetPhoto, Decoder, new PrefetchOptions(Clock, Limits, ownThread));
            Cache.Loaded += k => { lock (LoadedKeys) LoadedKeys.Add(k); };
            Cache.CacheStatusChanged += s => Statuses.Add(s);
        }

        public NavState Nav(int position) => new(List.Keys, position);
        public int PositionOf(Photo photo) => List.Keys.ToList().FindIndex(k => ReferenceEquals(List.GetPhoto(k), photo));
        public Photo At(int position) => List.GetPhoto(List.Keys[position])!;
        public int Key(int position) => List.Keys[position];

        public void Start(int position)
        {
            _position = position;
            Cache.Start(Nav(position));
            Settle();
        }

        public void Move(int position)
        {
            _position = position;
            Cache.MoveWindow(Nav(position));
            Settle();
        }

        public void Settle()
        {
            Cache.Step();
            while (Cache.HasMessages) Cache.Step();
        }

        public IReadOnlyList<Decode> Pending(DecodeKind kind, int? position = null) =>
            [.. Decoder.PendingOf(kind).Where(d => position == null || ReferenceEquals(d.Photo, At(position.Value)))];

        public Decode Only(DecodeKind kind, int position) => Pending(kind, position).Single();

        /// <summary>Removes photos as the controller does: list change, forget, retire.</summary>
        public void Remove(params int[] positions)
        {
            var paths = positions.Select(p => At(p).FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keep = List.Keys.First(k => !paths.Contains(List.GetPhoto(k)!.FilePath));
            var removed = List.ApplyChanges(new ListChangeBatch(paths.Contains, null, [], null), keep);
            Cache.Forget(removed.ConvertAll(r => r.Key));
            List.Forget(removed);
            int position = Math.Clamp(_position, 0, List.Keys.Count - 1);
            Cache.MoveWindow(Nav(position));
            foreach (var (_, photo) in removed) photo.Images.Retire();
            Settle();
        }

        public void Dispose()
        {
            Cache.Dispose();
            Decoder.CompleteAll();
            foreach (int key in List.Keys) List.GetPhoto(key)!.Images.Retire();
            // A decode on the thread pool counts as finished just before it makes its image and hands it back; the
            // cache releases that late result a moment later (as in DisplayHarness.Dispose).
            var settleBy = Environment.TickCount64 + (_ownThread ? 5_000 : 0);
            while (Decoder.Created.Any(i => !i.IsFreed) && Environment.TickCount64 < settleBy) Thread.Sleep(5);
            var alive = Decoder.Created.Where(i => !i.IsFreed).ToList();
            Log.Dispose();
            Assert.AreEqual(0, alive.Count, $"{alive.Count} images leaked");
        }
    }

    // --- What loads first ---

    [TestMethod]
    public void The_centre_hq_and_the_nearest_previews_load_first_and_neighbour_hqs_wait_for_the_centre()
    {
        using var rig = new Rig();
        rig.Start(5);

        var hq = rig.Decoder.PendingOf(DecodeKind.Hq).Single();
        Assert.AreSame(rig.At(5), hq.Photo);
        Assert.IsTrue(hq.AllowLarge, "only the centre may load a tiled large image");
        CollectionAssert.AreEqual(new[] { 5, 4, 6 },
            rig.Decoder.PendingOf(DecodeKind.PreviewLight).Select(d => rig.PositionOf(d.Photo)).ToArray(),
            "nearest first; the photo before wins a tie");

        hq.Succeed();
        rig.Settle();
        Assert.IsNotNull(rig.At(5).Images.Hq);
        var neighbours = rig.Decoder.PendingOf(DecodeKind.Hq);
        Assert.AreEqual(2, neighbours.Count);
        CollectionAssert.AreEquivalent(new[] { rig.At(4), rig.At(6) }, neighbours.Select(d => d.Photo).ToArray());
        Assert.IsTrue(neighbours.All(d => !d.AllowLarge));
        Assert.IsTrue(rig.LoadedKeys.Contains(rig.Key(5)));
    }

    [TestMethod]
    public void A_full_window_fills_and_stays_within_every_limit()
    {
        using var rig = new Rig(photos: 400, previewHalf: 60, hqHalf: 3, limits: new JobLimits(4, 2, 2, 2));
        rig.Start(200);
        var rng = new Random(11);
        for (int i = 0; i < 5000 && rig.Decoder.Pending.Count > 0; i++)
        {
            var pending = rig.Decoder.Pending;
            var d = pending[rng.Next(pending.Count)];
            d.Complete(d.Kind == DecodeKind.PreviewLight && rng.Next(5) == 0 ? Reply.Nothing : Reply.Item);
            rig.Settle();
        }
        Assert.AreEqual(0, rig.Decoder.Pending.Count, "everything finished");
        int withPreview = rig.List.Keys.Count(k => rig.List.GetPhoto(k)!.Images.Preview != null);
        int withHq = rig.List.Keys.Count(k => rig.List.GetPhoto(k)!.Images.Hq != null);
        Assert.AreEqual(121, withPreview, "the whole preview window");
        Assert.AreEqual(7, withHq, "the whole HQ window");
        Assert.IsTrue(rig.Decoder.MaxPreviews <= 4 && rig.Decoder.MaxHeavy <= 2 && rig.Decoder.MaxHq <= 2 && rig.Decoder.MaxWrites <= 2,
            $"limits: {rig.Decoder.MaxPreviews}/{rig.Decoder.MaxHeavy}/{rig.Decoder.MaxHq}/{rig.Decoder.MaxWrites}");
        Assert.IsTrue(rig.Decoder.MaxHeavy > 0, "the heavy lane ran");
        Assert.IsTrue(rig.Decoder.DiskWrites.Count > 0, "fresh previews were written to the disk cache");
        Assert.AreEqual("60/200 < Cache > 60/199", rig.Statuses[^1]);
    }

    [TestMethod]
    public void Disk_writes_wait_until_no_light_preview_is_waiting()
    {
        using var rig = new Rig(photos: 30, previewHalf: 10, limits: new JobLimits(2, 1, 1, 2));
        rig.Start(15);
        rig.Only(DecodeKind.Hq, 15).Succeed();
        rig.Settle();
        int Started(DecodeKind kind) => rig.Decoder.All.Count(d => d.Kind == kind);
        while (rig.Decoder.PendingOf(DecodeKind.PreviewLight) is { Count: > 0 } light)
        {
            if (Started(DecodeKind.PreviewLight) < 21)
                Assert.AreEqual(0, Started(DecodeKind.DiskWrite), "no write while a preview waits for a slot");
            light[0].Succeed();
            rig.Settle();
        }
        Assert.IsTrue(rig.Decoder.PendingOf(DecodeKind.DiskWrite).Count > 0);
        for (int round = 0; round < 100 && rig.Decoder.Pending.Count > 0; round++)
        {
            rig.Decoder.CompleteAll();
            rig.Settle();
        }
        Assert.AreEqual(21, rig.Decoder.DiskWrites.Count, "each fresh preview written once");
        Assert.AreEqual(0, rig.Cache.Counts.NeedsDiskWrite);
    }

    // --- Results for photos that left ---

    [TestMethod]
    public void A_result_for_a_removed_photo_is_released_and_leaves_nothing_behind()
    {
        using var rig = new Rig();
        rig.Start(5);
        var preview = rig.Only(DecodeKind.PreviewLight, 6);
        var removedPhoto = rig.At(6);
        rig.Remove(6);
        var (loaded, heavy, writes, parked, _) = rig.Cache.Counts;

        preview.Complete(Reply.Nothing); // light pass found nothing: must not mark a removed photo for the heavy lane
        rig.Settle();
        Assert.AreEqual(heavy, rig.Cache.Counts.NeedsHeavy);
        Assert.IsNull(removedPhoto.Images.Preview);
        Assert.AreEqual((loaded, writes, parked), (rig.Cache.Counts.Loaded, rig.Cache.Counts.NeedsDiskWrite, rig.Cache.Counts.Parked));
    }

    [TestMethod]
    public void A_result_arriving_after_the_list_dropped_its_photo_but_before_the_cache_heard_is_not_stored()
    {
        using var rig = new Rig();
        rig.Start(5);
        var photo = rig.At(4);
        int key = rig.Key(4);
        rig.Only(DecodeKind.PreviewLight, 4).Succeed(); // queued for the prefetch thread...
        // ...when the UI thread drops the photo from the list, before the cache's Forget arrives.
        var removed = rig.List.ApplyChanges(new ListChangeBatch(p => p == photo.FilePath, null, [], null), rig.Key(5));
        rig.List.Forget(removed);
        rig.Settle();
        Assert.IsNull(photo.Images.Preview, "not stored into a photo that left the list");
        Assert.IsFalse(rig.LoadedKeys.Contains(key), "nobody is told about it");
        Assert.AreEqual(0, rig.Cache.Counts.NeedsDiskWrite);
        photo.Images.Retire();
    }

    [TestMethod]
    public void A_removed_photos_decoded_item_is_freed_not_stored()
    {
        using var rig = new Rig();
        rig.Start(5);
        var preview = rig.Only(DecodeKind.PreviewLight, 4);
        var photo = rig.At(4);
        rig.Remove(4);
        preview.Succeed();
        rig.Settle();
        Assert.IsNull(photo.Images.Preview);
        Assert.IsTrue(rig.Decoder.Created.Single().IsFreed);
    }

    [TestMethod]
    public void Forgetting_photos_clears_everything_the_cache_tracked_for_them()
    {
        using var rig = new Rig(photos: 40, previewHalf: 10, hqHalf: 1,
            script: d => d.Kind == DecodeKind.PreviewHeavy ? Reply.Nothing : d.Kind == DecodeKind.PreviewLight ? Reply.Nothing : Reply.Item);
        rig.Start(20);
        Assert.IsTrue(rig.Cache.Counts.Parked > 0 || rig.Cache.Counts.NeedsHeavy > 0);
        rig.Remove([.. Enumerable.Range(0, 40).Where(p => p != 20)]);
        var counts = rig.Cache.Counts;
        Assert.IsTrue(counts.Loaded <= 1 && counts.NeedsHeavy <= 1 && counts.Parked <= 2 && counts.NeedsDiskWrite <= 1,
            $"tracked for removed photos: {counts}");
    }

    // --- Failures ---

    [TestMethod]
    public void A_throwing_preview_decode_shows_the_error_preview_and_is_not_retried()
    {
        using var rig = new Rig(script: d => d.Kind is DecodeKind.PreviewLight ? Reply.Throw : Reply.Pending);
        rig.Start(5);
        var photo = rig.At(5);
        Assert.IsTrue(photo.Images.Preview!.IsErrorOrUndefined());
        Assert.IsTrue(rig.LoadedKeys.Contains(rig.Key(5)), "the screen is told, so it leaves the placeholder");
        int lightDecodes = rig.Decoder.All.Count(d => d.Kind == DecodeKind.PreviewLight && ReferenceEquals(d.Photo, photo));
        rig.Move(4);
        rig.Move(5);
        Assert.AreEqual(lightDecodes, rig.Decoder.All.Count(d => d.Kind == DecodeKind.PreviewLight && ReferenceEquals(d.Photo, photo)));
        Assert.IsTrue(rig.Log.Errors.Any(e => e.Exception is ScriptedFailure));
    }

    [TestMethod]
    public void A_throwing_hq_decode_shows_the_error_hq()
    {
        using var rig = new Rig(script: d => d.Kind == DecodeKind.Hq ? Reply.Throw : Reply.Item);
        rig.Start(5);
        Assert.IsTrue(rig.At(5).Images.Hq!.IsErrorOrUndefined());
        Assert.IsTrue(rig.At(4).Images.Hq!.IsErrorOrUndefined());
    }

    [TestMethod]
    public void A_throwing_disk_write_is_dropped_not_retried()
    {
        // A window covering every photo: nothing is evicted and decoded (and written) again.
        using var rig = new Rig(photos: 3, previewHalf: 2, hqHalf: 0,
            script: d => d.Kind == DecodeKind.DiskWrite ? Reply.Throw : Reply.Item);
        rig.Start(1);
        int writes = rig.Decoder.All.Count(d => d.Kind == DecodeKind.DiskWrite);
        Assert.AreEqual(3, writes);
        for (int i = 0; i < 5; i++) rig.Move(i % 3);
        Assert.AreEqual(writes, rig.Decoder.All.Count(d => d.Kind == DecodeKind.DiskWrite));
        Assert.AreEqual(0, rig.Cache.Counts.NeedsDiskWrite);
    }

    [TestMethod]
    public void A_decoder_reported_failure_is_stored_as_the_error_item_like_a_throw()
    {
        using var rig = new Rig(script: d => d.Kind == DecodeKind.Hq ? Reply.Error : Reply.Item);
        rig.Start(5);
        Assert.IsTrue(rig.At(5).Images.Hq!.IsErrorOrUndefined());
    }

    // --- Lanes, parking and retries ---

    [TestMethod]
    public void A_light_miss_goes_to_the_heavy_lane_and_a_heavy_miss_parks_until_it_is_the_centre()
    {
        using var rig = new Rig(script: d => d.Kind switch
        {
            DecodeKind.PreviewLight or DecodeKind.PreviewHeavy => Reply.Nothing,
            _ => Reply.Item,
        });
        rig.Start(5);
        var photo6 = rig.At(6);
        int Heavy() => rig.Decoder.All.Count(d => d.Kind == DecodeKind.PreviewHeavy && ReferenceEquals(d.Photo, photo6));
        Assert.AreEqual(1, Heavy(), "one heavy pass after the light miss");
        rig.Move(4);
        rig.Move(3);
        Assert.AreEqual(1, Heavy(), "a parked neighbour is not retried when the centre moves elsewhere (FMEA 18)");
        rig.Move(6);
        Assert.AreEqual(2, Heavy(), "landing on it retries it");
    }

    [TestMethod]
    public void A_deferred_neighbour_hq_parks_and_retries_with_large_allowed_when_landed_on()
    {
        using var rig = new Rig(script: d => d.Kind == DecodeKind.Hq && !d.AllowLarge ? Reply.Nothing : Reply.Item);
        rig.Start(5);
        Assert.IsNull(rig.At(6).Images.Hq, "too large for a neighbour");
        rig.Move(4);
        Assert.AreEqual(1, rig.Decoder.All.Count(d => d.Kind == DecodeKind.Hq && ReferenceEquals(d.Photo, rig.At(6))));
        rig.Move(6);
        Assert.IsNotNull(rig.At(6).Images.Hq);
        Assert.IsTrue(rig.Decoder.All.Any(d => d.Kind == DecodeKind.Hq && d.AllowLarge && ReferenceEquals(d.Photo, rig.At(6))));
    }

    [TestMethod]
    public void A_tiled_hq_is_kept_only_for_the_centre()
    {
        // Like the real decoder: a neighbour (large not allowed) gets nothing for an oversized image.
        using var rig = new Rig(script: d => d.Kind != DecodeKind.Hq ? Reply.Item : d.AllowLarge ? Reply.Tiled : Reply.Nothing);
        rig.Start(5);
        Assert.IsInstanceOfType<TiledHqDisplayItem>(rig.At(5).Images.Hq);
        var tiled = rig.At(5).Images.Hq!;
        Assert.IsNull(rig.At(4).Images.Hq, "a neighbour's large image is deferred");
        rig.Move(6);
        Assert.IsTrue(tiled.IsFreed, "the old centre's tiles go as soon as it is not the centre");
        Assert.IsInstanceOfType<TiledHqDisplayItem>(rig.At(6).Images.Hq);
    }

    // --- Cancelling ---

    [TestMethod]
    public void Moving_away_cancels_the_jobs_left_behind_and_releases_what_they_bring()
    {
        using var rig = new Rig(photos: 40, previewHalf: 2, hqHalf: 1);
        rig.Start(5);
        var old = rig.Decoder.Pending.ToList();
        rig.Move(30);
        foreach (var d in old.Where(d => !d.IsDone)) d.Succeed();
        rig.Settle();
        foreach (int pos in new[] { 3, 4, 5, 6, 7 })
        {
            Assert.IsNull(rig.At(pos).Images.Preview, $"photo {pos} left the window");
            Assert.IsNull(rig.At(pos).Images.Hq);
        }
    }

    [TestMethod]
    public void A_cancelled_large_decode_stores_nothing_and_is_retried_when_landed_on_again()
    {
        using var rig = new Rig(photos: 40, previewHalf: 2, hqHalf: 1);
        rig.Start(5);
        var large = rig.Only(DecodeKind.Hq, 5);
        rig.Move(30);
        Assert.IsTrue(large.IsDone, "the large decode observes cancellation");
        Assert.IsNull(rig.At(5).Images.Hq);
        rig.Move(5);
        Assert.AreEqual(2, rig.Decoder.All.Count(d => d.Kind == DecodeKind.Hq && ReferenceEquals(d.Photo, rig.At(5))));
    }

    [TestMethod]
    public void Moving_out_of_the_window_evicts_but_a_leased_item_survives_until_released()
    {
        using var rig = new Rig(photos: 40, previewHalf: 2, hqHalf: 1, script: _ => Reply.Item);
        rig.Start(5);
        var shown = rig.At(5).Images.Hq!;
        Assert.IsTrue(shown.TryAcquire()); // the canvas
        rig.Move(30);
        Assert.IsNull(rig.At(5).Images.Hq);
        Assert.IsFalse(shown.IsFreed);
        shown.Release();
        Assert.IsTrue(shown.IsFreed);
    }

    // --- Holding HQ ---

    [TestMethod]
    public void A_burst_holds_hq_but_not_previews_until_resumed()
    {
        using var rig = new Rig();
        rig.Cache.HoldHq();
        rig.Start(5);
        Assert.AreEqual(0, rig.Decoder.PendingOf(DecodeKind.Hq).Count);
        Assert.IsTrue(rig.Decoder.PendingOf(DecodeKind.PreviewLight).Count > 0);
        rig.Cache.ResumeHq();
        rig.Settle();
        Assert.AreSame(rig.At(5), rig.Decoder.PendingOf(DecodeKind.Hq).Single().Photo);
    }

    [TestMethod]
    public void The_folder_switch_quiet_period_holds_hq_until_the_clock_passes_it()
    {
        using var rig = new Rig();
        rig.Cache.HoldHqFor(500);
        rig.Start(5);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(499));
        rig.Cache.Replan();
        rig.Settle();
        Assert.AreEqual(0, rig.Decoder.PendingOf(DecodeKind.Hq).Count, "still quiet at 499 ms");
        rig.Cache.HoldHqFor(500); // another switch extends it
        rig.Clock.Advance(TimeSpan.FromMilliseconds(400));
        rig.Cache.Replan();
        rig.Settle();
        Assert.AreEqual(0, rig.Decoder.PendingOf(DecodeKind.Hq).Count, "extended by the second switch");
        rig.Clock.Advance(TimeSpan.FromMilliseconds(101));
        rig.Cache.Replan();
        rig.Settle();
        Assert.AreEqual(1, rig.Decoder.PendingOf(DecodeKind.Hq).Count);
    }

    [TestMethod]
    public void A_shorter_hold_never_cuts_a_longer_one_short()
    {
        using var rig = new Rig();
        rig.Cache.HoldHqFor(1000);
        rig.Cache.HoldHqFor(10);
        rig.Start(5);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        rig.Cache.Replan();
        rig.Settle();
        Assert.AreEqual(0, rig.Decoder.PendingOf(DecodeKind.Hq).Count);
    }

    // --- Settings ---

    [TestMethod]
    public void A_smaller_window_setting_evicts_at_once_on_replan()
    {
        using var rig = new Rig(photos: 30, previewHalf: 10, hqHalf: 2, script: _ => Reply.Item);
        rig.Start(15);
        AppConfig.Settings.CacheSizeOneSidePreviews = 1;
        AppConfig.Settings.CacheSizeOneSideHqImages = 0;
        rig.Cache.Replan();
        rig.Settle();
        Assert.AreEqual(3, rig.List.Keys.Count(k => rig.List.GetPhoto(k)!.Images.Preview != null));
        Assert.AreEqual(1, rig.List.Keys.Count(k => rig.List.GetPhoto(k)!.Images.Hq != null));
    }

    [TestMethod]
    public void A_raw_setting_change_reloads_raw_hqs_and_drops_raw_decodes_in_flight()
    {
        if (!new Photo(@"c:\x\a.cr2").IsRaw) Assert.Inconclusive("no RAW codec registers .cr2 on this machine");
        using var rig = new Rig(extension: ".cr2",
            script: d => d.Kind == DecodeKind.Hq && d.Path.EndsWith("00006.cr2") ? Reply.Pending : Reply.Item);
        rig.Start(5);
        var oldHq = rig.At(5).Images.Hq!;
        var inFlight = rig.Only(DecodeKind.Hq, 6);
        rig.Cache.InvalidateHqForRawFiles();
        rig.Settle();
        Assert.IsTrue(oldHq.IsFreed);
        Assert.IsNotNull(rig.At(5).Images.Hq);
        Assert.AreNotSame(oldHq, rig.At(5).Images.Hq, "decoded again with the new settings");
        inFlight.Succeed();
        rig.Settle();
        Assert.IsTrue(rig.Decoder.Created.Where(i => !i.IsFreed).All(i =>
            rig.List.Keys.Any(k => ReferenceEquals(rig.List.GetPhoto(k)!.Images.Hq, i) || ReferenceEquals(rig.List.GetPhoto(k)!.Images.Preview, i))));
    }

    // --- Resilience ---

    [TestMethod]
    public void A_listener_that_throws_does_not_stop_loading()
    {
        using var rig = new Rig(script: _ => Reply.Item);
        int calls = 0;
        rig.Cache.Loaded += _ => { if (++calls == 1) throw new InvalidOperationException("listener bug"); };
        rig.Start(5);
        Assert.IsTrue(calls > 5, "later loads still announced");
        Assert.IsNotNull(rig.At(6).Images.Preview);
        Assert.AreEqual(1, rig.Log.Errors.Count(e => e.Exception?.Message == "listener bug"));
    }

    [TestMethod]
    public void A_decode_that_never_returns_holds_only_its_own_slot()
    {
        using var rig = new Rig(photos: 30, previewHalf: 5, hqHalf: 0, limits: new JobLimits(2, 1, 1, 1),
            script: d => d.Kind == DecodeKind.PreviewLight && d.Path.EndsWith("00010.jpg") ? Reply.Hang : Reply.Item);
        rig.Start(10);
        Assert.AreEqual(10, rig.List.Keys.Count(k => rig.List.GetPhoto(k)!.Images.Preview != null),
            "the other photos load around the stuck one, through the remaining slot");
    }

    // --- Shutdown ---

    [TestMethod]
    public void Decodes_that_return_after_dispose_are_released()
    {
        var rig = new Rig();
        rig.Start(5);
        var pending = rig.Decoder.Pending.ToList();
        Assert.IsTrue(pending.Count > 1);
        pending[0].Succeed(); // returned, its result queued but never stored
        rig.Cache.Dispose();
        foreach (var d in pending.Skip(1)) d.Succeed(); // returned after dispose
        rig.Settle(); // a step after dispose does nothing
        Assert.IsTrue(rig.Decoder.Created.All(i => i.IsFreed), "every late result released");
        rig.Dispose();
    }

    [TestMethod]
    public void Messages_after_dispose_are_harmless()
    {
        var rig = new Rig();
        rig.Start(5);
        rig.Cache.Dispose();
        rig.Cache.MoveWindow(rig.Nav(3));
        rig.Cache.HoldHq();
        rig.Cache.Replan();
        rig.Cache.Dispose();
        rig.Dispose();
    }

    [TestMethod]
    public void Dispose_gives_up_on_a_prefetch_thread_that_does_not_stop_and_the_thread_ends_by_itself()
    {
        // Not disposed through the rig: its leak check doesn't apply here. Results that reach the prefetch thread
        // after Dispose gave up on it are not released (the accepted ceiling of this path).
        var rig = new Rig(photos: 20, ownThread: true, script: _ => Reply.Item);
        using var stuck = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        rig.Cache.Loaded += _ =>
        {
            entered.Set();
            stuck.Wait(); // the prefetch thread hangs in a listener
        };
        rig.Cache.Start(rig.Nav(5));
        Assert.IsTrue(entered.Wait(5_000), "the prefetch thread stored something");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        rig.Cache.Dispose(); // must not throw or wait forever
        Assert.IsTrue(watch.ElapsedMilliseconds >= 1_900, $"waited {watch.ElapsedMilliseconds} ms for the thread");
        Assert.IsTrue(watch.ElapsedMilliseconds < 10_000);

        stuck.Set();
        Thread.Sleep(300); // the thread finishes its step, sees the stop and ends
        int decodes = rig.Decoder.All.Count;
        Thread.Sleep(300);
        Assert.AreEqual(decodes, rig.Decoder.All.Count, "nothing more is loaded once the thread ended");
        Assert.AreEqual(0, rig.Log.Errors.Count, string.Join("\n", rig.Log.Errors.Select(e => $"{e.Message} {e.Exception}")));
        rig.Log.Dispose();
    }

    [TestMethod]
    public void The_prefetch_thread_fills_the_window_while_the_viewer_races_through()
    {
        using var rig = new Rig(photos: 500, previewHalf: 20, hqHalf: 2, ownThread: true, script: _ => Reply.Async,
            limits: new JobLimits(3, 2, 2, 2));
        rig.Cache.Start(rig.Nav(0));
        for (int pos = 0; pos < 300; pos++) rig.Cache.MoveWindow(rig.Nav(pos));
        var centre = rig.At(299);
        var deadline = Environment.TickCount64 + 20_000;
        while ((centre.Images.Hq == null || rig.Decoder.InFlight > 0) && Environment.TickCount64 < deadline) Thread.Sleep(5);
        Assert.IsNotNull(centre.Images.Hq, "the photo the viewer stopped on gets its HQ");
        Assert.IsTrue(rig.Decoder.MaxPreviews <= 3 && rig.Decoder.MaxHq <= 2 && rig.Decoder.MaxHeavy <= 2);
        Assert.AreEqual(0, rig.Log.Errors.Count, string.Join("\n", rig.Log.Errors.Select(e => e.Exception?.ToString())));
    }
}
