using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The controller over the whole pipeline (real list and prefetch cache, scripted decoder, recording canvas): Fly's
/// promise that every key press shows something at once, the upgrade rules, bursts, edges, list changes, failures
/// and shutdown. Every test ends with the harness's leak check.
/// </summary>
[TestClass]
public sealed class PhotoDisplayControllerTests
{
    // --- Opening ---

    [TestMethod]
    public void Opening_shows_the_preview_then_the_hq_then_publishes_the_listing()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 7);
        string? firstDetails = null;
        bool firstLoaded = false;
        h.Controller.FileNameOrDetailsChanged += d => firstDetails ??= d.DisplayText;
        h.Controller.FirstPhotoLoaded += () => firstLoaded = true;
        h.Open();

        Assert.AreEqual(DisplayLevel.Preview, h.Canvas.History[0].Level);
        Assert.AreEqual(DisplayLevel.Hq, h.Canvas.History[1].Level);
        Assert.IsTrue(h.Canvas.History.All(i => ReferenceEquals(i.Photo, h.Current)));
        Assert.AreEqual(7, h.Nav.Position);
        Assert.AreEqual(20, h.Nav.Keys.Count);
        Assert.IsTrue(firstLoaded);
        Assert.IsNotNull(firstDetails);
        Assert.AreEqual(1, h.Decoder.WarmUps, "the disk cache is opened once, before the listing");
        Assert.AreEqual(1, h.Decoder.All.Count(d => d.Kind == DecodeKind.Hq && ReferenceEquals(d.Photo, h.Current)),
            "the first photo's HQ is loaded once, and kept (not decoded again by the cache)");
        h.CheckInvariants();
    }

    [TestMethod]
    public void The_filename_bar_shows_position_and_size_once_the_listing_is_in()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 3);
        var details = new List<FileDisplayDetails>();
        h.Controller.FileNameOrDetailsChanged += details.Add;
        h.Open();
        h.Next();
        Assert.AreEqual("[5/20] img_00004.jpg (2 x 2)", details[^1].DisplayText);
    }

    [TestMethod]
    public void A_first_photo_that_throws_shows_the_error_screen_and_the_viewer_still_works()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 4,
            script: d => d.Kind == DecodeKind.FirstPreview ? Reply.Throw : Reply.Item);
        h.Open();
        Assert.IsTrue(h.Canvas.History.Last(i => ReferenceEquals(i.Photo, h.Current)).Item.IsErrorOrUndefined());
        Assert.IsTrue(h.Controller.ListingPublished);
        h.Next();
        h.Settle();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_first_photo_with_nothing_quick_shows_the_placeholder_and_upgrades_when_it_loads()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 0,
            script: d => d.Kind switch { DecodeKind.FirstPreview => Reply.Nothing, DecodeKind.Hq => Reply.Pending, _ => Reply.Item });
        var first = h.Controller.LoadFirstPhoto();
        h.RunUntil(() => h.Canvas.History.Count > 0, "something on screen");
        Assert.AreEqual(DisplayLevel.PlaceHolder, h.ShownLevel);
        h.RunUntil(() => first.IsCompleted && h.Controller.ListingPublished, "the listing");
        h.Settle();
        Assert.AreEqual(DisplayLevel.Preview, h.ShownLevel, "the cache's preview replaced the placeholder");
        h.Decoder.CompleteAll();
        h.Settle();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_file_whose_folder_is_gone_opens_alone()
    {
        using var h = new DisplayHarness(photos: 3, beforeStart: folder => Directory.Delete(folder, recursive: true));
        h.Open();
        Assert.AreEqual(1, h.Nav.Keys.Count);
        h.Next();
        Assert.AreEqual(0, h.Strip.Bounces.Count, "a single photo neither moves nor bounces");
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_secondary_instance_shows_only_the_opened_photo_and_loads_nothing_else()
    {
        AppConfig.Volatile.IsSecondaryInstance = true;
        try
        {
            using var h = new DisplayHarness(photos: 10, openAt: 4);
            h.Open();
            Assert.AreEqual(1, h.Nav.Keys.Count, "no folder listing");
            Assert.AreEqual("img_00004.jpg", h.CurrentName);
            Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);

            h.Next();
            h.Settle();
            Assert.AreEqual("img_00004.jpg", h.CurrentName);
            Assert.AreEqual(0, h.Strip.Bounces.Count);
            Assert.IsTrue(h.Decoder.All.All(d => d.Path == h.Current.FilePath),
                "nothing else is decoded: the prefetch cache never starts");
            h.CheckInvariants();
        }
        finally
        {
            AppConfig.Volatile.IsSecondaryInstance = false;
        }
    }

    // --- Every key press shows something ---

    [TestMethod]
    public void Every_key_press_shows_the_new_photo_at_once_even_with_the_decoder_frozen()
    {
        using var h = new DisplayHarness(photos: 50, openAt: 0);
        h.Open();
        h.Decoder.Script = _ => Reply.Hang; // nothing ever loads again
        for (int i = 1; i <= 30; i++)
        {
            int installs = h.Canvas.History.Count;
            h.Next();
            Assert.AreEqual(installs + 1, h.Canvas.History.Count, $"press {i} installed something");
            Assert.AreSame(h.Current, h.Canvas.HeldPhoto);
            Assert.AreEqual(i, h.Nav.Position);
            h.Settle();
        }
        Assert.AreEqual(DisplayLevel.PlaceHolder, h.ShownLevel);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_key_press_shows_the_best_level_already_loaded()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 5);
        h.Open();
        h.Next();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel, "the neighbour's HQ was prefetched");
        h.Run(() => h.Controller.FlyBy(4));
        Assert.AreEqual(DisplayLevel.Preview, h.ShownLevel, "beyond the HQ window, inside the preview window");
        h.Settle();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel, "upgraded once its HQ loaded");
        h.CheckInvariants();
    }

    [TestMethod]
    public void An_item_freed_before_it_could_be_leased_falls_back_a_level()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 5);
        h.Open();
        var next = h.PhotoAt(6);
        var stale = new StaticHqDisplayItem(TestGpu.Bitmap(), Origin.Disk);
        stale.Release(); // freed
        next.Images.DetachHq()?.Release();
        next.Images.TryAttachHq(stale); // what the slot held when the cache freed it a moment ago
        h.Decoder.Script = _ => Reply.Pending;
        h.Next();
        next.Images.DetachHq(); // take the freed item back out before anything releases it again
        Assert.AreEqual(DisplayLevel.Preview, h.ShownLevel);
        Assert.AreEqual(0, h.Canvas.Violations.Count);
    }

    [TestMethod]
    public void The_screen_never_steps_down_for_the_same_photo()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 5, script: d => d.Kind == DecodeKind.PreviewLight ? Reply.Pending : Reply.Item);
        h.Open();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        int installs = h.Canvas.History.Count;
        h.Decoder.CompleteAll(); // previews arrive after the HQ
        h.Settle();
        Assert.AreEqual(installs, h.Canvas.History.Count, "a preview arriving later doesn't replace the HQ");
        h.Next();
        h.Settle();
        var shown = h.Canvas.History.Where(i => ReferenceEquals(i.Photo, h.Current)).Select(i => i.Level).ToList();
        CollectionAssert.AreEqual(shown.OrderBy(l => l).ToList(), shown, "levels only go up");
        h.CheckInvariants();
    }

    // --- Bursts ---

    [TestMethod]
    public void A_held_key_holds_hq_loading_and_letting_go_loads_the_photo_it_stopped_on()
    {
        using var h = new DisplayHarness(photos: 100, openAt: 0, previewHalf: 3, hqHalf: 1);
        h.Open();
        h.Decoder.Script = d => d.Kind == DecodeKind.Hq ? Reply.Pending : Reply.Item;
        int hqsBefore = h.Decoder.All.Count(d => d.Kind == DecodeKind.Hq);
        h.Next();
        h.Settle();
        int hqsAfterOnePress = h.Decoder.All.Count(d => d.Kind == DecodeKind.Hq);
        for (int i = 0; i < 39; i++) { h.Next(); h.Settle(); }
        Assert.AreEqual(hqsAfterOnePress, h.Decoder.All.Count(d => d.Kind == DecodeKind.Hq), "no HQ starts during the burst");
        Assert.IsTrue(hqsAfterOnePress > hqsBefore, "a single press is not a burst");
        Assert.AreEqual(DisplayLevel.Preview, h.ShownLevel);

        h.Brake();
        h.Settle();
        var centreHq = h.Decoder.PendingOf(DecodeKind.Hq).SingleOrDefault(d => ReferenceEquals(d.Photo, h.Current));
        Assert.IsNotNull(centreHq, "letting go starts the HQ of the photo on screen");
        centreHq.Succeed();
        h.Settle();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        h.CheckInvariants();
    }

    [TestMethod]
    public void Presses_on_a_single_photo_never_open_a_burst()
    {
        using var h = new DisplayHarness(photos: 1);
        h.Open();
        for (int i = 0; i < 5; i++) h.Next();
        Assert.IsTrue(h.Controller.CanDeleteCurrentPhoto(), "no burst: delete stays available");
        Assert.IsTrue(h.Controller.IsSinglePhoto());
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void Delete_and_rename_wait_for_the_burst_to_end_and_the_hq()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 0);
        h.Open();
        Assert.IsTrue(h.Controller.CanDeleteCurrentPhoto());
        h.Next();
        h.Next();
        Assert.IsFalse(h.Controller.CanDeleteCurrentPhoto(), "mid-burst");
        Assert.IsFalse(h.Controller.CanRenameCurrentPhoto());
        h.Brake();
        h.Settle();
        Assert.IsTrue(h.Controller.CanRenameCurrentPhoto());
    }

    // --- Edges and jumps ---

    [TestMethod]
    public void Edges_bounce_without_installing_anything()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 0);
        h.Open();
        int installs = h.Canvas.History.Count;
        h.Prev();
        CollectionAssert.AreEqual(new[] { NavDirection.Prev }, h.Strip.Bounces);
        Assert.AreEqual(installs, h.Canvas.History.Count);
        h.Run(h.Controller.FlyToFirst);
        Assert.AreEqual(2, h.Strip.Bounces.Count, "Home on the first photo bounces too");
        h.Run(h.Controller.FlyToLast);
        Assert.AreEqual(4, h.Nav.Position);
        h.Next();
        Assert.AreEqual(NavDirection.Next, h.Strip.Bounces[^1]);
        Assert.AreEqual(4, h.Nav.Position);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void Big_jumps_clamp_to_the_ends()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 5);
        h.Open();
        h.Run(() => h.Controller.FlyBy(int.MaxValue));
        Assert.AreEqual(9, h.Nav.Position);
        h.Run(() => h.Controller.FlyBy(int.MinValue));
        Assert.AreEqual(0, h.Nav.Position);
        h.Run(() => h.Controller.FlyBy(0));
        Assert.AreEqual(0, h.Nav.Position);
        h.Settle();
        h.CheckInvariants();
    }

    // --- The canvas failing ---

    [TestMethod]
    public void A_canvas_that_cannot_draw_an_item_gets_the_placeholder_and_is_not_retried_for_that_item()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 0);
        h.Open();
        h.Decoder.Script = d => d.Kind == DecodeKind.Hq ? Reply.Pending : Reply.Item;
        h.Run(() => h.Controller.FlyBy(5)); // past the HQ window: its HQ is still to load
        h.Settle();
        var current = h.Current;
        h.Canvas.Fails = item => ReferenceEquals(item, current.Images.Hq);
        var hq = h.Decoder.PendingOf(DecodeKind.Hq).Single(d => ReferenceEquals(d.Photo, current));
        hq.Succeed();
        h.Settle();
        Assert.AreEqual(1, h.Canvas.Failures);
        Assert.AreEqual(DisplayLevel.PlaceHolder, h.ShownLevel, "the placeholder, not the previous photo");
        Assert.AreSame(current, h.Canvas.HeldPhoto);
        h.Decoder.CompleteAll();
        h.Settle();
        Assert.AreEqual(1, h.Canvas.Failures, "later loads don't retry the item that failed");

        h.Canvas.Fails = null;
        h.Next();
        h.Prev();
        h.Settle();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel, "coming back tries again");
        Assert.IsTrue(h.Log.Errors.Any(e => e.Message.Contains("failed")));
        h.CheckInvariants();
    }

    // --- List changes ---

    [TestMethod]
    public void Removing_the_photo_on_screen_shows_the_next_one_and_frees_the_removed_images()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 4);
        h.Open();
        var removed = h.Current;
        var removedHq = removed.Images.Hq!;
        h.Remove(removed.FilePath);
        Assert.AreEqual("img_00005.jpg", h.CurrentName);
        Assert.AreEqual(4, h.Nav.Position);
        h.Settle();
        Assert.IsTrue(removedHq.IsFreed, "retired, and the canvas let go when the next photo replaced it");
        h.CheckInvariants();
    }

    [TestMethod]
    public void Removing_the_last_photo_on_screen_shows_the_one_before()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 4);
        h.Open();
        h.Remove(h.Current.FilePath);
        Assert.AreEqual("img_00003.jpg", h.CurrentName);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void Changes_elsewhere_keep_the_same_photo_on_screen_without_reinstalling_it()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 5);
        h.Open();
        int installs = h.Canvas.History.Count;
        int redraws = h.Strip.RedrawRequests;
        h.Remove(h.PathOf(0), h.PathOf(1), h.PathOf(9));
        Assert.AreEqual("img_00005.jpg", h.CurrentName);
        Assert.AreEqual(3, h.Nav.Position);
        h.Add(Path.Combine(h.PhotoFolder, "img_00000a.jpg"));
        Assert.AreEqual(4, h.Nav.Position);
        Assert.AreEqual(installs, h.Canvas.History.Count);
        Assert.IsTrue(h.Strip.RedrawRequests > redraws);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void Removing_every_other_photo_leaves_the_one_on_screen()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 3);
        h.Open();
        h.Remove([.. Enumerable.Range(0, 10).Select(h.PathOf)]);
        Assert.AreEqual(1, h.Nav.Keys.Count);
        Assert.AreEqual("img_00003.jpg", h.CurrentName);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_decode_in_flight_for_a_removed_photo_is_freed_when_it_returns()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 5);
        h.Open();
        h.Decoder.Script = _ => Reply.Pending;
        h.Run(() => h.Controller.FlyBy(3));
        h.Settle();
        var target = h.Current; // its HQ is decoding
        var pending = h.Decoder.Pending.Where(d => ReferenceEquals(d.Photo, target)).ToList();
        Assert.IsTrue(pending.Count > 0);
        h.Remove(target.FilePath);
        foreach (var d in pending) d.Succeed();
        h.Settle();
        Assert.IsNull(target.Images.Preview);
        Assert.IsNull(target.Images.Hq);
        h.Decoder.CompleteAll();
        h.Settle();
        h.CheckInvariants();
    }

    // --- Settings ---

    [TestMethod]
    public void A_smaller_cache_setting_takes_effect_at_once()
    {
        using var h = new DisplayHarness(photos: 40, openAt: 20, previewHalf: 10, hqHalf: 3);
        h.Open();
        AppConfig.Settings.CacheSizeOneSidePreviews = 2;
        AppConfig.Settings.CacheSizeOneSideHqImages = 0;
        h.Controller.CacheSizeChanged();
        h.Settle();
        Assert.AreEqual(5, h.Nav.Keys.Count(k => h.Controller.Photos.GetPhoto(k)!.Images.Preview != null));
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_raw_setting_change_keeps_the_raw_on_screen_until_the_new_decode_replaces_it()
    {
        if (!new Photo(@"c:\x\a.cr2").IsRaw) Assert.Inconclusive("no RAW codec registers .cr2 on this machine");
        using var h = new DisplayHarness(photos: 6, openAt: 2, extension: ".cr2");
        h.Open();
        var old = h.Canvas.Held!;
        h.Decoder.Script = d => d.Kind == DecodeKind.Hq ? Reply.Pending : Reply.Item;
        h.Controller.InvalidateRawHqCache();
        h.Settle();
        Assert.AreSame(old, h.Canvas.Held, "no step down to the preview meanwhile");
        Assert.IsFalse(old.IsFreed, "the canvas still holds it");
        h.Decoder.PendingOf(DecodeKind.Hq).Single(d => ReferenceEquals(d.Photo, h.Current)).Succeed();
        h.Settle();
        Assert.AreNotSame(old, h.Canvas.Held);
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        Assert.IsTrue(old.IsFreed);
        h.Decoder.CompleteAll();
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void The_cache_status_reaches_the_window()
    {
        using var h = new DisplayHarness(photos: 20, openAt: 10, previewHalf: 3);
        var statuses = new List<string>();
        h.Controller.CacheStatusChanged += statuses.Add;
        h.Open();
        Assert.AreEqual("3/10 < Cache > 3/9", statuses[^1]);
    }

    // --- Shutdown ---

    [TestMethod]
    public void Shutting_down_with_decodes_in_flight_frees_everything()
    {
        var h = new DisplayHarness(photos: 30, openAt: 10);
        h.Open();
        h.Decoder.Script = _ => Reply.Pending;
        for (int i = 0; i < 5; i++) h.Next();
        h.Settle();
        Assert.IsTrue(h.Decoder.Pending.Count > 0);
        int installs = h.Canvas.History.Count;
        h.Dispose(); // completes the pending decodes after the controller is gone; asserts nothing leaked
        Assert.AreEqual(installs, h.Canvas.History.Count, "nothing reaches the canvas after shutdown");
        h.Controller.Dispose(); // twice is harmless
    }

    [TestMethod]
    public void Shutting_down_before_the_listing_arrives_is_clean()
    {
        var h = new DisplayHarness(photos: 30, openAt: 10, script: d => d.Kind == DecodeKind.FirstPreview ? Reply.Pending : Reply.Item);
        var first = h.Controller.LoadFirstPhoto();
        h.Ui.RunPending();
        h.Dispose();
        Assert.IsFalse(h.Controller.ListingPublished);
    }
}
