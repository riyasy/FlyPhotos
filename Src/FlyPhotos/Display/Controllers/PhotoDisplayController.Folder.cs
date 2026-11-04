#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Folder mode (<see cref="ListSource.Folder"/>): the list is one folder sorted by Fly, kept live by a
/// <see cref="FolderWatcher"/>, and Ctrl+PageUp / Ctrl+PageDown swap it for a sibling folder.
/// <para>
/// Threading: the startup thread lists the folder; afterwards every list change runs on the UI thread —
/// the same thread navigation and delete run on — one at a time through <c>_listChangeGate</c>. The
/// comparer and watcher fields are only touched there (startup hands them over through
/// <see cref="_listingReady"/>). The folder-switch stamp is the exception: the HQ worker reads it too.
/// </para>
/// </summary>
internal partial class PhotoDisplayController
{
    // Non-null only in Folder mode.
    private FolderSortComparer? _folderSort;
    private FolderWatcher? _watcher;

    // Watcher batches wait for this so none is applied before the initial list is published.
    private readonly TaskCompletionSource _listingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // HQ loading waits until folder switching has paused this long, so skipping through many folders decodes
    // full-quality images only in the folder the user stops at. Previews still load in every folder.
    private const int FolderSwitchQuietMs = 500;

    // When the last folder switch happened (Environment.TickCount64). Written on the UI thread, read by the HQ
    // worker and the wake-up timer, hence Interlocked.
    private long _lastFolderSwitchTicks = long.MinValue / 2;

    private static bool IsFolderMode => AppConfig.Settings.ListSource == ListSource.Folder;

    /// <summary>
    /// Startup thread. Starts watching BEFORE listing, so a file created while the folder is being listed
    /// is still reported (and ignored if the listing already had it). The opened file is always listed.
    /// </summary>
    private IReadOnlyList<string> ListFolderAndStartWatching(string selectedFilePath)
    {
        _folderSort = new FolderSortComparer(AppConfig.Settings.FolderSortBy, AppConfig.Settings.FolderSortDescending);
        if (Path.GetDirectoryName(selectedFilePath) is not { } folder) return [selectedFilePath];

        _watcher = StartWatcher(folder);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var files = FolderListing.ListFolder(folder, CodecDiscovery.SupportedExtensions, _folderSort,
            Path.GetFileName(selectedFilePath));
        Logger.Trace($"Listed {files.Length} files from folder in " +
                     $"{System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");
        return files.Length == 0 ? [selectedFilePath] : files;
    }

    private FolderWatcher? StartWatcher(string folder)
    {
        try
        {
            var watcher = new FolderWatcher(folder, FileDiscovery.IsSupported);
            watcher.Changed += Watcher_Changed;
            return watcher;
        }
        catch (Exception ex)
        {
            // Some locations (certain network shares) cannot be watched; the list then simply stays static.
            Logger.Warn(ex, $"Cannot watch folder {folder}");
            return null;
        }
    }

    // ThreadPool thread.
    private async void Watcher_Changed(object? sender, FolderChanges changes)
    {
        try
        {
            await _listingReady.Task;
            if (sender is FolderWatcher watcher)
                _d2dCanvas.DispatcherQueue.TryEnqueue(() => _ = ApplyWatcherChangesAsync(watcher, changes));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // UI thread.
    private async Task ApplyWatcherChangesAsync(FolderWatcher watcher, FolderChanges changes)
    {
        await _listChangeGate.WaitAsync();
        try
        {
            // A batch from the watcher of a folder we have since left is stale.
            if (_cts.IsCancellationRequested || !ReferenceEquals(watcher, _watcher) || _folderSort == null) return;

            if (changes.Rescan)
            {
                // Events were lost: list again with a fresh comparer (off the UI thread), then diff. The photo
                // on screen is kept even if its extension is unsupported, as at startup.
                var fresh = _folderSort.NewEmpty();
                var keepName = Path.GetFileName(_photoList.GetPhoto(_photoSessionState.CurrentPhotoKey)?.FilePath);
                var listed = await Task.Run(() =>
                    FolderListing.ListFolder(watcher.Folder, CodecDiscovery.SupportedExtensions, fresh, keepName));
                if (!ReferenceEquals(watcher, _watcher)) return;
                _folderSort = fresh;
                var onDisk = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
                await ApplyListChangeAsync(path => !onDisk.Contains(path), renamed: null, listed, _folderSort,
                    goToFirst: false);
                return;
            }

            Dictionary<string, string>? renamed = null;
            foreach (var (oldPath, newPath) in changes.Renamed)
            {
                _folderSort.Rename(oldPath, newPath);
                (renamed ??= new(StringComparer.OrdinalIgnoreCase))[oldPath] = newPath;
            }
            // Rename targets are offered as additions too: one whose old name was never listed still appears,
            // and one that was renamed in place is skipped as already listed.
            var added = new string[changes.Added.Count + changes.Renamed.Count];
            for (int i = 0; i < changes.Added.Count; i++)
            {
                _folderSort.Record(changes.Added[i]);
                added[i] = changes.Added[i].Path;
            }
            for (int i = 0; i < changes.Renamed.Count; i++)
                added[changes.Added.Count + i] = changes.Renamed[i].NewPath;
            Array.Sort(added, _folderSort);
            var removed = new HashSet<string>(changes.Removed, StringComparer.OrdinalIgnoreCase);
            await ApplyListChangeAsync(removed.Contains, renamed, added, _folderSort, goToFirst: false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
        finally
        {
            _listChangeGate.Release();
        }
    }

    // True while a folder switch is waiting for the list-change gate or running. UI thread only.
    private bool _siblingSwitchPending;

    /// <summary>
    /// UI thread. Opens the previous/next sibling folder that has images and shows its first photo.
    /// Returns the opened folder's name, or null when nothing was opened: there is no such folder (the strip
    /// bounces) or another folder switch is already waiting or running (the press is dropped, so a held key
    /// can't queue up switches). A press is never dropped just because a watcher batch is being applied.
    /// </summary>
    public async Task<string?> TryOpenSiblingFolderAsync(NavDirection direction)
    {
        if (_folderSort == null || _watcher == null || !_listingReady.Task.IsCompleted || _siblingSwitchPending)
            return null;
        _siblingSwitchPending = true;
        try
        {
            await _listChangeGate.WaitAsync();
            try
            {
                return await OpenSiblingFolderAsync(direction);
            }
            finally
            {
                _listChangeGate.Release();
            }
        }
        finally
        {
            _siblingSwitchPending = false;
        }
    }

    // Inside the gate.
    private async Task<string?> OpenSiblingFolderAsync(NavDirection direction)
    {
        if (_folderSort == null || _watcher == null || _cts.IsCancellationRequested) return null;
        var from = _watcher.Folder;
        var order = _folderSort.NewEmpty();

        // Find, watch and list the sibling off the UI thread; nothing shared is touched in here. The sibling is
        // watched before it is listed, as at startup. A folder that emptied between the check and the listing is
        // passed over like any folder without photos.
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

        if (next.Folder == null || _cts.IsCancellationRequested)
        {
            next.Watcher?.Dispose();
            if (!_cts.IsCancellationRequested) _thumbNailController.BounceAtEdge(direction);
            return null;
        }

        _watcher.Dispose();
        _watcher = next.Watcher;
        _folderSort = order;
        StartFolderSwitchQuietPeriod();
        await ApplyListChangeAsync(static _ => true, renamed: null, next.Files, order, goToFirst: true);
        return Path.GetFileName(next.Folder);
    }

    // Any thread (the HQ worker asks). Before the first switch the stamp is far in the past, so the subtraction
    // can't overflow and launch is never held.
    private bool InFolderSwitchQuietPeriod() =>
        Environment.TickCount64 - Interlocked.Read(ref _lastFolderSwitchTicks) < FolderSwitchQuietMs;

    // UI thread. Holds the HQ worker, which re-checks the burst predicate after every wake-up, and wakes it
    // once switching stops. Each switch schedules a wake-up; only the one whose stamp is still the latest
    // sends it. Comparing stamps rather than elapsed time means a timer firing a tick early can't drop it.
    private void StartFolderSwitchQuietPeriod()
    {
        long stamp = Environment.TickCount64;
        Interlocked.Exchange(ref _lastFolderSwitchTicks, stamp);
        _ = Task.Delay(FolderSwitchQuietMs).ContinueWith(_ =>
        {
            if (Interlocked.Read(ref _lastFolderSwitchTicks) != stamp || _cts.IsCancellationRequested) return;
            try
            {
                _cache.SignalHqLoading();
            }
            catch (ObjectDisposedException)
            {
                // The window closed between the check and the signal.
            }
        }, TaskScheduler.Default);
    }

    private void DisposeFolderMode()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
