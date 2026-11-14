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
    // Non-null only in Folder mode. Created by the startup thread before the listing is published (it lists the
    // folder); from then on touched only on the UI thread, inside _listChangeGate. Null again after Dispose.
    private FolderSource? _folder;

    // Completed by PublishListing (UI thread). Watcher batches and folder switches wait for it, so none is applied to
    // the startup list (the opened photo alone) before the real listing is published. Never completed for a
    // secondary instance, which has no Folder mode.
    private readonly TaskCompletionSource _listingReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // HQ loading waits until folder switching has paused this long, so skipping through many folders decodes
    // full-quality images only in the folder the user stops at. Previews still load in every folder.
    private const int FolderSwitchQuietMs = 500;

    // The list comes from one folder sorted by Fly (rather than Explorer's view). Read once, at startup.
    private static bool IsFolderMode => AppConfig.Settings.ListSource == ListSource.Folder;

    // Thread-pool thread: the folder watcher has a batch of changes. Waits for the listing, then hands the batch to the
    // UI thread. async void as an event handler; it catches everything itself.
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

    // UI thread. Turns a watcher batch into a list change and applies it, inside the list-change gate. The photo on
    // screen is kept by name even if a rescan no longer lists it (an unsupported extension, as at startup).
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

    // True while a folder switch is waiting for the list-change gate or running. UI thread only. A second switch press
    // meanwhile is dropped, so holding Ctrl+PageDown can't queue up a long run of switches.
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

    // UI thread, from Dispose. Stops the watcher; a batch already queued then finds _folder null and does nothing.
    private void DisposeFolderMode()
    {
        _folder?.Dispose();
        _folder = null;
    }
}
