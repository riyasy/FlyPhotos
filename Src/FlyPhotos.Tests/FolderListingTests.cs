using FlyPhotos.Core.Model;
using FlyPhotos.Services;

namespace FlyPhotos.Tests;

/// <summary>Folder mode listing: filtering, sorting, sibling folders and the batch merge.</summary>
[TestClass]
public sealed class FolderListingTests
{
    private static readonly HashSet<string> Extensions = new([".png", ".jpg"], StringComparer.OrdinalIgnoreCase);

    private string _root = "";

    [TestInitialize]
    public void Init() => _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "FlyFolderListing_" + Guid.NewGuid().ToString("N"))).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative, int bytes = 1, DateTime? modified = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modified is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    private string[] List(FolderSortBy sortBy = FolderSortBy.Name, bool descending = false, string? include = null) =>
        FolderListing.ListFolder(_root, Extensions, new FolderSortComparer(sortBy, descending), include)
            .Select(Path.GetFileName).ToArray()!;

    [TestMethod]
    public void Lists_only_supported_files_directly_in_the_folder()
    {
        Touch("a.png");
        Touch("b.JPG");
        Touch("notes.txt");
        Touch("noext");
        Touch(@"sub\c.png");

        CollectionAssert.AreEqual(new[] { "a.png", "b.JPG" }, List());
    }

    [TestMethod]
    public void Name_sort_is_natural_like_Explorer()
    {
        foreach (var n in new[] { "10.png", "2.png", "1.png", "img (12).jpg", "img (3).jpg" }) Touch(n);

        CollectionAssert.AreEqual(new[] { "1.png", "2.png", "10.png", "img (3).jpg", "img (12).jpg" }, List());
        CollectionAssert.AreEqual(new[] { "img (12).jpg", "img (3).jpg", "10.png", "2.png", "1.png" },
            List(descending: true));
    }

    [TestMethod]
    public void Date_modified_sort_uses_the_timestamp_and_falls_back_to_name()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Touch("a.png", modified: t.AddHours(2));
        Touch("b.png", modified: t);
        Touch("c.png", modified: t.AddHours(1));
        Touch("d.png", modified: t.AddHours(1));

        CollectionAssert.AreEqual(new[] { "b.png", "c.png", "d.png", "a.png" }, List(FolderSortBy.DateModified));
        CollectionAssert.AreEqual(new[] { "a.png", "d.png", "c.png", "b.png" }, List(FolderSortBy.DateModified, true));
    }

    [TestMethod]
    public void Size_sort_orders_by_length()
    {
        Touch("big.png", 300);
        Touch("small.png", 10);
        Touch("mid.png", 100);

        CollectionAssert.AreEqual(new[] { "small.png", "mid.png", "big.png" }, List(FolderSortBy.Size));
    }

    [TestMethod]
    public void The_opened_file_is_kept_even_when_unsupported()
    {
        Touch("a.png");
        Touch("odd.xyz");

        CollectionAssert.AreEqual(new[] { "a.png", "odd.xyz" }, List(include: "ODD.xyz"));
    }

    [TestMethod]
    public void Missing_folder_lists_nothing() =>
        Assert.AreEqual(0, FolderListing.ListFolder(Path.Combine(_root, "nope"), Extensions,
            new FolderSortComparer(FolderSortBy.Name, false)).Length);

    [TestMethod]
    public void Recorded_value_places_a_later_file_correctly()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Touch("old.png", modified: t);
        var order = new FolderSortComparer(FolderSortBy.DateModified, false);
        var listed = FolderListing.ListFolder(_root, Extensions, order);

        var added = Touch("aaa_newest.png", modified: t.AddDays(1));
        order.Record(new FileStamp(added, File.GetLastWriteTimeUtc(added).Ticks, 0, 0));

        Assert.IsTrue(order.Compare(listed[0], added) < 0, "a newer file must sort after an older one");
    }

    [TestMethod]
    public void Sibling_folders_are_natural_ordered_and_skip_folders_without_images()
    {
        Touch(@"Vol1\p.png");
        Touch(@"Vol2\p.png");
        Touch(@"Vol2b\readme.txt"); // no images
        Directory.CreateDirectory(Path.Combine(_root, "Vol3"));  // empty
        Touch(@"Vol10\p.jpg");
        Touch(@"Vol4\deep\p.png"); // images only in a subfolder: skipped

        string Sibling(string from, NavDirection d) =>
            Path.GetFileName(FolderListing.FindSiblingFolder(Path.Combine(_root, from), d, Extensions)) ?? "<none>";

        Assert.AreEqual("Vol10", Sibling("Vol2", NavDirection.Next));
        Assert.AreEqual("Vol2", Sibling("Vol10", NavDirection.Prev));
        Assert.AreEqual("Vol1", Sibling("Vol2", NavDirection.Prev));
        Assert.AreEqual("<none>", Sibling("Vol1", NavDirection.Prev));
        Assert.AreEqual("<none>", Sibling("Vol10", NavDirection.Next));
        Assert.AreEqual("Vol2", Sibling(@"Vol1\", NavDirection.Next)); // trailing separator tolerated
    }

    [TestMethod]
    public void Merge_inserts_added_at_their_sorted_positions()
    {
        var order = Comparer<string>.Create(NaturalComparer.Instance.Compare);
        List<string> current = ["1", "2", "3", "5", "9", "10"];

        var merged = FolderListing.MergeSorted(current, ["0", "4", "6", "7", "11"], order);

        CollectionAssert.AreEqual(new[] { "0", "1", "2", "3", "4", "5", "6", "7", "9", "10", "11" }, merged);
    }

    [TestMethod]
    public void Merge_handles_empty_inputs()
    {
        var order = Comparer<string>.Create(NaturalComparer.Instance.Compare);

        CollectionAssert.AreEqual(new[] { "a", "b" }, FolderListing.MergeSorted([], ["a", "b"], order));
        CollectionAssert.AreEqual(new[] { "a" }, FolderListing.MergeSorted(["a"], [], order));
    }

    // --- Edge cases ---

    [TestMethod]
    public void Hidden_and_system_files_are_listed_like_Explorer_discovery()
    {
        File.SetAttributes(Touch("hidden.png"), FileAttributes.Hidden);
        File.SetAttributes(Touch("system.png"), FileAttributes.System);
        Touch("plain.png");

        CollectionAssert.AreEqual(new[] { "hidden.png", "plain.png", "system.png" }, List());
    }

    [TestMethod]
    public void Extension_is_judged_on_the_last_dot_only()
    {
        Touch("photo.png.bak");
        Touch("backup.bak.png");
        Touch(".png");          // a name that is only an extension
        Touch("trailingdot.");

        CollectionAssert.AreEqual(new[] { ".png", "backup.bak.png" }, List());
    }

    [TestMethod]
    public void Unicode_spaces_and_brackets_sort_naturally()
    {
        foreach (var n in new[] { "été 10.png", "été 2.png", "IMG (12).jpg", "IMG (3).jpg", "写真 1.png" }) Touch(n);

        var listed = List();

        Assert.AreEqual(5, listed.Length);
        Assert.IsTrue(Array.IndexOf(listed, "été 2.png") < Array.IndexOf(listed, "été 10.png"));
        Assert.IsTrue(Array.IndexOf(listed, "IMG (3).jpg") < Array.IndexOf(listed, "IMG (12).jpg"));
    }

    [TestMethod]
    public void A_file_open_exclusively_by_another_program_is_still_listed()
    {
        var path = Touch("busy.png");
        using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        CollectionAssert.AreEqual(new[] { "busy.png" }, List());
    }

    [TestMethod]
    public void Empty_folder_and_trailing_separator()
    {
        Assert.AreEqual(0, List().Length);
        Touch("a.png");
        Assert.AreEqual(1, FolderListing.ListFolder(_root + Path.DirectorySeparatorChar, Extensions,
            new FolderSortComparer(FolderSortBy.Name, false)).Length);
    }

    [TestMethod]
    public void Date_created_sort_uses_the_creation_time()
    {
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(Touch("a.png"), t.AddDays(2));
        File.SetCreationTimeUtc(Touch("b.png"), t);
        File.SetCreationTimeUtc(Touch("c.png"), t.AddDays(1));

        CollectionAssert.AreEqual(new[] { "b.png", "c.png", "a.png" }, List(FolderSortBy.DateCreated));
    }

    [TestMethod]
    public void Size_sort_descending_with_ties_by_name()
    {
        Touch("b.png", 50);
        Touch("a.png", 50);
        Touch("big.png", 500);

        CollectionAssert.AreEqual(new[] { "big.png", "b.png", "a.png" }, List(FolderSortBy.Size, descending: true));
    }

    [TestMethod]
    public void Comparer_rename_carries_the_recorded_value_to_the_new_name()
    {
        var order = new FolderSortComparer(FolderSortBy.Size, false);
        order.Record(new FileStamp(@"C:\f\big.png", 0, 0, 900));
        order.Record(new FileStamp(@"C:\f\small.png", 0, 0, 10));

        order.Rename(@"C:\f\big.png", @"C:\f\a_renamed.png");

        Assert.IsTrue(order.Compare(@"C:\f\small.png", @"C:\f\a_renamed.png") < 0,
            "renamed file keeps its size, so it still sorts after the small one");
    }

    [TestMethod]
    public void Comparer_value_lookup_matches_paths_case_insensitively()
    {
        var order = new FolderSortComparer(FolderSortBy.Size, false);
        order.Record(new FileStamp(@"C:\f\BIG.png", 0, 0, 900));
        order.Record(new FileStamp(@"C:\f\small.png", 0, 0, 10));

        Assert.IsTrue(order.Compare(@"C:\f\small.png", @"c:\F\big.PNG") < 0);
    }

    [TestMethod]
    public void Comparing_stamps_and_comparing_recorded_paths_agree()
    {
        var rng = new Random(7);
        foreach (var sortBy in Enum.GetValues<FolderSortBy>())
            foreach (var descending in new[] { false, true })
            {
                var order = new FolderSortComparer(sortBy, descending);
                var stamps = Enumerable.Range(0, 30).Select(i => new FileStamp($@"C:\f\{i}.png",
                    rng.Next(5), rng.Next(5), rng.Next(5))).ToArray();
                foreach (var s in stamps) order.Record(s);
                foreach (var a in stamps)
                    foreach (var b in stamps)
                        Assert.AreEqual(Math.Sign(order.Compare(a, b)), Math.Sign(order.Compare(a.Path, b.Path)),
                            $"{sortBy} desc={descending}: {a.Path} vs {b.Path}");
            }
    }

    [TestMethod]
    public void NewEmpty_keeps_the_settings_but_not_the_values()
    {
        var order = new FolderSortComparer(FolderSortBy.Size, descending: true);
        order.Record(new FileStamp(@"C:\f\a.png", 0, 0, 900));

        var fresh = order.NewEmpty();

        Assert.AreEqual(FolderSortBy.Size, fresh.SortBy);
        Assert.IsTrue(fresh.Compare(@"C:\f\a.png", @"C:\f\b.png") > 0, "no sizes recorded: names decide, descending");
    }

    [TestMethod]
    public void Hidden_sibling_folders_are_skipped()
    {
        Touch(@"A\p.png");
        Touch(@"B\p.png");
        File.SetAttributes(Path.Combine(_root, "B"), FileAttributes.Directory | FileAttributes.Hidden);
        Touch(@"C\p.png");

        Assert.AreEqual("C", Path.GetFileName(FolderListing.FindSiblingFolder(Path.Combine(_root, "A"),
            NavDirection.Next, Extensions)));
    }

    [TestMethod]
    public void Sibling_lookup_is_case_insensitive_and_handles_missing_or_lonely_folders()
    {
        Touch(@"Vol1\p.png");
        Touch(@"Vol2\p.png");
        Touch(@"Solo\Only\p.png");

        Assert.AreEqual("Vol2", Path.GetFileName(FolderListing.FindSiblingFolder(
            Path.Combine(_root, "VOL1"), NavDirection.Next, Extensions)));
        Assert.IsNull(FolderListing.FindSiblingFolder(Path.Combine(_root, "Gone"), NavDirection.Next, Extensions),
            "a folder that no longer exists has no position among its siblings");
        Assert.IsNull(FolderListing.FindSiblingFolder(Path.Combine(_root, @"Solo\Only"), NavDirection.Next, Extensions),
            "the only folder in its parent");
        Assert.IsNull(FolderListing.FindSiblingFolder(Path.GetPathRoot(_root)!, NavDirection.Next, Extensions),
            "a drive root has no parent");
    }

    [TestMethod]
    public void Merge_matches_a_full_sort_on_random_input()
    {
        var rng = new Random(42);
        var order = Comparer<int>.Default;
        for (int round = 0; round < 500; round++)
        {
            var current = Enumerable.Range(0, rng.Next(0, 50)).Select(_ => rng.Next(100)).Order().ToList();
            var added = Enumerable.Range(0, rng.Next(0, 20)).Select(_ => rng.Next(100)).Order().ToList();

            var merged = FolderListing.MergeSorted(current, added, order);

            CollectionAssert.AreEqual(current.Concat(added).Order().ToList(), merged, $"round {round}");
        }
    }
}
