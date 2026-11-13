using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// Folder mode's list source on its own, on real temp folders: the paths the controller can't reach from the disk on
/// cue — a rescan after the watcher lost events, and a batch from a folder already left.
/// </summary>
[TestClass]
public sealed class FolderSourceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        AppConfig.Settings = new AppSettings();
        _root = Path.Combine(Path.GetTempPath(), "FlyFolderSource_" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name, params string[] files)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var file in files) File.WriteAllBytes(Path.Combine(folder, file), []);
        return folder;
    }

    // The source's current watcher, caught from the batch it reports for change.
    private static FolderWatcher WatcherOf(FolderSource source, Action change)
    {
        var got = new TaskCompletionSource<FolderWatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += (watcher, _) => got.TrySetResult(watcher);
        change();
        Assert.IsTrue(got.Task.Wait(5000), "the watcher reported the change");
        return got.Task.Result;
    }

    private static readonly FolderChanges Rescan = new([], [], [], Rescan: true);

    [TestMethod]
    public async Task A_rescan_relists_the_folder_and_keeps_the_photo_on_screen_even_if_unsupported()
    {
        var folder = Folder("b", "a.jpg", "b.jpg", "c.jpg", "keep.xyz");
        using var source = new FolderSource();
        Assert.AreEqual(3, source.ListAndWatch(Path.Combine(folder, "b.jpg")).Count);
        var watcher = WatcherOf(source, () => File.WriteAllBytes(Path.Combine(folder, "d.jpg"), []));
        File.Delete(Path.Combine(folder, "a.jpg"));

        var batch = await source.ToBatchAsync(watcher, Rescan, keepName: "keep.xyz");

        Assert.IsNotNull(batch);
        Assert.IsTrue(batch.IsRemoved(Path.Combine(folder, "a.jpg")), "gone from disk");
        Assert.IsFalse(batch.IsRemoved(Path.Combine(folder, "b.jpg")));
        Assert.IsFalse(batch.IsRemoved(Path.Combine(folder, "keep.xyz")), "the photo on screen stays");
        CollectionAssert.AreEquivalent(new[] { "b.jpg", "c.jpg", "d.jpg", "keep.xyz" },
            batch.Added.Select(Path.GetFileName).ToList());
        Assert.IsNotNull(batch.Order, "additions come sorted");
    }

    [TestMethod]
    public async Task A_batch_from_a_folder_left_behind_is_stale()
    {
        var before = Folder("b", "b1.jpg");
        Folder("c", "c1.jpg");
        using var source = new FolderSource();
        source.ListAndWatch(Path.Combine(before, "b1.jpg"));
        var old = WatcherOf(source, () => File.WriteAllBytes(Path.Combine(before, "b2.jpg"), []));
        Assert.IsTrue(source.IsCurrent(old));

        var switched = await source.SwitchToSiblingAsync(NavDirection.Next);

        Assert.AreEqual("c", switched?.Name);
        Assert.IsFalse(source.IsCurrent(old), "the controller drops its batches");
        Assert.IsNull(await source.ToBatchAsync(old, Rescan, keepName: null), "a rescan for it yields nothing");
    }
}
