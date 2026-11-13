using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// Folder mode end to end on real temp folders: the watcher's adds and deletes reach the screen, a rename moves the
/// photo with the viewer, and switching to a sibling folder holds HQ loading for the quiet period.
/// </summary>
[TestClass]
public sealed class FolderModeTests
{
    [TestMethod]
    public void Files_added_and_deleted_on_disk_reach_the_list_and_the_viewer_stays_on_its_photo()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 2, source: ListSource.Folder);
        h.Open();
        File.WriteAllBytes(Path.Combine(h.PhotoFolder, "img_00002b.jpg"), []);
        h.RunUntil(() => h.Nav.Keys.Count == 6, "the watcher's add");
        Assert.AreEqual("img_00002.jpg", h.CurrentName);

        File.Delete(h.PathOf(0));
        h.RunUntil(() => h.Nav.Keys.Count == 5, "the watcher's delete");
        Assert.AreEqual("img_00002.jpg", h.CurrentName);
        Assert.AreEqual(1, h.Nav.Position);

        var shown = h.Current;
        File.Delete(shown.FilePath);
        h.RunUntil(() => !ReferenceEquals(h.Current, shown), "the deleted photo on screen to be replaced");
        Assert.AreEqual("img_00002b.jpg", h.CurrentName, "the next one in sort order");
        h.Settle();
        Assert.IsNull(shown.Images.Hq, "the deleted photo's images are released");
        h.CheckInvariants();
    }

    [TestMethod]
    public void Renaming_in_fly_moves_the_photo_and_the_viewer_with_it()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 1, source: ListSource.Folder);
        h.Open();
        var photo = h.Current;
        RenameResult? result = null;
        h.Run(async () => result = await h.Controller.RenameCurrentPhoto("zzz.jpg"));
        Assert.IsTrue(result!.Success, result.FailMessage);
        Assert.AreSame(photo, h.Current, "the same photo, key and images");
        Assert.AreEqual("zzz.jpg", h.CurrentName);
        Assert.AreEqual(4, h.Nav.Position, "moved to its sorted place");
        Assert.IsTrue(h.Canvas.Renames.Any(r => r.New.EndsWith("zzz.jpg")));
        // The watcher reports the same rename a moment later: nothing left to do.
        Thread.Sleep(300);
        h.Settle();
        Assert.AreEqual(5, h.Nav.Keys.Count);
        h.CheckInvariants();
    }

    [TestMethod]
    public void Switching_to_a_sibling_folder_shows_its_first_photo_and_holds_hq_for_the_quiet_period()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 2, source: ListSource.Folder);
        var after = Directory.CreateDirectory(Path.Combine(h.Folder, "c_photos")).FullName;
        foreach (var n in new[] { "c1.jpg", "c2.jpg", "c3.jpg" }) File.WriteAllBytes(Path.Combine(after, n), []);
        Directory.CreateDirectory(Path.Combine(h.Folder, "a_empty")); // no images: skipped
        h.Open();
        var oldPhotos = h.Nav.Keys.Select(k => h.Controller.Photos.GetPhoto(k)!).ToList();

        string? name = null;
        h.Run(async () => name = await h.Controller.TryOpenSiblingFolderAsync(NavDirection.Next));
        Assert.AreEqual("c_photos", name);
        Assert.AreEqual("c1.jpg", h.CurrentName);
        Assert.AreEqual(3, h.Nav.Keys.Count);
        h.Settle();
        Assert.IsFalse(h.Decoder.All.Any(d => d.Kind == DecodeKind.Hq && d.Path.StartsWith(after)), "HQ held while skipping folders");
        Assert.AreEqual(DisplayLevel.Preview, h.ShownLevel, "previews still load");

        h.Advance(500);
        h.Settle();
        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        Assert.IsTrue(oldPhotos.All(p => p.Images.Preview == null && p.Images.Hq == null), "the old folder's images are released");

        h.Run(async () => name = await h.Controller.TryOpenSiblingFolderAsync(NavDirection.Next));
        Assert.IsNull(name, "no folder after c_photos");
        Assert.AreEqual(NavDirection.Next, h.Strip.Bounces[^1]);
        h.Run(async () => name = await h.Controller.TryOpenSiblingFolderAsync(NavDirection.Prev));
        Assert.AreEqual("b_photos", name);
        Assert.AreEqual("img_00000.jpg", h.CurrentName);
        h.Run(async () => name = await h.Controller.TryOpenSiblingFolderAsync(NavDirection.Prev));
        Assert.IsNull(name, "a folder without images is skipped, and there is none before it");
        h.Settle();
        h.Advance(500);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_file_added_before_the_listing_is_published_appears_once()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 2, source: ListSource.Folder);
        Thread.Sleep(300); // the startup thread has watched and listed the folder, and waits for the first photo
        File.WriteAllBytes(Path.Combine(h.PhotoFolder, "img_00002b.jpg"), []);
        Thread.Sleep(800); // the watcher's batch is out, held until the listing is published
        h.Open();

        h.RunUntil(() => h.Nav.Keys.Count == 6, "the new file");
        Thread.Sleep(300);
        h.Settle();
        Assert.AreEqual(6, h.Nav.Keys.Count, "listed once, not twice");
        Assert.AreEqual(1, h.Nav.Keys.Count(k => h.Controller.Photos.GetPhoto(k)!.FilePath.EndsWith("img_00002b.jpg")));
        Assert.AreEqual("img_00002.jpg", h.CurrentName);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_held_folder_switch_key_switches_one_folder_at_a_time()
    {
        using var h = new DisplayHarness(photos: 3, source: ListSource.Folder);
        foreach (var (folder, file) in new[] { ("c_photos", "c1.jpg"), ("d_photos", "d1.jpg") })
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(h.Folder, folder)).FullName, file), []);
        h.Open();

        var first = h.Controller.TryOpenSiblingFolderAsync(NavDirection.Next);
        var repeat = h.Controller.TryOpenSiblingFolderAsync(NavDirection.Next); // key repeat while the switch runs
        Assert.IsTrue(repeat.IsCompleted, "dropped at once");
        Assert.IsNull(repeat.Result);

        h.RunUntil(() => first.IsCompleted, "the first switch", stepCache: false);
        Assert.AreEqual("c_photos", first.Result, "one folder on, not two");
        Assert.AreEqual("c1.jpg", h.CurrentName);
        h.Settle();
        h.Advance(500);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_folder_that_cannot_be_watched_opens_alone_and_cannot_switch_folders()
    {
        using var h = new DisplayHarness(photos: 3, source: ListSource.Folder,
            beforeStart: folder => Directory.Delete(folder, recursive: true));
        var sibling = Directory.CreateDirectory(Path.Combine(h.Folder, "c_photos")).FullName;
        File.WriteAllBytes(Path.Combine(sibling, "c1.jpg"), []);
        h.Open();
        Assert.AreEqual(1, h.Nav.Keys.Count);

        string? name = "unset";
        h.Run(async () => name = await h.Controller.TryOpenSiblingFolderAsync(NavDirection.Next));
        Assert.IsNull(name, "siblings are found from the watched folder; there is none");
        Assert.AreEqual(0, h.Strip.Bounces.Count, "not even a bounce");
        h.CheckInvariants();
    }
}
