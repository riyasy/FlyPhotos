#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Services;

/// <summary>
/// The comparison Explorer itself sorts filenames with: digit runs compare as numbers, so
/// "(3)" sorts before "(12)". Ordinal comparison puts '1' before '3' and gives 1, 12, 3.
/// </summary>
internal sealed partial class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string psz1, string psz2);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
}

/// <summary>A file and the metadata Folder mode can sort on, captured in one file-system read.</summary>
internal readonly record struct FileStamp(string Path, long LastWriteUtcTicks, long CreationUtcTicks, long Length);

/// <summary>
/// Orders the files of ONE folder for Folder mode. The date/size each file is sorted on is captured when
/// the file is listed or added (<see cref="Record"/>), so a comparison never touches the disk. Ties fall
/// back to the natural name order, which keeps the order stable. Not thread-safe: one owner thread
/// records and compares.
/// </summary>
internal sealed class FolderSortComparer(FolderSortBy sortBy, bool descending) : IComparer<string>
{
    private readonly Dictionary<string, long> _values = new(StringComparer.OrdinalIgnoreCase);

    public FolderSortBy SortBy { get; } = sortBy;

    /// <summary>An empty comparer with the same sort settings, for listing a folder afresh.</summary>
    public FolderSortComparer NewEmpty() => new(SortBy, descending);

    /// <summary>Remembers the value <paramref name="file"/> sorts on. A no-op for Name, which needs none.</summary>
    public void Record(in FileStamp file)
    {
        if (SortBy != FolderSortBy.Name) _values[file.Path] = ValueOf(file);
    }

    /// <summary>A rename keeps the file's date and size, so its recorded value moves to the new name.</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (_values.Remove(oldPath, out var value)) _values[newPath] = value;
    }

    public int Compare(string? x, string? y) => Compare(x, Value(x), y, Value(y));

    /// <summary>Compares two files whose sort values are already at hand (no dictionary lookups).</summary>
    public int Compare(in FileStamp x, in FileStamp y) => Compare(x.Path, ValueOf(x), y.Path, ValueOf(y));

    private int Compare(string? x, long xValue, string? y, long yValue)
    {
        int c = xValue.CompareTo(yValue);
        // Every path lives in the same folder, so comparing whole paths orders by file name
        // without cutting the name out of each path.
        if (c == 0)
            c = NaturalComparer.Instance.Compare(x, y);
        return descending ? -c : c;
    }

    // The one place that says what each sort key means. Name sorts on the name alone (value 0 for all).
    private long ValueOf(in FileStamp file) => SortBy switch
    {
        FolderSortBy.DateModified => file.LastWriteUtcTicks,
        FolderSortBy.DateCreated => file.CreationUtcTicks,
        FolderSortBy.Size => file.Length,
        _ => 0
    };

    private long Value(string? path) => path != null && _values.TryGetValue(path, out var v) ? v : 0;
}

/// <summary>
/// Folder mode listing: one folder, never recursive, filtered to supported extensions and sorted by
/// <see cref="FolderSortComparer"/>. Also finds the sibling folder for Ctrl+PageUp / Ctrl+PageDown.
/// Pure file-system code with no UI dependencies, so it is unit tested directly.
/// </summary>
internal static class FolderListing
{
    /// <summary>
    /// How Fly lists the files of a folder, in either mode: not recursive, and hidden and system files
    /// included, matching what Explorer-driven discovery has always returned.
    /// </summary>
    internal static readonly EnumerationOptions FileOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    // Folders keep the default skip of hidden + system, so $RECYCLE.BIN and friends are never a sibling folder.
    private static readonly EnumerationOptions FolderOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    /// <summary>
    /// Lists the supported images directly inside <paramref name="folder"/>, sorted by <paramref name="order"/>,
    /// recording each file's sort value into it. <paramref name="alwaysIncludeName"/> (a bare file name) is kept
    /// even if its extension is not supported, so a file the user explicitly opened is never lost.
    /// </summary>
    /// <param name="extensions">Supported extensions with leading dot; must use a case-insensitive comparer.</param>
    public static string[] ListFolder(string folder, HashSet<string> extensions, FolderSortComparer order,
        string? alwaysIncludeName = null)
    {
        if (!Directory.Exists(folder)) return [];

        var stamps = new List<FileStamp>();
        foreach (var stamp in SupportedFiles(folder, extensions, alwaysIncludeName,
                     static (ref FileSystemEntry e) => new FileStamp(e.ToFullPath(),
                         e.LastWriteTimeUtc.UtcTicks, e.CreationTimeUtc.UtcTicks, e.Length)))
            stamps.Add(stamp);

        // Sort the stamps themselves, so each comparison has its values at hand instead of looking both
        // paths up; record them afterwards for files added later.
        stamps.Sort((a, b) => order.Compare(a, b));
        var result = new string[stamps.Count];
        for (int i = 0; i < result.Length; i++)
        {
            order.Record(stamps[i]);
            result[i] = stamps[i].Path;
        }
        return result;
    }

    /// <summary>
    /// The nearest folder before or after <paramref name="currentFolder"/> among its parent's subfolders
    /// (natural name order) that contains at least one supported image, or null at either end.
    /// </summary>
    public static string? FindSiblingFolder(string currentFolder, NavDirection direction, HashSet<string> extensions)
    {
        var current = Path.TrimEndingDirectorySeparator(currentFolder);
        var parent = Path.GetDirectoryName(current);
        if (parent == null || !Directory.Exists(parent)) return null;

        var siblings = Directory.GetDirectories(parent, "*", FolderOptions);
        Array.Sort(siblings, NaturalComparer.Instance);

        int index = Array.FindIndex(siblings, s => s.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        int step = direction == NavDirection.Next ? 1 : -1;
        for (int i = index + step; i >= 0 && i < siblings.Length; i += step)
            if (ContainsSupportedFile(siblings[i], extensions))
                return siblings[i];
        return null;
    }

    /// <summary>Stops at the first supported file, so a big folder costs one directory read at most.</summary>
    private static bool ContainsSupportedFile(string folder, HashSet<string> extensions)
    {
        try
        {
            foreach (var _ in SupportedFiles(folder, extensions, null, static (ref FileSystemEntry _) => true))
                return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    // The supported files directly in a folder, filtered on the span of each name (no string per rejected file).
    private static FileSystemEnumerable<T> SupportedFiles<T>(string folder, HashSet<string> extensions,
        string? alwaysIncludeName, FileSystemEnumerable<T>.FindTransform transform)
    {
        var lookup = extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        return new FileSystemEnumerable<T>(folder, transform, FileOptions)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory &&
                (lookup.Contains(Path.GetExtension(e.FileName)) ||
                 (alwaysIncludeName != null && e.FileName.Equals(alwaysIncludeName, StringComparison.OrdinalIgnoreCase)))
        };
    }

    /// <summary>
    /// Merges <paramref name="addedSorted"/> into <paramref name="current"/>; both must be sorted by
    /// <paramref name="order"/>. Each insertion point is binary-searched and the runs between them are block
    /// copied, so a few additions to a long list cost A·log N comparisons, not N.
    /// </summary>
    public static List<T> MergeSorted<T>(List<T> current, IReadOnlyList<T> addedSorted, IComparer<T> order)
    {
        var result = new List<T>(current.Count + addedSorted.Count);
        var source = CollectionsMarshal.AsSpan(current);
        int from = 0;
        foreach (var item in addedSorted)
        {
            int at = current.BinarySearch(from, current.Count - from, item, order);
            if (at < 0) at = ~at;
            result.AddRange(source[from..at]);
            result.Add(item);
            from = at;
        }
        result.AddRange(source[from..]);
        return result;
    }
}
