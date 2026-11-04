#nullable enable
using System;
using System.Collections.Generic;
using FlyPhotos.Services;

namespace FlyPhotos.Display.State;

/// <summary>
/// The rules for changing the photo list while it is open (a <b>list change</b>): which items survive, which
/// are renamed in place, which paths are genuinely new, and where the viewer lands afterwards. Pure — no
/// photos, bitmaps or threads — so every edge case is unit tested; <see cref="PhotoList"/> supplies the
/// item-specific parts as delegates.
/// </summary>
internal static class ListChange
{
    /// <summary>The outcome of <see cref="Apply{T}"/>: the new order, the removed items, and whether anything changed.</summary>
    public readonly record struct Result<T>(List<T> Items, List<T> Removed, bool Changed);

    /// <summary>
    /// Applies one change batch to <paramref name="current"/> (sorted by <paramref name="order"/> whenever
    /// something is renamed or added):
    /// <list type="number">
    ///   <item>drops every item whose path <paramref name="isRemoved"/> flags;</item>
    ///   <item>renames the items <paramref name="renamed"/> maps (old path -> new path) — the same item, moved to
    ///     its new sorted position;</item>
    ///   <item>creates items for <paramref name="addedSorted"/> (sorted by <paramref name="order"/>) and inserts
    ///     them at their sorted positions, skipping paths already present or repeated in the batch.</item>
    /// </list>
    /// Paths compare case-insensitively, as Windows does. If the batch would leave nothing, the item
    /// <paramref name="isKeep"/> picks is kept, so the viewer always has something to show.
    /// </summary>
    public static Result<T> Apply<T>(IReadOnlyList<T> current, Func<T, string> pathOf,
        Predicate<string> isRemoved, IReadOnlyDictionary<string, string>? renamed, IReadOnlyList<string> addedSorted,
        IComparer<string>? order, Func<string, T> create, Action<T, string> rename, Predicate<T> isKeep)
    {
        var removed = new List<T>();
        var survivors = new List<T>(current.Count);
        var moved = new List<T>();
        // Sized by the additions, not the list: a path still listed is crossed off, the rest are new.
        var newPaths = new HashSet<string>(addedSorted, StringComparer.OrdinalIgnoreCase);
        foreach (var item in current)
        {
            var path = pathOf(item);
            if (isRemoved(path))
            {
                removed.Add(item);
                continue;
            }
            if (renamed != null && renamed.TryGetValue(path, out var newPath))
            {
                rename(item, newPath);
                path = newPath;
                moved.Add(item);
            }
            else survivors.Add(item);
            if (newPaths.Count > 0) newPaths.Remove(path);
        }

        foreach (var path in addedSorted)
            if (newPaths.Remove(path)) // false: already listed, or repeated in this batch
                moved.Add(create(path));

        if (survivors.Count == 0 && moved.Count == 0 && removed.FindIndex(isKeep) is >= 0 and var keep)
        {
            survivors.Add(removed[keep]);
            removed.RemoveAt(keep);
        }
        if (removed.Count == 0 && moved.Count == 0) return new Result<T>(survivors, removed, Changed: false);
        if (moved.Count == 0) return new Result<T>(survivors, removed, Changed: true);

        var itemOrder = Comparer<T>.Create((a, b) => order!.Compare(pathOf(a), pathOf(b)));
        // Additions arrive sorted; only renamed items mixed in need a sort (a few at most).
        if (renamed is { Count: > 0 }) moved.Sort(itemOrder);
        return new Result<T>(FolderListing.MergeSorted(survivors, moved, itemOrder), removed, Changed: true);
    }

    /// <summary>
    /// Where the viewer lands after a change: the current key's new position if it is still listed; otherwise
    /// the new key at the same path (<paramref name="isReplacement"/> — the file was replaced in place, e.g. an
    /// editor saved over it, so the viewer stays on it, reloaded); otherwise the first still-listed key after it
    /// in the old order, otherwise the last still-listed one before it, otherwise (only new keys are left) the
    /// first position.
    /// </summary>
    public static int PositionAfter(IReadOnlyList<int> oldKeys, int oldPosition, IReadOnlyList<int> keys,
        int currentKey, Predicate<int> isListed, Predicate<int>? isReplacement = null)
    {
        int target = currentKey;
        if (!isListed(currentKey) && isReplacement != null)
            for (int i = 0; i < keys.Count; i++)
                if (isReplacement(keys[i]))
                    return i;
        if (!isListed(currentKey) && oldPosition >= 0 && oldPosition < oldKeys.Count)
        {
            target = -1;
            for (int i = oldPosition + 1; i < oldKeys.Count && target < 0; i++)
                if (isListed(oldKeys[i])) target = oldKeys[i];
            for (int i = oldPosition - 1; i >= 0 && target < 0; i--)
                if (isListed(oldKeys[i])) target = oldKeys[i];
        }
        return Math.Max(0, KeyWindow.IndexOf(keys, target, centre: 0, half: keys.Count));
    }
}
