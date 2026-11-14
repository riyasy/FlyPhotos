using FlyPhotos.Core.Model;
using FlyPhotos.Tests.Display.Harness;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// A photo's image slots and the leases on their items, with real Win2D bitmaps: who frees an item, when, and that
/// nothing frees one that is still drawn or read.
/// </summary>
[TestClass]
public sealed class DisplaySlotsTests
{
    private static StaticHqDisplayItem Hq() => new(TestGpu.Bitmap(), Origin.Disk);
    private static PreviewDisplayItem Preview(Origin origin = Origin.Disk) => new(TestGpu.Bitmap(), origin);

    [TestMethod]
    public void A_stored_item_lives_until_detached_and_released()
    {
        var slots = new DisplaySlots();
        var hq = Hq();
        Assert.IsTrue(slots.TryAttachHq(hq));
        Assert.AreSame(hq, slots.Hq);
        Assert.IsFalse(hq.IsFreed);

        var detached = slots.DetachHq();
        Assert.AreSame(hq, detached);
        Assert.IsNull(slots.Hq);
        Assert.IsFalse(hq.IsFreed, "detaching hands the lease over, it does not end it");
        detached!.Release();
        Assert.IsTrue(hq.IsFreed);
    }

    [TestMethod]
    public void A_lease_keeps_an_item_alive_after_its_slot_lets_go()
    {
        var slots = new DisplaySlots();
        var hq = Hq();
        slots.TryAttachHq(hq);
        Assert.IsTrue(hq.TryAcquire()); // the canvas

        slots.DetachHq()!.Release();    // the cache evicts it
        Assert.IsFalse(hq.IsFreed);
        Assert.AreEqual(2u, hq.Bitmap.SizeInPixels.Width, "the bitmap is still usable");

        hq.Release();                     // the canvas moves on
        Assert.IsTrue(hq.IsFreed);
        Assert.IsFalse(hq.TryAcquire(), "a freed item can't be leased again");
    }

    [TestMethod]
    public void Storing_into_a_retired_photo_releases_the_item()
    {
        var slots = new DisplaySlots();
        slots.Retire();
        var hq = Hq();
        var preview = Preview();
        Assert.IsFalse(slots.TryAttachHq(hq));
        Assert.IsFalse(slots.TryAttachPreview(preview, new Thumbnail([])));
        Assert.IsTrue(hq.IsFreed);
        Assert.IsTrue(preview.IsFreed);
        Assert.IsNull(slots.Hq);
        Assert.IsNull(slots.Preview);
        Assert.IsNull(slots.Thumbnail);
    }

    [TestMethod]
    public void Retire_releases_everything_once_however_often_it_is_called()
    {
        var slots = new DisplaySlots();
        var hq = Hq();
        var preview = Preview();
        slots.TryAttachHq(hq);
        slots.TryAttachPreview(preview, new Thumbnail([]));
        Assert.IsTrue(preview.TryAcquire()); // someone still reads it

        slots.Retire();
        slots.Retire();
        Assert.IsTrue(hq.IsFreed);
        Assert.IsFalse(preview.IsFreed, "the reader's lease survives the retire");
        Assert.IsNull(slots.Thumbnail);
        preview.Release();
        Assert.IsTrue(preview.IsFreed);
    }

    [TestMethod]
    public void Storing_over_a_filled_slot_releases_the_replaced_item()
    {
        var slots = new DisplaySlots();
        var first = Preview();
        var second = Preview();
        slots.TryAttachPreview(first, null);
        slots.TryAttachPreview(second, null);
        Assert.IsTrue(first.IsFreed, "the replaced item is not leaked");
        Assert.AreSame(second, slots.Preview);

        var hq1 = Hq();
        var hq2 = Hq();
        slots.TryAttachHq(hq1);
        slots.TryAttachHq(hq2);
        Assert.IsTrue(hq1.IsFreed);
        Assert.IsFalse(hq2.IsFreed);
    }

    [TestMethod]
    public void Storing_the_same_item_twice_keeps_exactly_one_lease()
    {
        var slots = new DisplaySlots();
        var hq = Hq();
        slots.TryAttachHq(hq);
        Assert.IsTrue(hq.TryAcquire()); // the second caller's own lease, handed to the slot
        slots.TryAttachHq(hq);
        slots.DetachHq()!.Release();
        Assert.IsTrue(hq.IsFreed, "one lease for one slot");
    }

    [TestMethod]
    public void A_preview_without_a_thumbnail_keeps_the_old_thumbnail_and_detach_drops_it()
    {
        var slots = new DisplaySlots();
        var thumb = new Thumbnail([1]);
        slots.TryAttachPreview(Preview(), thumb);
        slots.TryAttachPreview(Preview(), null);
        Assert.AreSame(thumb, slots.Thumbnail);
        slots.DetachPreview()!.Release();
        Assert.IsNull(slots.Thumbnail);
    }

    [TestMethod]
    public void Error_and_placeholder_items_are_shared_and_never_freed()
    {
        var bitmap = TestGpu.Bitmap();
        var error = new StaticHqDisplayItem(bitmap, Origin.ErrorScreen);
        var undefined = new PreviewDisplayItem(bitmap, Origin.Undefined);
        for (int i = 0; i < 5; i++) { error.Release(); undefined.Release(); }
        Assert.IsFalse(error.IsFreed);
        Assert.IsTrue(error.TryAcquire());
        Assert.IsTrue(undefined.TryAcquire());

        var slots = new DisplaySlots();
        slots.TryAttachPreview(new PreviewDisplayItem(bitmap, Origin.ErrorScreen), null);
        slots.TryAttachPreview(Preview(), null); // replacing an error preview frees nothing shared
        Assert.AreEqual(2u, bitmap.SizeInPixels.Width, "the shared bitmap is not disposed");
    }

    [TestMethod]
    public void The_placeholder_is_not_a_stored_level()
    {
        var slots = new DisplaySlots();
        slots.TryAttachPreview(Preview(), null);
        Assert.IsNull(slots.Get(DisplayLevel.PlaceHolder));
        Assert.IsTrue(slots.IsErrorScreen(DisplayLevel.PlaceHolder));
        Assert.IsFalse(slots.IsErrorScreen(DisplayLevel.Preview));
        Assert.IsTrue(slots.IsErrorScreen(DisplayLevel.Hq), "no HQ yet");
    }

    [TestMethod]
    public void Sizes_come_from_the_best_item_and_skip_an_error_hq_for_the_source_size()
    {
        var slots = new DisplaySlots();
        Assert.AreEqual((100.0, 100.0), slots.GetActualSize(), "nothing loaded: a default");
        slots.TryAttachPreview(new PreviewDisplayItem(TestGpu.Bitmap(), Origin.DiskCache, new ImageMetadata(4000, 3000)), null);
        Assert.AreEqual((4000.0, 3000.0), slots.GetActualSize(), "the preview's metadata gives the full size");

        slots.TryAttachHq(new StaticHqDisplayItem(TestGpu.Bitmap(), Origin.ErrorScreen));
        Assert.AreEqual((2.0, 2.0), slots.GetActualSize(), "what is on screen: the error icon");
        Assert.AreEqual((4000.0, 3000.0), slots.GetSourcePixelSize(), "the real size skips the error item");
    }

    [TestMethod]
    public void A_size_read_of_a_freed_item_falls_back_instead_of_touching_it()
    {
        var slots = new DisplaySlots();
        var hq = Hq();
        slots.TryAttachHq(hq);
        slots.TryAttachPreview(new PreviewDisplayItem(TestGpu.Bitmap(), Origin.DiskCache, new ImageMetadata(10, 20)), null);
        hq.Release(); // freed behind the slot's back: the read must not use it
        Assert.AreEqual((10.0, 20.0), slots.GetActualSize());
    }

    [TestMethod]
    public void Known_oversized_comes_from_an_earlier_load_or_the_preview_metadata()
    {
        var slots = new DisplaySlots();
        Assert.IsFalse(slots.IsKnownOversized(16384));
        slots.TryAttachPreview(new PreviewDisplayItem(TestGpu.Bitmap(), Origin.DiskCache, new ImageMetadata(20000, 100)), null);
        Assert.IsTrue(slots.IsKnownOversized(16384));
        Assert.IsFalse(slots.IsKnownOversized(20000), "exactly the maximum fits");
        var other = new DisplaySlots { KnownOversized = true };
        Assert.IsTrue(other.IsKnownOversized(16384));
    }

    /// <summary>
    /// The race the leases exist for: readers lease and use the item on screen while the cache evicts and replaces
    /// it from another thread. No reader may ever hold a freed item, and every item is freed exactly once at the end.
    /// </summary>
    [TestMethod]
    public void Readers_never_see_a_freed_item_while_the_cache_evicts_and_replaces_it()
    {
        var slots = new DisplaySlots();
        var made = new System.Collections.Concurrent.ConcurrentQueue<HqDisplayItem>();
        int violations = 0;
        using var stop = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 6).Select(reader => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (slots.Hq is not { } item || !item.TryAcquire()) continue;
                try
                {
                    _ = item.Bitmap.SizeInPixels; // a freed bitmap throws
                    _ = slots.GetActualSize();
                }
                catch (ObjectDisposedException) { Interlocked.Increment(ref violations); }
                item.Release();
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 3000; i++)
            {
                var hq = Hq();
                made.Enqueue(hq);
                slots.TryAttachHq(hq);
                if (i % 3 == 0) slots.DetachHq()?.Release();
            }
        });

        writer.Wait(TimeSpan.FromSeconds(60));
        stop.Cancel();
        Task.WaitAll(readers, TimeSpan.FromSeconds(30));
        slots.Retire();

        Assert.AreEqual(0, violations);
        Assert.IsTrue(made.All(i => i.IsFreed), "every item freed once the slot and readers let go");
    }
}
