using FlyPhotos.Display.State;
using FlyPhotos.Services;

namespace FlyPhotos.Tests;

/// <summary>
/// The list-change rules every Folder mode update, folder switch, rename and delete goes through:
/// what survives, what is renamed in place, what is new, and where the viewer lands.
/// </summary>
[TestClass]
public sealed class ListChangeTests
{
    /// <summary>Stands in for a photo: an identity whose path can change (rename).</summary>
    private sealed class Item(string path)
    {
        public string Path = path;
        public override string ToString() => Path;
    }

    private static readonly IComparer<string> Order = Comparer<string>.Create(NaturalComparer.Instance.Compare);

    private static List<Item> Items(params string[] paths) => paths.Select(p => new Item(p)).ToList();

    private static ListChange.Result<Item> Apply(List<Item> current, string[]? removed = null,
        Dictionary<string, string>? renamed = null, string[]? added = null, Item? keep = null)
    {
        var removedSet = new HashSet<string>(removed ?? [], StringComparer.OrdinalIgnoreCase);
        var renamedMap = renamed == null ? null : new Dictionary<string, string>(renamed, StringComparer.OrdinalIgnoreCase);
        var addedSorted = (added ?? []).ToArray();
        Array.Sort(addedSorted, Order);
        return ListChange.Apply(current, i => i.Path, removedSet.Contains, renamedMap, addedSorted, Order,
            create: p => new Item(p), rename: (i, p) => i.Path = p, isKeep: i => ReferenceEquals(i, keep));
    }

    private static string[] Paths(ListChange.Result<Item> r) => r.Items.Select(i => i.Path).ToArray();

    [TestMethod]
    public void Remove_drops_matching_items_and_keeps_the_rest_in_order()
    {
        var r = Apply(Items("1", "2", "3", "4"), removed: ["2", "4"]);

        Assert.IsTrue(r.Changed);
        CollectionAssert.AreEqual(new[] { "1", "3" }, Paths(r));
        CollectionAssert.AreEqual(new[] { "2", "4" }, r.Removed.Select(i => i.Path).ToArray());
    }

    [TestMethod]
    public void Paths_match_case_insensitively_like_Windows()
    {
        var r = Apply(Items("a.PNG", "b.png"), removed: ["A.png"], added: ["B.PNG"]);

        CollectionAssert.AreEqual(new[] { "b.png" }, Paths(r), "B.PNG is the listed b.png, not a new photo");
    }

    [TestMethod]
    public void Additions_go_to_their_sorted_positions()
    {
        var r = Apply(Items("1", "3", "5", "9"), added: ["0", "4", "6", "11"]);

        CollectionAssert.AreEqual(new[] { "0", "1", "3", "4", "5", "6", "9", "11" }, Paths(r));
    }

    [TestMethod]
    public void Additions_already_listed_or_repeated_are_skipped()
    {
        var current = Items("1", "3");
        var r = Apply(current, added: ["3", "2", "2"]);

        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, Paths(r));
        Assert.AreSame(current[1], r.Items[2], "the listed 3 keeps its identity");
    }

    [TestMethod]
    public void A_file_replaced_in_place_comes_back_as_a_new_item()
    {
        var current = Items("1", "2", "3");
        var r = Apply(current, removed: ["2"], added: ["2"]);

        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, Paths(r));
        Assert.AreNotSame(current[1], r.Items[1], "a replaced file must be reloaded, not reuse old bitmaps");
        Assert.AreSame(current[1], r.Removed.Single());
    }

    [TestMethod]
    public void Rename_keeps_identity_and_moves_to_the_new_sorted_position()
    {
        var current = Items("1", "2", "3", "4");
        var two = current[1];
        var r = Apply(current, renamed: new() { ["2"] = "9" });

        CollectionAssert.AreEqual(new[] { "1", "3", "4", "9" }, Paths(r));
        Assert.AreSame(two, r.Items[3]);
        Assert.AreEqual(0, r.Removed.Count);
    }

    [TestMethod]
    public void Rename_target_also_offered_as_an_addition_is_not_duplicated()
    {
        var r = Apply(Items("1", "2"), renamed: new() { ["2"] = "5" }, added: ["5"]);

        CollectionAssert.AreEqual(new[] { "1", "5" }, Paths(r));
    }

    [TestMethod]
    public void Rename_of_an_unlisted_file_changes_nothing_unless_its_new_name_is_added()
    {
        var current = Items("1", "2");

        Assert.IsFalse(Apply(current, renamed: new() { ["7"] = "8" }).Changed);
        CollectionAssert.AreEqual(new[] { "1", "2", "8" },
            Paths(Apply(current, renamed: new() { ["7"] = "8" }, added: ["8"])));
    }

    [TestMethod]
    public void Case_only_rename_updates_the_path()
    {
        var current = Items("a.png");
        var r = Apply(current, renamed: new() { ["a.png"] = "A.png" }, added: ["A.png"]);

        CollectionAssert.AreEqual(new[] { "A.png" }, Paths(r));
        Assert.AreSame(current[0], r.Items[0]);
    }

    [TestMethod]
    public void Remove_rename_and_add_in_one_batch()
    {
        var r = Apply(Items("1", "3", "5", "7"), removed: ["3"], renamed: new() { ["7"] = "2" }, added: ["6", "8"]);

        CollectionAssert.AreEqual(new[] { "1", "2", "5", "6", "8" }, Paths(r));
    }

    [TestMethod]
    public void A_batch_that_would_empty_the_list_keeps_the_current_item()
    {
        var current = Items("1", "2", "3");
        var r = Apply(current, removed: ["1", "2", "3"], keep: current[1]);

        CollectionAssert.AreEqual(new[] { "2" }, Paths(r));
        CollectionAssert.AreEqual(new[] { "1", "3" }, r.Removed.Select(i => i.Path).ToArray());
    }

    [TestMethod]
    public void Keep_rule_does_not_apply_when_something_else_remains()
    {
        var current = Items("1", "2");
        var r = Apply(current, removed: ["1", "2"], added: ["x"], keep: current[0]);

        CollectionAssert.AreEqual(new[] { "x" }, Paths(r), "a folder switch replaces everything");
        Assert.AreEqual(2, r.Removed.Count);
    }

    [TestMethod]
    public void A_batch_with_no_effect_reports_unchanged()
    {
        var r = Apply(Items("1", "2"), removed: ["9"], added: ["1", "2"]);

        Assert.IsFalse(r.Changed);
        Assert.AreEqual(0, r.Removed.Count);
    }

    [TestMethod]
    public void Removal_only_needs_no_sort_order()
    {
        var current = Items("b", "a"); // deliberately not sorted: removal must not reorder anything
        var r = ListChange.Apply(current, i => i.Path, p => p == "a", null, [], order: null,
            create: p => new Item(p), rename: (i, p) => i.Path = p, isKeep: _ => false);

        CollectionAssert.AreEqual(new[] { "b" }, Paths(r));
    }

    [TestMethod]
    public void Adding_to_an_empty_list()
    {
        CollectionAssert.AreEqual(new[] { "1", "2" }, Paths(Apply([], added: ["2", "1"])));
    }

    /// <summary>
    /// Random batches checked against a simple model (a sorted set of paths) and against identity rules:
    /// survivors and renamed items keep their object, everything new is a fresh object.
    /// </summary>
    [TestMethod]
    public void Random_batches_match_a_simple_model()
    {
        var rng = new Random(20261104);
        for (int round = 0; round < 2000; round++)
        {
            var names = Enumerable.Range(0, 40).Select(i => $"f{i}.png").ToList();
            var current = Items(names.OrderBy(_ => rng.Next()).Take(rng.Next(0, 15)).ToArray());
            current.Sort((a, b) => Order.Compare(a.Path, b.Path));
            var before = current.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            var listed = before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unlisted = names.Where(n => !listed.Contains(n)).ToList();

            var removed = listed.Where(_ => rng.Next(4) == 0).ToArray();
            var renamed = new Dictionary<string, string>();
            foreach (var p in listed.Except(removed).Where(_ => rng.Next(6) == 0))
                if (unlisted.Count > 0)
                {
                    var target = unlisted[rng.Next(unlisted.Count)];
                    unlisted.Remove(target);
                    renamed[p] = target;
                }
            var added = names.Where(_ => rng.Next(5) == 0).ToArray(); // may repeat listed or renamed names
            var keep = current.Count > 0 ? current[rng.Next(current.Count)] : null;

            var r = Apply(current, removed, renamed, added, keep);

            var model = new SortedSet<string>(Order);
            foreach (var p in listed.Except(removed))
                model.Add(renamed.TryGetValue(p, out var to) ? to : p);
            foreach (var p in added) model.Add(p); // a removed path added back is a replaced file
            if (model.Count == 0 && keep != null) model.Add(keep.Path);

            CollectionAssert.AreEqual(model.ToArray(), Paths(r), $"round {round}");
            Assert.AreEqual(r.Items.Count, r.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                $"round {round}: duplicate paths");
            foreach (var item in r.Items)
            {
                // A renamed photo carries its old identity; a new file at a renamed-away name is a new item.
                var source = renamed.FirstOrDefault(kv => kv.Value == item.Path).Key;
                var originalPath = source ?? item.Path;
                bool survivor = listed.Contains(originalPath) && !removed.Contains(originalPath) &&
                                (source != null || !renamed.ContainsKey(originalPath));
                if (survivor) Assert.AreSame(before[originalPath], item, $"round {round}: {item} lost its identity");
                else if (before.TryGetValue(item.Path, out var old) && source == null)
                    Assert.AreNotSame(old, item, $"round {round}: {item} should be a new item");
            }
        }
    }

    // --- Where the viewer lands ---

    private static int PositionAfter(int[] oldKeys, int oldPosition, int[] keys) =>
        ListChange.PositionAfter(oldKeys, oldPosition, keys, oldKeys.ElementAtOrDefault(oldPosition), keys.Contains);

    [TestMethod]
    public void Viewer_follows_its_photo_when_others_shift_it()
    {
        // Viewing key 9 at position 3; two photos are inserted before it.
        Assert.AreEqual(5, PositionAfter([1, 2, 3, 9, 10], 3, [1, 2, 20, 3, 21, 9, 10]));
    }

    [TestMethod]
    public void Removed_photo_lands_on_the_next_survivor()
    {
        Assert.AreEqual(2, PositionAfter([1, 2, 3, 4, 5], 2, [1, 2, 4, 5]));
    }

    [TestMethod]
    public void A_run_of_removed_photos_is_skipped_forwards()
    {
        Assert.AreEqual(1, PositionAfter([1, 2, 3, 4, 5], 1, [1, 5]));
    }

    [TestMethod]
    public void Removed_last_photo_lands_on_the_previous_survivor()
    {
        Assert.AreEqual(2, PositionAfter([1, 2, 3, 4], 3, [1, 2, 3]));
    }

    [TestMethod]
    public void Only_new_photos_left_lands_on_the_first()
    {
        Assert.AreEqual(0, PositionAfter([1, 2], 1, [7, 8, 9]));
    }

    [TestMethod]
    public void Photo_replaced_in_place_keeps_the_viewer_on_it()
    {
        // Viewing key 3 (position 2); an editor saved over it, so key 3 is gone and key 7 has the same path.
        int[] keys = [1, 2, 7, 4];

        Assert.AreEqual(2, ListChange.PositionAfter([1, 2, 3, 4], 2, keys, currentKey: 3, keys.Contains,
            isReplacement: k => k == 7));
    }

    [TestMethod]
    public void Invalid_old_position_falls_back_to_the_first()
    {
        Assert.AreEqual(0, ListChange.PositionAfter([1, 2], -1, [3, 4], currentKey: 99, isListed: _ => false));
    }
}

[TestClass]
public sealed class KeyWindowTests
{
    [TestMethod]
    public void Range_clamps_to_the_list()
    {
        Assert.AreEqual((0, 3), KeyWindow.Range(10, 1, 2));
        Assert.AreEqual((6, 9), KeyWindow.Range(10, 8, 2));
        Assert.AreEqual((5, 5), KeyWindow.Range(10, 5, 0));
        Assert.AreEqual((0, 9), KeyWindow.Range(10, 0, 100));
    }

    [TestMethod]
    public void IndexOf_searches_only_the_window()
    {
        int[] keys = [40, 7, 99, 3, 12, 5];

        Assert.AreEqual(3, KeyWindow.IndexOf(keys, 3, centre: 2, half: 1));
        Assert.AreEqual(-1, KeyWindow.IndexOf(keys, 5, centre: 2, half: 1), "outside the window");
        Assert.AreEqual(-1, KeyWindow.IndexOf(keys, 1000, centre: 2, half: 10), "absent");
        Assert.AreEqual(-1, KeyWindow.IndexOf([], 1, centre: 0, half: 3), "empty list");
    }
}
