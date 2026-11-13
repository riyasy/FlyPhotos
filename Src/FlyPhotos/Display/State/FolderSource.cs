#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using NLog;

namespace FlyPhotos.Display.State;

/// <summary>
/// One <b>list change</b>, ready for <see cref="PhotoList.ApplyChanges"/>: paths removed, renamed (old -> new) and
/// added (sorted by <see cref="Order"/>, which is null when nothing is added or renamed).
/// </summary>
internal sealed record ListChangeBatch(Predicate<string> IsRemoved, IReadOnlyDictionary<string, string>? Renamed,
    IReadOnlyList<string> Added, IComparer<string>? Order)
{
    /// <summary>The file at <paramref name="path"/> is gone (deleted in Fly).</summary>
    public static ListChangeBatch Remove(string path) =>
        new(p => p.Equals(path, StringComparison.OrdinalIgnoreCase), null, [], null);
}

/// <summary>
/// Where Folder mode's photo list comes from: one folder sorted by Fly (<see cref="FolderSortComparer"/>), kept live
/// by a <see cref="FolderWatcher"/>, and swapped for a <b>sibling folder</b> on request. It turns what happens on
/// disk into <see cref="ListChangeBatch"/>es; the controller applies them.
/// <para>
/// Threading: the startup thread calls <see cref="ListAndWatch"/>; after that every call is on the UI thread, one at
/// a time (the controller's list-change gate). <see cref="Changed"/> fires on a thread-pool thread.
/// </para>
/// </summary>
internal sealed class FolderSource : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private FolderSortComparer _order = new(AppConfig.Settings.FolderSortBy, AppConfig.Settings.FolderSortDescending);
    private FolderWatcher? _watcher;

    /// <summary>Fired on a thread-pool thread with a batch from the watcher of <paramref name="watcher"/>'s folder.</summary>
    public event Action<FolderWatcher, FolderChanges>? Changed;

    /// <summary>
    /// Startup thread. Starts watching BEFORE listing, so a file created while the folder is being listed is still
    /// reported (and ignored if the listing already had it). The opened file is always listed.
    /// </summary>
    public IReadOnlyList<string> ListAndWatch(string selectedFilePath)
    {
        if (Path.GetDirectoryName(selectedFilePath) is not { } folder) return [selectedFilePath];

        _watcher = StartWatcher(folder);
        long start = Stopwatch.GetTimestamp();
        var files = FolderListing.ListFolder(folder, CodecDiscovery.SupportedExtensions, _order,
            Path.GetFileName(selectedFilePath));
        Logger.Trace($"Listed {files.Length} files from folder in " +
                     $"{Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");
        return files.Length == 0 ? [selectedFilePath] : files;
    }

    /// <summary>Whether a batch from <paramref name="watcher"/> is for the folder shown now (not one since left).</summary>
    public bool IsCurrent(FolderWatcher watcher) => ReferenceEquals(watcher, _watcher);

    /// <summary>
    /// UI thread. The list change for a watcher batch. When events were lost (<see cref="FolderChanges.Rescan"/>) the
    /// folder is listed again (off the UI thread) and diffed; the photo on screen (<paramref name="keepName"/>) is kept
    /// even if its extension is unsupported, as at startup. Null when the folder was left meanwhile.
    /// </summary>
    public async Task<ListChangeBatch?> ToBatchAsync(FolderWatcher watcher, FolderChanges changes, string? keepName)
    {
        if (changes.Rescan)
        {
            var fresh = _order.NewEmpty();
            var listed = await Task.Run(() =>
                FolderListing.ListFolder(watcher.Folder, CodecDiscovery.SupportedExtensions, fresh, keepName));
            if (!IsCurrent(watcher)) return null;
            _order = fresh;
            var onDisk = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
            return new ListChangeBatch(path => !onDisk.Contains(path), null, listed, _order);
        }

        Dictionary<string, string>? renamed = null;
        foreach (var (oldPath, newPath) in changes.Renamed)
        {
            _order.Rename(oldPath, newPath);
            (renamed ??= new(StringComparer.OrdinalIgnoreCase))[oldPath] = newPath;
        }
        // Rename targets are offered as additions too: one whose old name was never listed still appears,
        // and one that was renamed in place is skipped as already listed.
        var added = new string[changes.Added.Count + changes.Renamed.Count];
        for (int i = 0; i < changes.Added.Count; i++)
        {
            _order.Record(changes.Added[i]);
            added[i] = changes.Added[i].Path;
        }
        for (int i = 0; i < changes.Renamed.Count; i++)
            added[changes.Added.Count + i] = changes.Renamed[i].NewPath;
        Array.Sort(added, _order);
        var removed = new HashSet<string>(changes.Removed, StringComparer.OrdinalIgnoreCase);
        return new ListChangeBatch(removed.Contains, renamed, added, _order);
    }

    /// <summary>
    /// UI thread. The list change for a rename made in Fly: the photo moves to its new sorted place (the watcher's
    /// own report of the rename then finds nothing left to do).
    /// </summary>
    public ListChangeBatch Rename(string oldPath, string newPath)
    {
        _order.Rename(oldPath, newPath);
        return new ListChangeBatch(static _ => false,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldPath] = newPath }, [], _order);
    }

    /// <summary>
    /// UI thread. Moves to the previous/next sibling folder that has photos and returns its name and the list change
    /// that replaces the whole list; null when there is none. The sibling is found, watched and listed off the UI
    /// thread (watched before listed, as at startup); a folder that emptied meanwhile is passed over.
    /// </summary>
    public async Task<(string Name, ListChangeBatch Batch)?> SwitchToSiblingAsync(NavDirection direction)
    {
        if (_watcher == null) return null;
        var from = _watcher.Folder;
        var order = _order.NewEmpty();
        var next = await Task.Run(() =>
        {
            for (var folder = from; ;)
            {
                folder = FolderListing.FindSiblingFolder(folder, direction, CodecDiscovery.SupportedExtensions);
                if (folder == null) return default;
                var watcher = StartWatcher(folder);
                var files = FolderListing.ListFolder(folder, CodecDiscovery.SupportedExtensions, order);
                if (files.Length > 0) return (Folder: folder, Watcher: watcher, Files: files);
                watcher?.Dispose();
            }
        });
        if (next.Folder == null) return null;

        _watcher?.Dispose();
        _watcher = next.Watcher;
        _order = order;
        return (Path.GetFileName(next.Folder), new ListChangeBatch(static _ => true, null, next.Files, order));
    }

    /// <summary>Whether Fly can switch folders from here: the folder is watched (siblings are found from it).</summary>
    public bool CanSwitch => _watcher != null;

    private FolderWatcher? StartWatcher(string folder)
    {
        try
        {
            var watcher = new FolderWatcher(folder, FileDiscovery.IsSupported);
            watcher.Changed += (sender, changes) => Changed?.Invoke((FolderWatcher)sender!, changes);
            return watcher;
        }
        catch (Exception ex)
        {
            // Some locations (certain network shares) cannot be watched; the list then simply stays static.
            Logger.Warn(ex, $"Cannot watch folder {folder}");
            return null;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
