#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Folder mode (<see cref="ListSource.Folder"/>): the list comes from a <see cref="FolderSource"/> — one folder
/// sorted by Fly, kept live by its watcher — and Ctrl+PageUp / Ctrl+PageDown swap it for a sibling folder.
/// <para>
/// Threading: the startup thread lists the folder; afterwards every list change runs on the UI thread — the same
/// thread navigation and delete run on — one at a time through <c>_listChangeGate</c>. The folder source is only
/// touched there (startup hands it over through <see cref="_listingReady"/>).
/// </para>
/// </summary>
internal partial class PhotoDisplayController
{
    // Non-null only in Folder mode.
    private FolderSource? _folder;

    // Watcher batches wait for this so none is applied before the initial list is published.
    private readonly TaskCompletionSource _listingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // HQ loading waits until folder switching has paused this long, so skipping through many folders decodes
    // full-quality images only in the folder the user stops at. Previews still load in every folder.
    private const int FolderSwitchQuietMs = 500;

    private static bool IsFolderMode => AppConfig.Settings.ListSource == ListSource.Folder;

    // ThreadPool thread.
    private async void Watcher_Changed(FolderWatcher watcher, FolderChanges changes)
    {
        try
        {
            await _listingReady.Task;
            PostToUi(() => _ = ApplyWatcherChangesAsync(watcher, changes));
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
            if (_cts.IsCancellationRequested || _folder == null || !_folder.IsCurrent(watcher)) return;
            var keepName = Path.GetFileName(_photoList.GetPhoto(_nav.Key)?.FilePath);
            if (await _folder.ToBatchAsync(watcher, changes, keepName) is { } batch)
                await ApplyListChangeAsync(batch, goToFirst: false);
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
        if (_folder is not { CanSwitch: true } || !_listingReady.Task.IsCompleted || _siblingSwitchPending)
            return null;
        _siblingSwitchPending = true;
        try
        {
            await _listChangeGate.WaitAsync();
            try
            {
                if (_cts.IsCancellationRequested) return null;
                if (await _folder.SwitchToSiblingAsync(direction) is not var (name, batch))
                {
                    if (!_cts.IsCancellationRequested) _thumbNailController.BounceAtEdge(direction);
                    return null;
                }
                _cache.HoldHqFor(FolderSwitchQuietMs);
                await ApplyListChangeAsync(batch, goToFirst: true);
                return name;
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

    private void DisposeFolderMode()
    {
        _folder?.Dispose();
        _folder = null;
    }
}
