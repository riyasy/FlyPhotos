using System.Collections.Concurrent;
using FlyPhotos.Services;

namespace FlyPhotos.Tests;

/// <summary>
/// Folder monitoring against a real temp folder. Waits are bounded polls, so a pass is fast and a
/// failure costs at most the timeout.
/// </summary>
[TestClass]
public sealed class FolderWatcherTests
{
    private const int QuietMs = 100;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private string _dir = "";
    private FolderWatcher _watcher = null!;
    private readonly ConcurrentQueue<FolderChanges> _batches = new();

    private static bool IsSupported(string path) =>
        Path.GetExtension(path) is var e && (e.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                                             e.Equals(".jpg", StringComparison.OrdinalIgnoreCase));

    [TestInitialize]
    public void Init()
    {
        _dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "FlyFolderWatcher_" + Guid.NewGuid().ToString("N"))).FullName;
        _watcher = new FolderWatcher(_dir, IsSupported, QuietMs);
        _watcher.Changed += (_, c) => _batches.Enqueue(c);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _watcher.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string P(string name) => Path.Combine(_dir, name);

    private HashSet<string> Added => _batches.SelectMany(b => b.Added).Select(s => Path.GetFileName(s.Path)).ToHashSet()!;
    private HashSet<string> Removed => _batches.SelectMany(b => b.Removed).Select(Path.GetFileName).ToHashSet()!;

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + what);
            Thread.Sleep(20);
        }
    }

    // Long enough for any batch that was going to come to have come.
    private static void Settle() => Thread.Sleep(QuietMs * 6);

    [TestMethod]
    public void New_image_is_reported_once()
    {
        File.WriteAllBytes(P("a.png"), [1, 2, 3]);

        WaitUntil(() => Added.Contains("a.png"), "a.png added");
        Settle();
        var stamp = _batches.SelectMany(b => b.Added).Single();
        Assert.AreEqual(3, stamp.Length, "the stamp carries the file's real size");
        Assert.IsTrue(stamp.LastWriteUtcTicks > 0);
    }

    [TestMethod]
    public void Unsupported_files_are_ignored()
    {
        File.WriteAllText(P("notes.txt"), "x");
        File.WriteAllBytes(P("b.jpg"), [1]);

        WaitUntil(() => Added.Contains("b.jpg"), "b.jpg added");
        Settle();
        CollectionAssert.AreEquivalent(new[] { "b.jpg" }, Added.ToArray());
        Assert.AreEqual(0, Removed.Count);
    }

    [TestMethod]
    public void Deleted_image_is_reported_as_removed()
    {
        File.WriteAllBytes(P("old.png"), [1]);
        WaitUntil(() => Added.Contains("old.png"), "old.png added");

        File.Delete(P("old.png"));

        WaitUntil(() => Removed.Contains("old.png"), "old.png removed");
    }

    [TestMethod]
    public void File_still_being_written_is_reported_only_after_it_is_closed()
    {
        using (var writer = new FileStream(P("big.png"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            for (int i = 0; i < 5; i++)
            {
                writer.Write(new byte[1024]);
                writer.Flush();
                Thread.Sleep(QuietMs);
            }
            Settle();
            Assert.IsFalse(Added.Contains("big.png"), "reported while the writer still had it open");
        }

        WaitUntil(() => Added.Contains("big.png"), "big.png added after close");
    }

    [TestMethod]
    public void Temp_file_renamed_to_an_image_is_reported_as_added()
    {
        File.WriteAllBytes(P("render.tmp"), [1]);
        File.Move(P("render.tmp"), P("render.png"));

        WaitUntil(() => Added.Contains("render.png"), "render.png added");
        Assert.AreEqual(0, Removed.Count);
    }

    private List<(string Old, string New)> Renamed =>
        _batches.SelectMany(b => b.Renamed).Select(r => (Path.GetFileName(r.OldPath), Path.GetFileName(r.NewPath))).ToList();

    private void WaitForAdded(string name)
    {
        File.WriteAllBytes(P(name), [1]);
        WaitUntil(() => Added.Contains(name), name + " added");
    }

    [TestMethod]
    public void Renamed_image_is_reported_as_a_rename_pair()
    {
        WaitForAdded("before.png");

        File.Move(P("before.png"), P("after.png"));

        WaitUntil(() => Renamed.Count > 0, "rename seen");
        Settle();
        CollectionAssert.AreEqual(new[] { ("before.png", "after.png") }, Renamed);
        Assert.AreEqual(0, Removed.Count);
        Assert.IsFalse(Added.Contains("after.png"), "a rename is not an addition");
    }

    [TestMethod]
    public void Rename_chain_within_one_batch_reports_first_to_last()
    {
        WaitForAdded("a.png");

        File.Move(P("a.png"), P("b.png"));
        File.Move(P("b.png"), P("c.png"));

        WaitUntil(() => Renamed.Count > 0, "rename seen");
        Settle();
        CollectionAssert.AreEqual(new[] { ("a.png", "c.png") }, Renamed);
    }

    [TestMethod]
    public void Renamed_then_deleted_is_reported_as_removal_of_the_original()
    {
        WaitForAdded("orig.png");

        File.Move(P("orig.png"), P("moved.png"));
        File.Delete(P("moved.png"));

        WaitUntil(() => Removed.Contains("orig.png"), "orig.png removed");
        Settle();
        Assert.AreEqual(0, Renamed.Count);
    }

    [TestMethod]
    public void Image_renamed_to_unsupported_is_reported_as_removed()
    {
        WaitForAdded("keep.png");

        File.Move(P("keep.png"), P("keep.png.bak"));

        WaitUntil(() => Removed.Contains("keep.png"), "keep.png removed");
    }

    [TestMethod]
    public void New_image_renamed_before_it_is_reported_is_just_added_under_its_new_name()
    {
        File.WriteAllBytes(P("draft.png"), [1]);
        File.Move(P("draft.png"), P("final.png"));

        WaitUntil(() => Added.Contains("final.png"), "final.png added");
        Settle();
        Assert.IsFalse(Added.Contains("draft.png"));
        Assert.AreEqual(0, Renamed.Count);
        Assert.AreEqual(0, Removed.Count);
    }

    [TestMethod]
    public void Image_created_and_deleted_within_one_batch_is_not_added()
    {
        File.WriteAllBytes(P("flash.png"), [1]);
        File.Delete(P("flash.png"));

        Settle();
        Assert.IsFalse(Added.Contains("flash.png"));
    }

    [TestMethod]
    public void Burst_of_images_is_all_reported()
    {
        for (int i = 0; i < 50; i++)
            File.WriteAllBytes(P($"gen_{i:000}.png"), new byte[256]);

        WaitUntil(() => Added.Count == 50, "all 50 added");
    }

    [TestMethod]
    public void Subfolder_changes_are_ignored()
    {
        Directory.CreateDirectory(P("sub"));
        File.WriteAllBytes(P(@"sub\deep.png"), [1]);
        File.WriteAllBytes(P("top.png"), [1]);

        WaitUntil(() => Added.Contains("top.png"), "top.png added");
        Settle();
        Assert.IsFalse(Added.Contains("deep.png"));
    }

    [TestMethod]
    public void Case_only_rename_is_reported()
    {
        WaitForAdded("shot.png");

        File.Move(P("shot.png"), P("SHOT.png"));

        WaitUntil(() => Renamed.Count > 0, "case rename seen");
        CollectionAssert.AreEqual(new[] { ("shot.png", "SHOT.png") }, Renamed);
    }

    [TestMethod]
    public void Renaming_back_and_forth_within_a_batch_reports_nothing()
    {
        WaitForAdded("x.png");

        File.Move(P("x.png"), P("y.png"));
        File.Move(P("y.png"), P("x.png"));

        Settle();
        Assert.AreEqual(0, Renamed.Count);
        Assert.AreEqual(0, Removed.Count);
    }

    [TestMethod]
    public void Renames_between_unsupported_names_are_ignored()
    {
        File.WriteAllText(P("a.txt"), "x");
        Settle();
        int before = _batches.Count;

        File.Move(P("a.txt"), P("b.txt"));

        Settle();
        Assert.AreEqual(before, _batches.Count);
    }

    [TestMethod]
    public void File_overwritten_in_place_is_reported_as_removed_and_added()
    {
        WaitForAdded("same.png");
        _batches.Clear();

        File.Delete(P("same.png"));
        File.WriteAllBytes(P("same.png"), [9, 9]);

        WaitUntil(() => Removed.Contains("same.png") && Added.Contains("same.png"), "replace seen");
    }

    [TestMethod]
    public void Editing_an_already_reported_image_reports_nothing()
    {
        WaitForAdded("edit.png");
        _batches.Clear();

        File.AppendAllText(P("edit.png"), "more bytes");

        Settle();
        Assert.AreEqual(0, _batches.Count);
    }

    [TestMethod]
    public void A_steady_stream_of_new_images_is_not_starved()
    {
        // One new file every 60 ms for ~2 s: events never stop for a whole quiet period, yet files must
        // still be reported while the stream is running, not only after it ends.
        bool reportedDuringStream = false;
        for (int i = 0; i < 30; i++)
        {
            File.WriteAllBytes(P($"stream_{i:00}.png"), [1]);
            Thread.Sleep(60);
            reportedDuringStream |= Added.Count > 0;
        }

        Assert.IsTrue(reportedDuringStream, "nothing reported while files kept arriving");
        WaitUntil(() => Added.Count == 30, "all 30 added");
    }

    [TestMethod]
    public void A_reader_holding_the_file_does_not_delay_it()
    {
        File.WriteAllBytes(P("viewed.png"), [1, 2]);
        using var reader = new FileStream(P("viewed.png"), FileMode.Open, FileAccess.Read, FileShare.Read);

        WaitUntil(() => Added.Contains("viewed.png"), "viewed.png added while a reader has it open");
    }

    [TestMethod]
    public void Unicode_file_names_are_reported()
    {
        File.WriteAllBytes(P("写真 été (1).png"), [1]);

        WaitUntil(() => Added.Contains("写真 été (1).png"), "unicode name added");
    }

    [TestMethod]
    public void Watcher_error_asks_for_a_rescan_and_drops_pending_work()
    {
        // Held open for writing, so it is pending (seen) but cannot be reported before the error.
        using (var writer = new FileStream(P("pending.png"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write([1, 2, 3]);
            writer.Flush();
            Thread.Sleep(QuietMs * 2); // let its events arrive

            _watcher.OnError(null, new ErrorEventArgs(new InternalBufferOverflowException()));
        }

        WaitUntil(() => _batches.Any(b => b.Rescan), "rescan requested");
        Settle();
        var rescan = _batches.First(b => b.Rescan);
        Assert.AreEqual(0, rescan.Added.Count + rescan.Removed.Count + rescan.Renamed.Count);
        Assert.IsFalse(Added.Contains("pending.png"), "work from before the lost events is replaced by the rescan");
    }

    [TestMethod]
    public void Replaced_file_reports_its_removal_together_with_the_new_file()
    {
        WaitForAdded("edit_me.png");
        _batches.Clear();

        // An editor saving over the file: delete, then a new file that keeps changing for a while
        // (each append opens, writes and closes; nothing stays open).
        File.Delete(P("edit_me.png"));
        File.WriteAllBytes(P("edit_me.png"), [1]);
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(QuietMs / 2);
            File.AppendAllText(P("edit_me.png"), "x");
        }

        WaitUntil(() => Added.Contains("edit_me.png"), "replacement added");
        Settle();
        Assert.IsFalse(_batches.Any(b => b.Removed.Any(r => r.EndsWith("edit_me.png")) &&
                                         !b.Added.Any(a => a.Path.EndsWith("edit_me.png"))),
            "the removal must not arrive in an earlier batch than its replacement");
        Assert.IsTrue(Removed.Contains("edit_me.png"));
    }

    [TestMethod]
    public void Rename_that_changes_the_extension_is_a_removal_plus_an_addition()
    {
        WaitForAdded("format.png");

        File.Move(P("format.png"), P("format.jpg"));

        WaitUntil(() => Removed.Contains("format.png") && Added.Contains("format.jpg"), "remove + add seen");
        Assert.AreEqual(0, Renamed.Count, "a changed format is not the same photo");
    }

    [TestMethod]
    public void Dispose_twice_is_safe()
    {
        _watcher.Dispose();
        _watcher.Dispose();
    }

    [TestMethod]
    public void No_events_after_dispose()
    {
        _watcher.Dispose();
        File.WriteAllBytes(P("late.png"), [1]);

        Settle();
        Assert.AreEqual(0, _batches.Count);
    }
}
