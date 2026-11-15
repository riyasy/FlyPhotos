using FlyPhotos.Display.State;

namespace FlyPhotos.Tests.Display;

/// <summary>The photo list: stable keys, the startup hand-over to the full listing, and lock-free readers.</summary>
[TestClass]
public sealed class PhotoListTests
{
    private static ListChangeBatch Removing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return new ListChangeBatch(set.Contains, null, [], null);
    }

    private static ListChangeBatch Adding(params string[] paths) =>
        new(_ => false, null, paths, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void The_opened_photo_keeps_its_key_and_photo_at_its_place_in_the_listing()
    {
        var list = new PhotoList();
        list.Initialize([@"c:\f\c.jpg"]);
        int openedKey = list.Keys[0];
        var opened = list.GetPhoto(openedKey);

        var keys = list.PrepareListing([@"c:\f\a.jpg", @"c:\f\b.jpg", @"c:\f\c.jpg", @"c:\f\d.jpg"], 2, openedKey);
        Assert.AreEqual(1, list.Keys.Count, "nothing is published until Publish");
        list.Publish(keys);

        Assert.AreEqual(4, list.Keys.Count);
        Assert.AreEqual(openedKey, list.Keys[2]);
        Assert.AreSame(opened, list.GetPhoto(openedKey));
        Assert.AreEqual(4, list.Keys.Distinct().Count());
        Assert.AreEqual(@"c:\f\a.jpg", list.GetPhoto(list.Keys[0])!.FilePath);
    }

    [TestMethod]
    public void Keys_are_never_reused_after_removals()
    {
        var list = new PhotoList();
        list.Initialize([@"c:\f\a.jpg", @"c:\f\b.jpg"]);
        var seen = new HashSet<int>(list.Keys);
        for (int round = 0; round < 50; round++)
        {
            var name = $@"c:\f\n{round:D3}.jpg";
            list.ApplyChanges(Adding(name), keepKey: list.Keys[0]);
            int added = list.Keys.Single(k => list.GetPhoto(k)!.FilePath == name);
            Assert.IsTrue(seen.Add(added), $"key {added} came back");
            var removed = list.ApplyChanges(Removing(name), keepKey: list.Keys[0]);
            list.Forget(removed);
            Assert.IsNull(list.GetPhoto(added));
        }
    }

    [TestMethod]
    public void Removed_photos_stay_resolvable_until_forgotten()
    {
        var list = new PhotoList();
        list.Initialize([@"c:\f\a.jpg", @"c:\f\b.jpg", @"c:\f\c.jpg"]);
        var before = list.Keys;
        int b = before[1];
        var removed = list.ApplyChanges(Removing(@"c:\f\b.jpg"), keepKey: before[0]);
        Assert.AreEqual(b, removed.Single().Key);
        Assert.IsFalse(list.Keys.Contains(b));
        Assert.IsNotNull(list.GetPhoto(b), "the controller still needs it to decide where to go");
        Assert.AreEqual(3, before.Count, "an old snapshot is never changed in place");
        list.Forget(removed);
        Assert.IsNull(list.GetPhoto(b));
    }

    [TestMethod]
    public void A_batch_that_would_empty_the_list_keeps_the_photo_on_screen()
    {
        var list = new PhotoList();
        list.Initialize([@"c:\f\a.jpg", @"c:\f\b.jpg"]);
        int keep = list.Keys[1];
        var removed = list.ApplyChanges(Removing(@"c:\f\a.jpg", @"c:\f\b.jpg"), keepKey: keep);
        Assert.AreEqual(1, removed.Count);
        CollectionAssert.AreEqual(new[] { keep }, list.Keys.ToArray());
    }

    [TestMethod]
    public void A_batch_that_changes_nothing_keeps_the_same_snapshot()
    {
        var list = new PhotoList();
        list.Initialize([@"c:\f\a.jpg"]);
        var keys = list.Keys;
        list.ApplyChanges(Removing(@"c:\f\zzz.jpg"), keepKey: keys[0]);
        Assert.AreSame(keys, list.Keys);
    }

    [TestMethod]
    public void Renames_keep_the_key_and_report_the_old_and_new_path()
    {
        var list = new PhotoList();
        list.Initialize([@"c:\f\a.jpg", @"c:\f\b.jpg"]);
        int a = list.Keys[0];
        var reported = new List<(string, string)>();
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"c:\f\a.jpg"] = @"c:\f\z.jpg" };
        list.ApplyChanges(new ListChangeBatch(_ => false, renamed, [], StringComparer.OrdinalIgnoreCase), keepKey: a,
            onRenamed: (o, n) => reported.Add((o, n)));
        Assert.AreEqual(@"c:\f\z.jpg", list.GetPhoto(a)!.FilePath);
        CollectionAssert.Contains(reported, (@"c:\f\a.jpg", @"c:\f\z.jpg"));
    }

    /// <summary>The strip and the cache read the list on other threads while the UI thread changes it.</summary>
    [TestMethod]
    public void Readers_always_see_a_whole_snapshot_while_the_list_changes()
    {
        var list = new PhotoList();
        list.Initialize([.. Enumerable.Range(0, 100).Select(i => $@"c:\f\p{i:D5}.jpg")]);
        using var stop = new CancellationTokenSource();
        int torn = 0;
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var keys = list.Keys;
                if (keys.Count == 0 || keys.Distinct().Count() != keys.Count) Interlocked.Increment(ref torn);
                foreach (int k in keys) _ = list.GetPhoto(k)?.FilePath; // may be null once forgotten: never throws
            }
        })).ToArray();

        var rng = new Random(3);
        for (int i = 0; i < 2000; i++)
        {
            var keys = list.Keys;
            if (rng.Next(2) == 0 && keys.Count > 1)
                list.Forget(list.ApplyChanges(Removing(list.GetPhoto(keys[rng.Next(keys.Count)])!.FilePath), keys[0]));
            else
                list.ApplyChanges(Adding($@"c:\f\q{i:D5}.jpg"), keys[0]);
        }
        stop.Cancel();
        Task.WaitAll(readers);
        Assert.AreEqual(0, torn);
    }
}
