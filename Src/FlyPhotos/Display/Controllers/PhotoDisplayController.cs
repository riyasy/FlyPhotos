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

internal partial class PhotoDisplayController : IPhotoProvider
{
    public event Action<string>? CacheStatusChanged;
    public event Action<FileDisplayDetails>? FileNameOrDetailsChanged;
    public event Action? FirstPhotoLoaded;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _firstPhotoLoadedTcs = new();

    // UI thread: presses since the last Brake, and whether that made a burst that holds HQ loading.
    private int _keyPressCounter;
    private bool _holdingHq;
    private const int ContinuousPressThreshold = 1;

    // The photo collection: Photo objects + the sorted-key snapshot. Its concurrency model (a
    // ConcurrentDictionary for lock-free reads + a volatile copy-on-write key snapshot) lives inside it.
    private readonly PhotoList _photoList = new();

    // Owns all background loading (preview/HQ/disk tiers, threads, throttling, the sliding window,
    // disk write-back). The controller keeps the photo list and navigation; it drives the cache with
    // MoveWindow and listens to its ready events.
    private readonly PrefetchCache _cache;

    // False while the list holds only the opened photo (the listing is still running): the filename bar shows no
    // position yet, and delete/rename wait.
    private volatile bool _initialFileListingCompleted = false;

    private readonly CanvasAnimatedControl _d2dCanvas;
    private readonly ICanvasController _canvasController;
    private readonly IThumbnailController _thumbNailController;
    private readonly string _firstPhotoPath;
    private readonly bool _launchedExternally;

    // Where the viewer is (key snapshot + position), one reference so the thumbnail strip never sees a position
    // from another list. Written on the startup thread until the listing is published, then on the UI thread.
    private volatile NavState _nav = NavState.None;

    // The level the canvas was last asked to show. Same threads as _nav.
    private DisplayLevel _displayLevel;

    // --- Initialization ---

    public PhotoDisplayController(CanvasAnimatedControl d2dCanvas, ICanvasController canvasController, IThumbnailController thumbNailController,
        string firstPhotoPath, bool launchedExternally)
    {
        _d2dCanvas = d2dCanvas;
        _canvasController = canvasController;
        _thumbNailController = thumbNailController;
        _firstPhotoPath = firstPhotoPath;
        _launchedExternally = launchedExternally;

        _cache = new PrefetchCache(_photoList.GetPhoto, _d2dCanvas);
        _cache.Loaded += OnLoaded;
        _cache.CacheStatusChanged += status => CacheStatusChanged?.Invoke(status);

        _thumbNailController.SetPhotoProvider(this);

        // The opened photo alone, until the listing replaces it with the whole folder/Explorer view around it.
        _photoList.Initialize([firstPhotoPath]);
        _nav = new NavState(_photoList.Keys, 0);

        var thread = new Thread(() => { DoStartupActivities(_firstPhotoPath, _cts.Token); });
        thread.SetApartmentState(ApartmentState.STA); // This is needed for COM interaction.
        thread.Start();
    }

    public async Task LoadFirstPhoto()
    {
        ImageReader.Initialize(_d2dCanvas);

        var firstPhoto = _photoList[_nav.Key];
        bool continueLoadingHq = await PhotoLoader.LoadFirstPreview(firstPhoto, _d2dCanvas);

        // The open-zoom animation begins on the FIRST SetSource below. Arm the completion wait now
        // so the W2D subscribe is enqueued (FIFO) before that SetSource installs/starts the animation.
        Task openZoomAnimation = AppConfig.Settings.OpenExitZoom
            ? _canvasController.WaitForPanZoomAnimationAsync(Constants.PanZoomAnimationDurationNormal * 2)
            : Task.CompletedTask;

        if (continueLoadingHq)
        {
            await SetSourceAsync(firstPhoto, DisplayLevel.Preview);
            await Task.Run(() => PhotoLoader.LoadHq(firstPhoto, _d2dCanvas));
        }

        await SetSourceAsync(firstPhoto, DisplayLevel.Hq);

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
                FileDiscovery.DiscoverFiles(selectedFilePath, _launchedExternally);

            // The opened photo gets the whole machine until it is on screen.
            _firstPhotoLoadedTcs.Task.Wait(token);

            // The opened photo keeps its key (and what it has loaded) at its place in the listing.
            var firstKey = _nav.Key;
            var position = FileDiscovery.FindSelectedFileIndex(selectedFilePath, files);
            _photoList.ReplaceWithListing(files, position, firstKey);
            _nav = new NavState(_photoList.Keys, position);

            _initialFileListingCompleted = true;
            UpdateFileNameAndDetails();

            FirstPhotoLoaded?.Invoke();

            // Secondary instances skip all background caching threads.
            if (AppConfig.Volatile.IsSecondaryInstance) return;

            _cache.Start(_nav);
            // Last: from here on, Folder mode list changes may run on the UI thread, so this thread must be
            // done with the list.
            _listingReady.TrySetResult();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // --- Cache events & display upgrade ---

    // 1 while a RefreshCurrent is queued on the UI thread, so a burst of loads queues one refresh.
    private int _refreshQueued;

    // UI thread: the item last handed to the canvas, to tell a reload (same level, new item) from a repeat.
    private DisplayItem? _shownItem;

    // Loader thread: an image was stored for a key. The strip redraws that slot; whether the screen changes is
    // decided on the UI thread, which alone knows what is on screen. Never compared with the loader's own centre:
    // the loader may not have seen the latest navigation yet.
    private void OnLoaded(int key)
    {
        _thumbNailController.RedrawThumbNailsIfNeeded(key);
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
            {
                Volatile.Write(ref _refreshQueued, 0);
                RefreshCurrent();
            });
    }

    /// <summary>
    /// UI thread. Shows the best image the photo on screen now has, if it is a higher level than the one shown, or
    /// the same level reloaded (a RAW setting change). Never steps down.
    /// </summary>
    private void RefreshCurrent()
    {
        if (_photoList.GetPhoto(_nav.Key) is not { } photo) return;
        var level = BestLevel(photo);
        if (level == DisplayLevel.PlaceHolder || level < _displayLevel) return;
        if (level == _displayLevel && ReferenceEquals(photo.Images.Get(level), _shownItem)) return;
        _ = SetSourceAsync(photo, level);
    }

    private static DisplayLevel BestLevel(Photo photo) =>
        photo.Images.Hq != null ? DisplayLevel.Hq :
        photo.Images.Preview != null ? DisplayLevel.Preview : DisplayLevel.PlaceHolder;

    // UI thread. Shows the photo on screen at the best level it has, and re-centres the cache on it — posted first,
    // so prefetching never waits for a slow install (an animation's first frames).
    private async Task DisplayCurrent()
    {
        if (_photoList.GetPhoto(_nav.Key) is not { } photo) return;
        _cache.MoveWindow(_nav);
        await SetSourceAsync(photo, BestLevel(photo));
    }

    private async Task SetSourceAsync(Photo photo, DisplayLevel displayLevel)
    {
        var item = photo.Images.Get(displayLevel);
        _displayLevel = displayLevel;
        _shownItem = item;
        await _canvasController.SetSource(photo, item, displayLevel);
        UpdateFileNameAndDetails();
    }

    // --- Navigation ---

    public async Task Fly(NavDirection direction)
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        // A list that can't be navigated must never open a burst: HQ stays held until a Brake().
        _keyPressCounter++;
        if (IsContinuousKeyPress() && !_holdingHq)
        {
            _holdingHq = true;
            _cache.HoldHq(true);
        }
        int currentPosition = _nav.Position;
        if (currentPosition < 0) return; // TODO Should not happen if state is consistent
        int newPosition = currentPosition + (direction == NavDirection.Next ? 1 : -1);
        if (newPosition < 0 || newPosition >= keys.Count)
        {
            _thumbNailController.BounceAtEdge(direction);
            return;
        }
        int newKey = keys[newPosition];
        await FlyTo(newKey, newPosition);
    }

    public async Task FlyToFirst()
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        if (_nav.Position == 0) _thumbNailController.BounceAtEdge(NavDirection.Prev);
        await FlyTo(keys[0], 0);
    }

    public async Task FlyToLast()
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        if (_nav.Position == keys.Count - 1) _thumbNailController.BounceAtEdge(NavDirection.Next);
        await FlyTo(keys[^1], keys.Count - 1);
    }

    public async Task FlyBy(int shiftBy)
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        int currentPosition = _nav.Position;
        if (currentPosition < 0) return;
        int newPosition = Math.Clamp(currentPosition + shiftBy, 0, keys.Count - 1);
        await FlyTo(keys[newPosition], newPosition);
    }

    private async Task FlyTo(int toKey, int toPosition)
    {
        if (!_photoList.Contains(toKey)) return;
        _nav = new NavState(_photoList.Keys, toPosition);
        await DisplayCurrent();
    }

    public async Task Brake()
    {
        _keyPressCounter = 0;
        if (_holdingHq)
        {
            _holdingHq = false;
            _cache.HoldHq(false);
        }
        if (_displayLevel != DisplayLevel.Hq && _photoList.GetPhoto(_nav.Key) is { Images.Hq: not null } photo)
            await SetSourceAsync(photo, DisplayLevel.Hq);
    }

    // --- File info ---

    public void UpdateFileNameAndDetails()
    {
        try
        {
            var initialFileListingCompleted = _initialFileListingCompleted;
            var currentKey = _nav.Key;
            var photo = _photoList.GetPhoto(currentKey);

            if (photo == null) return;

            string? positionText = null;
            var fileName = Path.GetFileName(photo.FilePath);
            string? dimensionText = null;

            if (initialFileListingCompleted)
            {
                var currentPosition = _nav.Position;
                if (currentPosition < 0) return;
                var totalFileCount = _photoList.Keys.Count;
                positionText = $"[{currentPosition + 1}/{totalFileCount}]";
            }

            var showDimension = AppConfig.Settings.ShowImageDimensions &&
                                _displayLevel != DisplayLevel.PlaceHolder &&
                                !photo.IsVector &&
                                !photo.Images.IsErrorScreen(_displayLevel);

            if (showDimension)
            {
                // A multi-page photo reports the page on screen, not the whole file - ICO frames each
                // have their own size, so the file's "actual size" would be wrong on every page but the first.
                if (_canvasController.CurrentPage is { } page && page.Path == photo.FilePath)
                {
                    dimensionText = $"({page.Width} x {page.Height} - {page.Index + 1}/{page.Count})";
                }
                else
                {
                    var (w, h) = photo.Images.GetActualSize();
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
    // Called from the UI thread when a RAW decode setting (DecodeRawData / decoder priority) changes. A RAW HQ on
    // screen stays (the canvas holds its own lease) until the re-decoded one replaces it (RefreshCurrent).
    public void InvalidateRawHqCache() => _cache.InvalidateHqForRawFiles();

    // --- Deletion ---

    public bool CanDeleteCurrentPhoto()
    {
        if (!_initialFileListingCompleted) return false; // the list is still just the opened photo
        if (IsContinuousKeyPress()) return false;
        if (_displayLevel != DisplayLevel.Hq) return false;
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
        var keyToDelete = _nav.Key;
        int currentPosition = _nav.Position;
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
        await ApplyListChangeAsync(new ListChangeBatch(
            path => path.Equals(deletedPath, StringComparison.OrdinalIgnoreCase), null, [], null), goToFirst: false);
        return new DeleteResult(true, false);
    }

    // --- List changes (delete, Folder mode) ---

    // Serializes everything that mutates the list on the UI thread: delete, watcher batches, folder switches.
    private readonly SemaphoreSlim _listChangeGate = new(1, 1);

    /// <summary>
    /// UI thread, inside <see cref="_listChangeGate"/>. Applies one change to the list (see
    /// <see cref="PhotoList.ApplyChanges"/>) and
    /// keeps the viewer on the same file: its position is recomputed, and if it was removed the nearest
    /// surviving neighbour in the old order is shown — the next one, else the previous one.
    /// </summary>
    private async Task ApplyListChangeAsync(ListChangeBatch batch, bool goToFirst)
    {
        var oldKeys = _photoList.Keys;
        int oldPosition = _nav.Position;
        int currentKey = _nav.Key;
        var currentPath = _photoList.GetPhoto(currentKey)?.FilePath;

        // Renamed photos keep their key; the canvas knows them by path, so it has to follow.
        var removed = _photoList.ApplyChanges(batch.IsRemoved, batch.Renamed, batch.Added, batch.Order, currentKey,
            onRenamed: _canvasController.OnPhotoRenamed);
        var keys = _photoList.Keys;
        if (ReferenceEquals(keys, oldKeys)) return; // nothing actually changed

        // Detach removed photos from the cache and the dictionary BEFORE navigating: the window move below
        // releases whatever loaded photo falls outside the new window, and removed photos are released by
        // Retire instead (once, not twice).
        _cache.Forget(removed.ConvertAll(r => r.Key));
        _photoList.Forget(removed);

        // Removed photos are already forgotten, so "still in the list" is exactly "survived".
        int newPosition = goToFirst ? 0 : ListChange.PositionAfter(oldKeys, oldPosition, keys, currentKey,
            _photoList.Contains,
            isReplacement: key => currentPath != null &&
                                  string.Equals(_photoList.GetPhoto(key)?.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
        if (keys[newPosition] == currentKey)
        {
            _nav = new NavState(keys, newPosition);
            _cache.MoveWindow(_nav);
            _thumbNailController.RequestThumbnailRedraw();
            UpdateFileNameAndDetails();
        }
        else
        {
            await FlyTo(keys[newPosition], newPosition);
        }

        // The canvas holds its own lease on whatever it still draws, and a load still running for a removed photo
        // releases what it brings, so retiring at once is safe.
        foreach (var (_, photo) in removed) photo.Images.Retire();
    }

    // --- Rename ---

    public bool CanRenameCurrentPhoto()
    {
        if (!_initialFileListingCompleted) return false; // the list is still just the opened photo
        if (IsContinuousKeyPress()) return false;
        if (_displayLevel != DisplayLevel.Hq) return false;
        return true;
    }

    public async Task<RenameResult> RenameCurrentPhoto(string newFileName)
    {
        var currentKey = _nav.Key;
        if (_photoList.GetPhoto(currentKey) is not { } photo)
        {
            Logger.Error($"RenameCurrentPhoto failed: Key {currentKey} not found in the collection.");
            return new RenameResult(false, "App - Inconsistent State");
        }

        var oldPath = photo.FilePath;
        var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", newFileName);
        var result = await StorageOps.RenameFileOnDisk(oldPath, newPath);
        if (!result.Success) return result;

        if (_folder == null)
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
            await ApplyListChangeAsync(_folder.Rename(oldPath, newPath), goToFirst: false);
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
        if (_photoList.GetPhoto(_nav.Key) is not { } photo)
            return Task.CompletedTask;
        return PhotoClipboard.CopyToClipboardAsync(photo, _displayLevel);
    }

    // --- Queries ---

    private bool IsContinuousKeyPress() => _keyPressCounter > ContinuousPressThreshold;

    public bool IsSinglePhoto() => _photoList.Count <= 1;

    public string GetFullPathCurrentFile() => _photoList[_nav.Key].FilePath;

    /// <summary>Whether the on-screen photo is a RAW file. Lock-free read; safe to call cross-thread.</summary>
    NavState IPhotoProvider.Nav => _nav;
    Photo? IPhotoProvider.GetPhoto(int key) => _photoList.GetPhoto(key);

    public bool CurrentPhotoIsRaw =>
        _photoList.GetPhoto(_nav.Key) is { IsRaw: true };

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
