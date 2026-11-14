using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Tests.Display.Harness;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// Delete and rename through the controller, as the window calls them: where the viewer goes, what is released, and
/// that a delete and a list change never run at once. The delete itself is scripted (the real one fills the Recycle
/// Bin); the rename is real.
/// </summary>
[TestClass]
public sealed class DeleteAndRenameTests
{
    private static DeleteResult Delete(DisplayHarness h)
    {
        DeleteResult? result = null;
        h.Run(async () => result = await h.Controller.DeleteCurrentPhoto());
        return result!;
    }

    // Records the paths asked for and reports success, without touching the disk.
    private static List<string> DeleteSucceeds(DisplayHarness h)
    {
        var deleted = new List<string>();
        h.Controller.DeleteFile = path =>
        {
            deleted.Add(path);
            return Task.FromResult(new DeleteResult(true, false));
        };
        return deleted;
    }

    // --- Delete ---

    [TestMethod]
    public void Deleting_the_photo_on_screen_shows_the_next_one_and_releases_its_images()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 3);
        var deleted = DeleteSucceeds(h);
        h.Open();
        var photo = h.Current;
        Assert.IsTrue(h.Controller.CanDeleteCurrentPhoto());

        var result = Delete(h);

        Assert.IsTrue(result.DeleteSuccess);
        Assert.IsFalse(result.IsLastPhoto);
        CollectionAssert.AreEqual(new[] { h.PathOf(3) }, deleted);
        Assert.AreEqual("img_00004.jpg", h.CurrentName);
        Assert.AreEqual(3, h.Nav.Position);
        Assert.AreEqual(9, h.Nav.Keys.Count);
        Assert.IsFalse(h.Nav.Keys.Any(k => ReferenceEquals(h.Controller.Photos.GetPhoto(k), photo)), "gone from the list");
        Assert.IsNull(photo.Slots.Hq, "its images are released");
        Assert.IsNull(photo.Slots.Preview);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void Deleting_the_last_photo_in_the_list_shows_the_one_before()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 9);
        DeleteSucceeds(h);
        h.Open();

        var result = Delete(h);

        Assert.IsTrue(result.DeleteSuccess);
        Assert.IsFalse(result.IsLastPhoto);
        Assert.AreEqual("img_00008.jpg", h.CurrentName);
        Assert.AreEqual(8, h.Nav.Position);
        Assert.AreEqual(9, h.Nav.Keys.Count);
        h.Settle();
        h.CheckInvariants();
    }

    [TestMethod]
    public void Deleting_the_only_photo_reports_it_and_leaves_the_photo_for_the_closing_window()
    {
        using var h = new DisplayHarness(photos: 1);
        DeleteSucceeds(h);
        h.Open();
        var photo = h.Current;

        var result = Delete(h);

        Assert.IsTrue(result.DeleteSuccess);
        Assert.IsTrue(result.IsLastPhoto, "the window closes the app");
        Assert.AreEqual(1, h.Nav.Keys.Count, "the list is left alone: the close animation still draws it");
        Assert.AreSame(photo, h.Canvas.HeldPhoto);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_failed_delete_changes_nothing()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 3);
        h.Controller.DeleteFile = _ => Task.FromResult(new DeleteResult(false, false, "locked"));
        h.Open();
        var photo = h.Current;
        int installs = h.Canvas.History.Count;

        var result = Delete(h);

        Assert.IsFalse(result.DeleteSuccess);
        Assert.AreEqual("locked", result.FailMessage);
        Assert.AreSame(photo, h.Current);
        Assert.AreEqual(10, h.Nav.Keys.Count);
        Assert.AreEqual(installs, h.Canvas.History.Count, "nothing reinstalled");
        Assert.IsNotNull(photo.Slots.Hq, "its images are kept");
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_list_change_that_arrives_during_a_delete_waits_for_it()
    {
        using var h = new DisplayHarness(photos: 10, openAt: 3);
        var onDisk = new TaskCompletionSource<DeleteResult>();
        h.Controller.DeleteFile = _ => onDisk.Task;
        h.Open();

        var delete = h.Controller.DeleteCurrentPhoto();
        var change = h.Controller.ChangeListAsync(ListChangeBatch.Remove(h.PathOf(4))); // e.g. a watcher batch
        h.Ui.RunPending();
        Assert.IsFalse(change.IsCompleted, "waits for the delete");
        Assert.AreEqual(10, h.Nav.Keys.Count);

        onDisk.SetResult(new DeleteResult(true, false));
        h.RunUntil(() => delete.IsCompleted && change.IsCompleted, "the delete, then the list change", stepCache: false);

        Assert.IsTrue(delete.Result.DeleteSuccess);
        Assert.AreEqual(8, h.Nav.Keys.Count, "both photos are gone");
        Assert.AreEqual("img_00005.jpg", h.CurrentName, "the delete showed 4, then its removal showed 5");
        Assert.AreEqual(3, h.Nav.Position);
        h.Settle();
        h.CheckInvariants();
    }

    // --- Rename (Explorer mode; Folder mode is in FolderModeTests) ---

    [TestMethod]
    public void Renaming_in_explorer_mode_keeps_the_photo_where_it_is()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 1);
        h.Open();
        var details = new List<FileDisplayDetails>();
        h.Controller.FileNameOrDetailsChanged += details.Add;
        var photo = h.Current;
        var oldPath = photo.FilePath;

        RenameResult? result = null;
        h.Run(async () => result = await h.Controller.RenameCurrentPhoto("zzz.jpg"));

        Assert.IsTrue(result!.Success, result.FailMessage);
        var newPath = Path.Combine(h.PhotoFolder, "zzz.jpg");
        Assert.AreSame(photo, h.Current, "the same photo, key and images");
        Assert.AreEqual(newPath, photo.FilePath);
        Assert.AreEqual(1, h.Nav.Position, "Explorer's order is kept: no move to a sorted place");
        Assert.IsTrue(File.Exists(newPath) && !File.Exists(oldPath), "renamed on disk");
        CollectionAssert.Contains(h.Canvas.Renames, (oldPath, newPath), "the canvas follows the new path");
        Assert.AreEqual("[2/5] zzz.jpg (2 x 2)", details[^1].DisplayText);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_disk_cache_write_running_during_a_rename_is_filed_under_the_name_it_was_decoded_from()
    {
        // FMEA 21. Nothing quick for the first photo, so the cache decodes its preview from the file and writes it.
        using var h = new DisplayHarness(photos: 5, openAt: 1, script: d => d.Kind switch
        {
            DecodeKind.FirstPreview => Reply.Nothing,
            DecodeKind.DiskWrite => Reply.Pending,
            _ => Reply.Item,
        });
        h.Open();
        var oldPath = h.Current.FilePath;
        var write = h.Decoder.PendingOf(DecodeKind.DiskWrite).Single(d => d.Path == oldPath);

        RenameResult? result = null;
        h.Run(async () => result = await h.Controller.RenameCurrentPhoto("zzz.jpg"));
        Assert.IsTrue(result!.Success, result.FailMessage);
        write.Succeed();
        h.Settle();

        CollectionAssert.AreEqual(new[] { oldPath }, h.Decoder.DiskWrites.ToArray(),
            "written once, under the path read when the job started, not the new one");
        Assert.AreEqual(Path.Combine(h.PhotoFolder, "zzz.jpg"), h.Current.FilePath);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_rename_onto_an_existing_file_fails_and_changes_nothing()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 1);
        h.Open();
        var photo = h.Current;
        var oldPath = photo.FilePath;

        RenameResult? result = null;
        h.Run(async () => result = await h.Controller.RenameCurrentPhoto("img_00003.jpg"));

        Assert.IsFalse(result!.Success);
        Assert.IsFalse(string.IsNullOrEmpty(result.FailMessage), "the user is told why");
        Assert.AreSame(photo, h.Current);
        Assert.AreEqual(oldPath, photo.FilePath);
        Assert.AreEqual(1, h.Nav.Position);
        Assert.AreEqual(5, h.Nav.Keys.Count);
        Assert.IsTrue(File.Exists(oldPath) && File.Exists(h.PathOf(3)), "neither file touched");
        Assert.AreEqual(0, h.Canvas.Renames.Count);
        // The failure message comes from the app's resources, which the test process may not load; if so it is
        // logged as an error and reported with the exception's message instead. Either way it is a failed rename.
        h.Log.Errors.Clear();
        h.CheckInvariants();
    }
}
