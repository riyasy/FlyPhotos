#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.Controllers;

internal partial class PhotoDisplayController
{
    public event Action<string>? CacheStatusChanged;
    public event Action<FileDisplayDetails>? FileNameOrDetailsChanged;
    public event Action? FirstPhotoLoaded;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _firstPhotoLoadedTcs = new();

    private volatile int _keyPressCounter;
    private const int ContinuousPressThreshold = 1;

    // The photo collection: Photo objects + the sorted-key snapshot. Its concurrency model (a
    // ConcurrentDictionary for lock-free reads + a volatile copy-on-write key snapshot) lives inside it.
    private readonly PhotoList _photoList = new();

    // Owns all background loading (preview/HQ/disk tiers, threads, throttling, the sliding window,
    // disk write-back). The controller keeps the photo list and navigation; it drives the cache with
    // MoveWindow and listens to its ready events.
    private readonly PrefetchCache _cache;

    private Photo _firstPhoto;
    private volatile bool _initialFileListingCompleted = false;

    private readonly CanvasAnimatedControl _d2dCanvas;
    private readonly ICanvasController _canvasController;
    private readonly IThumbnailController _thumbNailController;
    private readonly PhotoSessionState _photoSessionState;

    // --- Initialization ---

    public PhotoDisplayController(CanvasAnimatedControl d2dCanvas, ICanvasController canvasController, IThumbnailController thumbNailController,
        PhotoSessionState photoSessionState)
    {
        _d2dCanvas = d2dCanvas;
        _canvasController = canvasController;
        _thumbNailController = thumbNailController;
        _photoSessionState = photoSessionState;

        _cache = new PrefetchCache(_photoList.GetPhoto, _d2dCanvas,
            () => IsContinuousKeyPress() || InFolderSwitchQuietPeriod());
        _cache.PreviewReady += OnPreviewReady;
        _cache.HqReady += OnHqReady;
        _cache.CacheStatusChanged += status => CacheStatusChanged?.Invoke(status);
        _cache.RunAfterQueuedRenderWork = _canvasController.RunAfterQueuedRenderWork;

        _thumbNailController.SetPhotoProvider(_photoList);
        _thumbNailController.SetPreviewLoadedProbe(_cache.IsPreviewLoaded);
        _firstPhoto = Photo.Empty();

        var thread = new Thread(() => { DoStartupActivities(_photoSessionState.FirstPhotoPath, _cts.Token); });
        thread.SetApartmentState(ApartmentState.STA); // This is needed for COM interaction.
        thread.Start();
    }

    public async Task LoadFirstPhoto()
    {
        ImageReader.Initialize(_d2dCanvas);

        _firstPhoto = new Photo(_photoSessionState.FirstPhotoPath);
        bool continueLoadingHq = await _firstPhoto.LoadPreviewFirstPhoto(_d2dCanvas);

        // The open-zoom animation begins on the FIRST SetSource below. Arm the completion wait now
        // so the W2D subscribe is enqueued (FIFO) before that SetSource installs/starts the animation.
        Task openZoomAnimation = AppConfig.Settings.OpenExitZoom
            ? _canvasController.WaitForPanZoomAnimationAsync(Constants.PanZoomAnimationDurationNormal * 2)
            : Task.CompletedTask;

        if (continueLoadingHq)
        {
            await SetSourceAsync(_firstPhoto, DisplayLevel.Preview);
            await _firstPhoto.LoadHqFirstPhoto(_d2dCanvas);
        }

        await SetSourceAsync(_firstPhoto, DisplayLevel.Hq);

        // Wait for the actual animation to finish rather than a fixed delay; already-complete is a no-op.
        await openZoomAnimation;
        _firstPhotoLoadedTcs.SetResult(true);
    }

    private void DoStartupActivities(string selectedFilePath, CancellationToken token)
    {
        // Open the disk cache before the (possibly slow) listing, so the first photo's cache lookup doesn't pay
        // the ~50 ms SQLite start-up. Its own try: a failure here mustn't stop the rest of the start-up.
        try { _ = DiskCacherWithSqlite.Instance; }
        catch (Exception ex) { Logger.Error(ex); }

        try
        {
            // Secondary instances only display the single opened image.
            // No folder or Explorer-window discovery is performed.
            IReadOnlyList<string> files =
                AppConfig.Volatile.IsSecondaryInstance ? [selectedFilePath] :
                IsFolderMode ? ListFolderAndStartWatching(selectedFilePath) :
                FileDiscovery.DiscoverFiles(selectedFilePath, _photoSessionState.FlyLaunchedExternally);

            _photoSessionState.PhotosCount = files.Count;

            _photoList.Initialize(files);

            _firstPhotoLoadedTcs.Task.Wait(token);

            var currentDisplayIndex = FileDiscovery.FindSelectedFileIndex(selectedFilePath, files);
            // In the starting Keys and Index will be same as the keys are continous.
            _photoSessionState.SetCurrentPhotoKeyAndListPosition(currentDisplayIndex, currentDisplayIndex);

            _photoList.Set(_photoSessionState.CurrentPhotoKey, _firstPhoto);
            _cache.SeedHqLoaded(_photoSessionState.CurrentPhotoKey);
            _firstPhoto = Photo.Empty(); // release redundant field reference; _photoList owns it now

            _initialFileListingCompleted = true;

            _cache.MoveWindow(_photoSessionState.CurrentPhotoListPosition, _photoList.Keys, signalHqLoading: true);
            UpdateFileNameAndDetails();

            FirstPhotoLoaded?.Invoke();

            // Secondary instances skip all background caching threads.
            if (AppConfig.Volatile.IsSecondaryInstance) return;

            _cache.Start();
            // Last: from here on, Folder mode list changes may run on the UI thread, so this thread must be
            // done with the list and the cache window (MoveWindow is not safe to run on two threads at once).
            _listingReady.TrySetResult();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // --- Cache events & display upgrade ---

    // The cache fires these on a ThreadPool thread when a load completes. The controller decides
    // whether the freshly-loaded key is the one on screen and upgrades the display if so. This is the
    // single place that used to be UpgradeImageIfNeeded + the thumbnail redraw inside the loader.
    private void OnPreviewReady(int key)
    {
        if (_photoSessionState.CurrentPhotoKey == key && _photoList.GetPhoto(key) is { } photo)
            UpgradeImageIfNeeded(key, photo, DisplayLevel.PlaceHolder, DisplayLevel.Preview);
        _thumbNailController.RedrawThumbNailsIfNeeded(key);
    }

    private void OnHqReady(int key)
    {
        if (_photoSessionState.CurrentPhotoKey == key && _photoList.GetPhoto(key) is { } photo)
            UpgradeImageIfNeeded(key, photo, DisplayLevel.Preview, DisplayLevel.Hq);
    }

    private async Task DisplayPhotoAtKey(int key, bool triggerHqCaching)
    {
        if (_photoList.GetPhoto(key) is not { } photo) return;

        if (_cache.IsHqLoaded(key))
            await SetSourceAsync(photo, DisplayLevel.Hq);
        else if (_cache.IsPreviewLoaded(key))
            await SetSourceAsync(photo, DisplayLevel.Preview);
        else
            await SetSourceAsync(photo, DisplayLevel.PlaceHolder);

        _cache.MoveWindow(_photoSessionState.CurrentPhotoListPosition, _photoList.Keys, signalHqLoading: triggerHqCaching);
    }

    private void UpgradeImageIfNeeded(int key, Photo photo, DisplayLevel fromDisplayState,
        DisplayLevel toDisplayState)
    {
        _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
        {
            // Checked on the UI thread, where navigation, list changes and the display level are written:
            // since the load finished the user may have moved on, the photo may have left the list and been
            // retired (its bitmaps disposed), or the photo may already show a better level (navigating back
            // installs HQ directly), which this upgrade must not step down.
            if (_photoSessionState.CurrentPhotoKey != key || !ReferenceEquals(_photoList.GetPhoto(key), photo) ||
                _photoSessionState.CurrentDisplayLevel != fromDisplayState)
                return;
            _ = SetSourceAsync(photo, toDisplayState);
        });
    }

    private async Task SetSourceAsync(Photo photo, DisplayLevel displayLevel)
    {
        await _canvasController.SetSource(photo, displayLevel);
        UpdateFileNameAndDetails();
    }

    // --- Navigation ---

    public async Task Fly(NavDirection direction)
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        // A list that can't be navigated must never open a burst: the HQ worker parks on
        // IsContinuousKeyPress, and only a Brake() would unwind it.
        Interlocked.Increment(ref _keyPressCounter);
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return; // TODO Should not happen if state is consistent
        int newPosition = currentPosition + (direction == NavDirection.Next ? 1 : -1);
        if (newPosition < 0 || newPosition >= keys.Count)
        {
            _thumbNailController.BounceAtEdge(direction);
            return;
        }
        int newKey = keys[newPosition];
        await FlyTo(newKey, newPosition, false);
    }

    public async Task FlyToFirst()
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        if (_photoSessionState.CurrentPhotoListPosition == 0) _thumbNailController.BounceAtEdge(NavDirection.Prev);
        await FlyTo(keys[0], 0, true);
    }

    public async Task FlyToLast()
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        if (_photoSessionState.CurrentPhotoListPosition == keys.Count - 1) _thumbNailController.BounceAtEdge(NavDirection.Next);
        await FlyTo(keys[^1], keys.Count - 1, true);
    }

    public async Task FlyBy(int shiftBy)
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return;
        int newPosition = Math.Clamp(currentPosition + shiftBy, 0, keys.Count - 1);
        await FlyTo(keys[newPosition], newPosition, true);
    }

    private async Task FlyTo(int toKey, int toPosition, bool triggerHqCaching)
    {
        if (!_photoList.Contains(toKey)) return;
        _photoSessionState.SetCurrentPhotoKeyAndListPosition(toKey, toPosition);
        await DisplayPhotoAtKey(_photoSessionState.CurrentPhotoKey, triggerHqCaching);
    }

    public async Task Brake()
    {
        var currentKey = _photoSessionState.CurrentPhotoKey;
        if (_photoSessionState.CurrentDisplayLevel != DisplayLevel.Hq
            && _cache.IsHqLoaded(currentKey)
            && _photoList.GetPhoto(currentKey) is { } hqPhoto)
            await SetSourceAsync(hqPhoto, DisplayLevel.Hq);
        // Reset before waking: the HQ worker re-checks the burst predicate after every wake-up.
        _keyPressCounter = 0;
        _cache.SignalHqLoading();
    }

    // --- File info ---

    public void UpdateFileNameAndDetails()
    {
        try
        {
            var initialFileListingCompleted = _initialFileListingCompleted;
            var currentKey = _photoSessionState.CurrentPhotoKey;
            var photo = initialFileListingCompleted ? _photoList.GetPhoto(currentKey) : _firstPhoto;

            if (photo == null) return;

            string? positionText = null;
            var fileName = Path.GetFileName(photo.FilePath);
            string? dimensionText = null;

            if (initialFileListingCompleted)
            {
                var currentPosition = _photoSessionState.CurrentPhotoListPosition;
                if (currentPosition < 0) return;
                var totalFileCount = _photoList.Keys.Count;
                positionText = $"[{currentPosition + 1}/{totalFileCount}]";
            }

            var showDimension = AppConfig.Settings.ShowImageDimensions &&
                                _photoSessionState.CurrentDisplayLevel != DisplayLevel.PlaceHolder &&
                                !photo.IsVector &&
                                !photo.IsErrorScreen(_photoSessionState.CurrentDisplayLevel);

            if (showDimension)
            {
                // A multi-page photo reports the page on screen, not the whole file - ICO frames each
                // have their own size, so the file's "actual size" would be wrong on every page but the first.
                if (_photoSessionState.CurrentPage is { } page && page.Path == photo.FilePath)
                {
                    dimensionText = $"({page.Width} x {page.Height} - {page.Index + 1}/{page.Count})";
                }
                else
                {
                    var (w, h) = photo.GetActualSize();
                    dimensionText = $"({w} x {h})";
                }
            }

            FileNameOrDetailsChanged?.Invoke(new FileDisplayDetails(positionText, fileName, dimensionText));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // --- RAW decode settings changed ---

    // Drops cached RAW HQ bitmaps so the new decoder settings take effect, and re-decodes them.
    // Called from the UI thread when a RAW decode setting (DecodeRawData / decoder priority) changes.
    public async Task InvalidateRawHqCache()
    {
        var currentKey = _photoSessionState.CurrentPhotoKey;
        var currentPhoto = _photoList.GetPhoto(currentKey);

        // If a RAW HQ bitmap we're about to dispose is on screen, step the display down first so the
        // renderer isn't holding a disposed CanvasBitmap. OnHqReady upgrades it back once re-decoded.
        if (currentPhoto is { IsRaw: true } &&
            _photoSessionState.CurrentDisplayLevel == DisplayLevel.Hq)
        {
            var level = _cache.IsPreviewLoaded(currentKey)
                ? DisplayLevel.Preview : DisplayLevel.PlaceHolder;
            await SetSourceAsync(currentPhoto, level);
        }

        _cache.InvalidateHqForRawFiles();
    }

    // --- Deletion ---

    public bool CanDeleteCurrentPhoto()
    {
        if (_photoList.Keys.Count == 0) return false;
        if (IsContinuousKeyPress()) return false;
        if (_photoSessionState.CurrentDisplayLevel != DisplayLevel.Hq) return false;
        return true;
    }
    public async Task<DeleteResult> DeleteCurrentPhoto()
    {
        // Serialized with Folder mode list changes, which also move the current photo.
        await _listChangeGate.WaitAsync();
        try
        {
            return await DeleteCurrentPhotoCore();
        }
        finally
        {
            _listChangeGate.Release();
        }
    }

    private async Task<DeleteResult> DeleteCurrentPhotoCore()
    {
        // --- Step 1: Guard Clauses and State Capture ---
        var keyToDelete = _photoSessionState.CurrentPhotoKey;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        // Safety check: the current photo must have a position in the list.
        if (currentPosition < 0)
        {
            Logger.Error($"Inconsistent state: Could not find key {keyToDelete} in the photo list during deletion.");
            return new DeleteResult(false, false, "App - Inconsistent State");
        }

        if (_photoList.GetPhoto(keyToDelete) is not { } photo)
        {
            Logger.Error($"DeleteFileFromDiskAsync failed: Key {keyToDelete} not found in the collection.");
            return new DeleteResult(false, false, "App - Inconsistent State");
        }

        var deletedPath = photo.FilePath;
        var delResult = await StorageOps.DeleteFileFromDisk(deletedPath);
        if (!delResult.DeleteSuccess) return delResult;

        // The last photo is gone: the window closes the app.
        // TODO - Cleanup properly. If we call dispose now, the zoom out animation during
        // app close will crash as the draw call will try to display disposed canvas bitmaps.
        if (_photoList.Keys.Count <= 1) return new DeleteResult(true, true);

        // Same path as a Folder mode removal: the next photo is shown, else the previous one.
        await ApplyListChangeAsync(path => path.Equals(deletedPath, StringComparison.OrdinalIgnoreCase),
            renamed: null, added: [], order: null, goToFirst: false);
        return new DeleteResult(true, false);
    }

    // --- List changes (delete, Folder mode) ---

    // Serializes everything that mutates the list on the UI thread: delete, watcher batches, folder switches.
    private readonly SemaphoreSlim _listChangeGate = new(1, 1);

    /// <summary>
    /// UI thread, inside <see cref="_listChangeGate"/>. Applies one change to the list (see
    /// <see cref="PhotoList.ApplyChanges"/>; <paramref name="added"/> sorted by <paramref name="order"/>) and
    /// keeps the viewer on the same file: its position is recomputed, and if it was removed the nearest
    /// surviving neighbour in the old order is shown — the next one, else the previous one.
    /// </summary>
    private async Task ApplyListChangeAsync(Predicate<string> isRemoved, IReadOnlyDictionary<string, string>? renamed,
        IReadOnlyList<string> added, IComparer<string>? order, bool goToFirst)
    {
        var oldKeys = _photoList.Keys;
        int oldPosition = _photoSessionState.CurrentPhotoListPosition;
        int currentKey = _photoSessionState.CurrentPhotoKey;
        var currentPath = _photoList.GetPhoto(currentKey)?.FilePath;

        // The photos' exact current paths, not the batch's: the batch matches paths case-insensitively.
        List<(string OldPath, string NewPath)>? renamedPaths = null;
        if (renamed is { Count: > 0 })
            foreach (var key in oldKeys)
                if (_photoList.GetPhoto(key)?.FilePath is { } path && !isRemoved(path) &&
                    renamed.TryGetValue(path, out var newPath))
                    (renamedPaths ??= []).Add((path, newPath));

        var removed = _photoList.ApplyChanges(isRemoved, renamed, added, order, currentKey);
        var keys = _photoList.Keys;
        if (ReferenceEquals(keys, oldKeys)) return; // nothing actually changed

        // Renamed photos keep their key; the canvas knows them by path, so it has to follow.
        if (renamedPaths != null)
            foreach (var (oldPath, newPath) in renamedPaths)
                _canvasController.OnPhotoRenamed(oldPath, newPath);

        // Detach removed photos from the cache and the dictionary BEFORE navigating: the window move below
        // disposes whatever loaded photo falls outside the new window, and the old on-screen photo must not
        // be among them — the canvas draws it until the queued renderer swap runs.
        foreach (var (key, _) in removed)
            _cache.Evict(key);
        _photoList.Forget(removed);

        // Removed photos are already forgotten, so "still in the list" is exactly "survived".
        int newPosition = goToFirst ? 0 : ListChange.PositionAfter(oldKeys, oldPosition, keys, currentKey,
            _photoList.Contains,
            isReplacement: key => currentPath != null &&
                                  string.Equals(_photoList.GetPhoto(key)?.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
        if (keys[newPosition] == currentKey)
        {
            _photoSessionState.SetCurrentPhotoKeyAndListPosition(currentKey, newPosition);
            _cache.MoveWindow(newPosition, keys, signalHqLoading: true);
            _thumbNailController.RequestThumbnailRedraw();
            UpdateFileNameAndDetails();
        }
        else
        {
            await FlyTo(keys[newPosition], newPosition, triggerHqCaching: true);
        }

        // SetSource only queued the renderer swap; disposing now could free a bitmap the render thread is
        // still drawing (a native crash). The pump is FIFO, so this runs after the swap; Retire then waits for
        // any background load still using the photo.
        if (removed.Count > 0)
            _canvasController.RunAfterQueuedRenderWork(() =>
            {
                foreach (var (_, photo) in removed) photo.Retire();
            });
    }

    // --- Rename ---

    public bool CanRenameCurrentPhoto()
    {
        if (_photoList.Keys.Count == 0) return false;
        if (IsContinuousKeyPress()) return false;
        if (_photoSessionState.CurrentDisplayLevel != DisplayLevel.Hq) return false;
        return true;
    }

    public async Task<RenameResult> RenameCurrentPhoto(string newFileName)
    {
        var currentKey = _photoSessionState.CurrentPhotoKey;
        if (_photoList.GetPhoto(currentKey) is not { } photo)
        {
            Logger.Error($"RenameCurrentPhoto failed: Key {currentKey} not found in the collection.");
            return new RenameResult(false, "App - Inconsistent State");
        }

        var oldPath = photo.FilePath;
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", newFileName);
        var result = await StorageOps.RenameFileOnDisk(oldPath, newPath);
        if (!result.Success) return result;

        if (_folderSort == null)
        {
            // Explorer mode: the list keeps Explorer's order, so the photo stays where it is.
            photo.Rename(newPath);
            _canvasController.OnPhotoRenamed(oldPath, newPath);
            UpdateFileNameAndDetails();
            return result;
        }

        // Folder mode: the photo moves to its new sorted position, through the same path as an external
        // rename (the watcher's own report of this rename then finds nothing left to do).
        await _listChangeGate.WaitAsync();
        try
        {
            _folderSort.Rename(oldPath, newPath);
            await ApplyListChangeAsync(static _ => false,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldPath] = newPath },
                added: [], _folderSort, goToFirst: false);
        }
        finally
        {
            _listChangeGate.Release();
        }
        return result;
    }

    // --- Clipboard ---

    public Task CopyFileToClipboardAsync()
    {
        if (_photoList.GetPhoto(_photoSessionState.CurrentPhotoKey) is not { } photo)
            return Task.CompletedTask;
        return PhotoClipboard.CopyToClipboardAsync(photo, _photoSessionState.CurrentDisplayLevel);
    }

    // --- Queries ---

    private bool IsContinuousKeyPress() => _keyPressCounter > ContinuousPressThreshold;

    public bool IsSinglePhoto() => _photoList.Count <= 1;

    // Before the initial file listing completes, CurrentPhotoKey is still its default (0), which
    // does not necessarily correspond to the first photo's real position in the sorted list (e.g.
    // it isn't 0 whenever the opened file isn't alphabetically first). Mirror the same fallback
    // UpdateFileNameAndDetails uses so callers see the actual on-screen file during that window.
    public string GetFullPathCurrentFile() =>
        _initialFileListingCompleted ? _photoList[_photoSessionState.CurrentPhotoKey].FilePath : _firstPhoto.FilePath;

    /// <summary>Whether the on-screen photo is a RAW file. Lock-free read; safe to call cross-thread.</summary>
    public bool CurrentPhotoIsRaw =>
        _photoList.GetPhoto(_photoSessionState.CurrentPhotoKey) is { IsRaw: true };

    // --- Cleanup ---

    public void Dispose()
    {
        _cts.Cancel();
        _firstPhotoLoadedTcs.TrySetCanceled();
        DisposeFolderMode();
        _cache.Dispose();
        _cts.Dispose();
        Logger.Info("PhotoDisplayController disposed.");
    }
}
