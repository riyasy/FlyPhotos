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
using NLog;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// The viewer's orchestrator: owns the photo list, where the viewer is (<see cref="NavState"/>) and what is on
/// screen, and drives the <see cref="PrefetchCache"/> that loads around it.
/// <para>
/// <b>Flow.</b> A key press moves <c>_nav</c> and shows the best image the photo already has (HQ, else preview, else
/// the placeholder) at once, then re-centres the cache. When the cache stores a better image for the photo on screen
/// (<see cref="PrefetchCache.Loaded"/>), <see cref="UpgradeCurrent"/> shows it. A key press never waits for a decode:
/// "every key press shows something at once".
/// </para>
/// <para>
/// <b>Threads.</b>
/// <list type="bullet">
///   <item><b>UI thread</b>: every public method runs here, and it alone changes the list, <c>_nav</c> and what is on
///     screen. List changes (delete, rename, Folder mode) take turns through <c>_listChangeGate</c>.</item>
///   <item><b>Startup thread</b>: lists the folder or Explorer view once (<see cref="DoStartupActivities"/>), then
///     hands the listing to the UI thread.</item>
///   <item><b>Prefetch thread</b>: the cache's own; it loads and unloads images and reports through
///     <see cref="OnLoaded"/>.</item>
/// </list>
/// Neither background thread touches the screen: they post to the UI thread (<see cref="PostToUi"/>). The thumbnail
/// strip and the prefetch cache read the list and <c>_nav</c> lock-free from their own threads; both are immutable
/// snapshots published by reference, so a reader always sees a consistent pair.
/// </para>
/// <para>
/// <b>Leases.</b> Images are leased (<see cref="DisplayItem"/>): the item handed to the canvas carries its own lease,
/// so the cache can unload a photo at any time without freeing what is on screen.
/// </para>
/// <para>Folder mode (a live folder, sibling-folder switching) is in PhotoDisplayController.Folder.cs.</para>
/// </summary>
internal partial class PhotoDisplayController
{
    /// <summary>The "left/total &lt; Cache &gt; right/total" status. Fired on the prefetch thread.</summary>
    public event Action<string>? CacheStatusChanged;
    /// <summary>The filename bar's text (position, name, size) changed. UI thread.</summary>
    public event Action<FileDisplayDetails>? FileNameOrDetailsChanged;
    /// <summary>The first photo is on screen and the full listing is published. UI thread.</summary>
    public event Action? FirstPhotoLoaded;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Cancelled by Dispose: the window is closing. Checked before anything that would touch the canvas, and stops the
    // startup thread's wait for the first photo.
    private readonly CancellationTokenSource _cts = new();
    // Completed once the first photo is on screen (or failed). The startup thread waits for it before building the
    // full listing, so the opened photo gets the whole machine first.
    private readonly TaskCompletionSource<bool> _firstPhotoLoadedTcs = new();

    // UI thread: navigation presses since the last Brake. Above BurstThreshold the user is holding a key down (a
    // burst): HQ loading is held so the cores go to previews, and delete/rename are refused.
    private int _keyPressCounter;
    // Presses allowed before it counts as a burst: 1, so a single press loads HQ and the second press in a row holds it.
    private const int BurstThreshold = 1;

    // The photo collection: Photo objects + the ordered key snapshot (lock-free reads on any thread). Changed only on
    // the UI thread once the listing is published.
    private readonly PhotoList _photoList = new();

    // Owns all background loading (previews, HQ, disk-cache writes) on its own thread. The controller drives it with
    // MoveWindow / HoldHq / Forget and listens to Loaded.
    private readonly PrefetchCache _cache;

    // False while the list holds only the opened photo (the listing is still running): the filename bar shows no
    // position yet, and delete/rename wait. Set once, on the UI thread, by PublishListing.
    private volatile bool _initialFileListingCompleted = false;

    private readonly ICanvasController _canvasController;     // the main canvas: shows one item at a time
    private readonly IThumbnailController _thumbNailController; // the strip: reads the list and _nav itself
    private readonly IPhotoDecoder _decoder;                  // decodes, disk cache, shared indicator items
    private readonly SynchronizationContext _ui;              // the UI thread, for PostToUi
    private readonly string _firstPhotoPath;                  // the file Fly was opened with
    private readonly bool _launchedExternally;                // opened from Explorer (picks the Explorer listing)

    // Where the viewer is (key snapshot + position), one reference so the thumbnail strip never sees a position
    // from another list. Written on the UI thread only; read by the strip on its render thread.
    private volatile NavState _nav = NavState.None;

    // UI thread: the level the canvas was last asked to show. UpgradeCurrent never goes below it; delete and rename
    // need it to be Hq (the user sees what they act on).
    private DisplayLevel _displayLevel;

    // --- Initialization ---

    /// <summary>
    /// Starts with the opened photo alone in the list and starts the startup thread that lists the rest; the window
    /// then calls <see cref="LoadFirstPhoto"/>.
    /// </summary>
    /// <param name="canvasController">The main canvas.</param>
    /// <param name="thumbNailController">The thumbnail strip.</param>
    /// <param name="decoder">Where images come from (<see cref="PhotoDecoder"/> in the app).</param>
    /// <param name="ui">
    /// The UI thread, which owns navigation and the list. The controller's methods run on it, and work from other
    /// threads is posted to it.
    /// </param>
    /// <param name="firstPhotoPath">The file Fly was opened with.</param>
    /// <param name="launchedExternally">Opened from Explorer: the listing follows Explorer's view and order.</param>
    /// <param name="prefetch">How the prefetch cache runs; the default runs it on its own thread.</param>
    public PhotoDisplayController(ICanvasController canvasController, IThumbnailController thumbNailController,
        IPhotoDecoder decoder, SynchronizationContext ui, string firstPhotoPath, bool launchedExternally,
        PrefetchOptions? prefetch = null)
    {
        _canvasController = canvasController;
        _thumbNailController = thumbNailController;
        _decoder = decoder;
        _ui = ui;
        _firstPhotoPath = firstPhotoPath;
        _launchedExternally = launchedExternally;

        _cache = new PrefetchCache(_photoList.GetPhoto, decoder, prefetch);
        _cache.Loaded += OnLoaded;
        _cache.CacheStatusChanged += status => CacheStatusChanged?.Invoke(status);

        _thumbNailController.SetPhotoSource(_photoList.GetPhoto, () => _nav);

        // The opened photo alone, until the listing replaces it with the whole folder/Explorer view around it.
        _photoList.Initialize([firstPhotoPath]);
        _nav = new NavState(_photoList.Keys, 0);

        var thread = new Thread(() => { DoStartupActivities(_firstPhotoPath, _cts.Token); });
        thread.SetApartmentState(ApartmentState.STA); // This is needed for COM interaction.
        thread.Start();
    }

    // Any thread. Runs action on the UI thread, later (never inline, even when called on the UI thread).
    private void PostToUi(Action action) => _ui.Post(static a => ((Action)a!)(), action);

    /// <summary>
    /// UI thread, once. Shows the opened photo (preview, then HQ) before anything else loads; on failure shows the
    /// error screen. Either way the startup thread is then let go to publish the full listing.
    /// </summary>
    public async Task LoadFirstPhoto()
    {
        var firstPhoto = _photoList[_nav.Key];
        try
        {
            await LoadAndShowFirstPhoto(firstPhoto);
        }
        catch (Exception ex)
        {
            // Show the error screen rather than nothing, and let the listing go on so the user can move away.
            Logger.Error(ex, $"Loading the first photo failed: {firstPhoto.FilePath}");
            if (firstPhoto.Slots.Hq == null) firstPhoto.Slots.TryAttachHq(_decoder.HqFailed());
            await SetSourceAsync(firstPhoto, DisplayLevel.Hq);
        }
        finally
        {
            _firstPhotoLoadedTcs.TrySetResult(true);
        }
    }

    // UI thread. The first photo's own fast path: a cheap preview on screen first (unless the HQ came straight away),
    // then the HQ, which may be a tiled large image. The prefetch cache is not running yet, so the controller loads
    // and stores these itself. Waits for the open-zoom animation so the listing doesn't disturb it.
    private async Task LoadAndShowFirstPhoto(Photo firstPhoto)
    {
        bool continueLoadingHq = await _decoder.LoadFirstPreview(firstPhoto);
        if (_cts.IsCancellationRequested) return; // closed meanwhile

        // The open-zoom animation begins on the FIRST SetSource below. Arm the completion wait now
        // so the W2D subscribe is enqueued (FIFO) before that SetSource installs/starts the animation.
        Task openZoomAnimation = AppConfig.Settings.OpenExitZoom
            ? _canvasController.WaitForPanZoomAnimationAsync(Constants.PanZoomAnimationDurationNormal * 2)
            : Task.CompletedTask;

        if (continueLoadingHq)
        {
            await SetSourceAsync(firstPhoto, DisplayLevel.Preview);
            if (await _decoder.LoadHq(firstPhoto, allowLarge: true, _cts.Token) is { } hq)
                firstPhoto.Slots.TryAttachHq(hq);
        }

        await SetSourceAsync(firstPhoto, DisplayLevel.Hq);

        // Wait for the actual animation to finish rather than a fixed delay; already-complete is a no-op.
        await openZoomAnimation;
    }

    // Startup thread. Warms the disk cache, lists the files (single file / Folder mode / Explorer view), waits for the
    // first photo to be on screen, builds the photos, and hands the listing to the UI thread (PublishListing).
    private void DoStartupActivities(string selectedFilePath, CancellationToken token)
    {
        // Open the disk cache before the (possibly slow) listing, so the first photo's cache lookup doesn't pay
        // the ~50 ms SQLite start-up. Its own try: a failure here mustn't stop the rest of the start-up.
        try { _decoder.WarmUp(); }
        catch (Exception ex) { Logger.Error(ex); }

        // Every way through ends in PublishListing, which starts navigation and loading: a listing that fails
        // leaves the opened photo alone in the list, never a viewer stuck before it.
        IReadOnlyList<string> files;
        try
        {
            // Secondary instances only display the single opened image.
            // No folder or Explorer-window discovery is performed.
            if (AppConfig.Volatile.IsSecondaryInstance)
                files = [selectedFilePath];
            else if (IsFolderMode)
            {
                _folder = new FolderSource();
                _folder.Changed += Watcher_Changed;
                files = _folder.ListAndWatch(selectedFilePath);
            }
            else
                files = FileDiscovery.DiscoverFiles(selectedFilePath, _launchedExternally);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Listing failed; showing the opened photo alone");
            files = [selectedFilePath];
        }

        // The opened photo gets the whole machine until it is on screen.
        try { _firstPhotoLoadedTcs.Task.Wait(token); }
        catch (Exception) { return; } // the window is closing

        // The opened photo keeps its key (and what it has loaded) at its place in the listing. The photos are
        // made here, off the UI thread; the UI thread publishes them, since it alone moves the viewer.
        IReadOnlyList<int> keys;
        int position;
        try
        {
            position = FileDiscovery.FindSelectedFileIndex(selectedFilePath, files);
            keys = _photoList.PrepareListing(files, position, _nav.Key);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Building the listing failed; showing the opened photo alone");
            keys = _photoList.Keys; // still the startup list: the opened photo alone
            position = 0;
        }
        PostToUi(() => PublishListing(keys, position));
    }

    // UI thread. Swaps the startup list for the full listing; navigation, delete and rename start working here, and so
    // does background loading.
    private void PublishListing(IReadOnlyList<int> keys, int position)
    {
        if (_cts.IsCancellationRequested) return;
        _photoList.Publish(keys);
        _nav = new NavState(keys, position);
        _initialFileListingCompleted = true;
        UpdateFileNameAndDetails();
        FirstPhotoLoaded?.Invoke();

        // Secondary instances skip all background caching.
        if (AppConfig.Volatile.IsSecondaryInstance) return;
        _cache.Start(_nav);
        _listingReady.TrySetResult(); // Folder mode's watcher batches may now apply
    }

    // --- Cache events & display upgrade ---

    // 1 while an UpgradeCurrent is queued on the UI thread, so a run of loads queues one upgrade, not one per load.
    // Set on the prefetch thread (OnLoaded), cleared on the UI thread just before the upgrade runs.
    private int _upgradeQueued;

    // UI thread: the item last handed to the canvas, to tell a reload (same level, new item) from a repeat. Only
    // compared by reference, never used, so it holds no lease.
    private DisplayItem? _shownItem;

    // Prefetch thread: an image was stored for a key. The strip redraws that slot; whether the screen changes is
    // decided on the UI thread, which alone knows what is on screen. Never compared with the cache's own centre:
    // the cache may not have seen the latest navigation yet.
    private void OnLoaded(int key)
    {
        _thumbNailController.RedrawThumbNailsIfNeeded(key);
        if (Interlocked.Exchange(ref _upgradeQueued, 1) == 0)
            PostToUi(() =>
            {
                Volatile.Write(ref _upgradeQueued, 0);
                UpgradeCurrent();
            });
    }

    /// <summary>
    /// UI thread. Shows the best image the photo on screen now has, if it is a higher level than the one shown, or
    /// the same level reloaded (a RAW setting change). Never steps down, and never re-shows the placeholder.
    /// </summary>
    private void UpgradeCurrent()
    {
        if (_photoList.GetPhoto(_nav.Key) is not { } photo) return;
        var level = BestLevel(photo);
        if (level < _displayLevel || level == DisplayLevel.PlaceHolder) return;
        if (level == _displayLevel && ReferenceEquals(photo.Slots.Get(level), _shownItem)) return;
        _ = SetSourceAsync(photo, level);
    }

    // The highest level the photo has stored right now. Only a hint: the cache may unload it a moment later, which
    // SetSourceAsync handles by stepping down.
    private static DisplayLevel BestLevel(Photo photo) =>
        photo.Slots.Hq != null ? DisplayLevel.Hq :
        photo.Slots.Preview != null ? DisplayLevel.Preview : DisplayLevel.PlaceHolder;

    // UI thread. Shows the photo on screen at the best level it has, and re-centres the cache on it — posted first,
    // so prefetching never waits for a slow install (an animation's first frames).
    private async Task DisplayCurrent()
    {
        if (_photoList.GetPhoto(_nav.Key) is not { } photo) return;
        _cache.MoveWindow(_nav);
        await SetSourceAsync(photo, BestLevel(photo));
    }

    // UI thread. Shows the photo at displayLevel, or the next level down when that item was released meanwhile (the
    // cache may unload it at any moment): the placeholder is always there, so something is always shown. The lease
    // taken here passes to the canvas, which holds it while the item is on screen.
    private async Task SetSourceAsync(Photo photo, DisplayLevel displayLevel)
    {
        if (_cts.IsCancellationRequested) return; // shutting down: the canvas may be gone
        var item = LeaseAtOrBelow(photo, ref displayLevel);
        _displayLevel = displayLevel;
        _shownItem = item;
        try
        {
            await _canvasController.SetSource(photo, item, displayLevel);
        }
        catch (Exception ex) when (displayLevel > DisplayLevel.PlaceHolder)
        {
            // The canvas could not draw the item (it released the lease). Show the placeholder rather than leave the
            // previous photo up. _shownItem keeps the failed item, so UpgradeCurrent doesn't retry it on every load;
            // a newer item, or coming back to the photo, tries again.
            Logger.Error(ex, $"Showing {photo.FilePath} at {displayLevel} failed");
            await _canvasController.SetSource(photo, _decoder.LoadingIndicator, DisplayLevel.PlaceHolder);
        }
        UpdateFileNameAndDetails();
    }

    // UI thread. Leases the photo's item at level, or the highest one below it that is still alive, and lowers level
    // to match. The placeholder needs no lease, so this always returns something to show.
    private DisplayItem LeaseAtOrBelow(Photo photo, ref DisplayLevel level)
    {
        for (; level > DisplayLevel.PlaceHolder; level--)
            if (photo.Slots.Get(level) is { } item && item.TryAcquire())
                return item;
        return _decoder.LoadingIndicator; // shared, never freed: needs no lease
    }

    // --- Navigation ---
    // Positions are read from _nav and applied to _nav's own key list: one snapshot, never a position from one list
    // used on another.

    /// <summary>
    /// One step to the next/previous photo (a key press, or a repeat while held). The second press before a
    /// <see cref="Brake"/> starts a burst, which holds HQ loading. Bounces the strip at either end.
    /// </summary>
    public async Task Fly(NavDirection direction)
    {
        var (keys, currentPosition) = _nav;
        if (keys.Count <= 1) return;
        // A list that can't be navigated must never open a burst: HQ stays held until a Brake().
        if (++_keyPressCounter == BurstThreshold + 1) _cache.HoldHq();
        if (currentPosition < 0) return;
        int newPosition = currentPosition + (direction == NavDirection.Next ? 1 : -1);
        if (newPosition < 0 || newPosition >= keys.Count)
        {
            _thumbNailController.BounceAtEdge(direction);
            return;
        }
        await FlyTo(keys, newPosition);
    }

    /// <summary>Jumps to the first photo (bounces if already there).</summary>
    public async Task FlyToFirst()
    {
        var (keys, currentPosition) = _nav;
        if (keys.Count <= 1) return;
        if (currentPosition == 0) _thumbNailController.BounceAtEdge(NavDirection.Prev);
        await FlyTo(keys, 0);
    }

    /// <summary>Jumps to the last photo (bounces if already there).</summary>
    public async Task FlyToLast()
    {
        var (keys, currentPosition) = _nav;
        if (keys.Count <= 1) return;
        if (currentPosition == keys.Count - 1) _thumbNailController.BounceAtEdge(NavDirection.Next);
        await FlyTo(keys, keys.Count - 1);
    }

    /// <summary>Moves <paramref name="shiftBy"/> photos (negative: back), clamped to the list (a thumbnail click).</summary>
    public async Task FlyBy(int shiftBy)
    {
        var (keys, currentPosition) = _nav;
        if (keys.Count <= 1 || currentPosition < 0) return;
        int newPosition = (int)Math.Clamp((long)currentPosition + shiftBy, 0, keys.Count - 1);
        await FlyTo(keys, newPosition);
    }

    // UI thread. Moves the viewer to toPosition in keys (the snapshot the caller read the position from) and shows it.
    private async Task FlyTo(IReadOnlyList<int> keys, int toPosition)
    {
        if (!_photoList.Contains(keys[toPosition])) return;
        _nav = new NavState(keys, toPosition);
        await DisplayCurrent();
    }

    /// <summary>
    /// The navigation key was let go (or the window lost focus): ends a burst, so HQ loading resumes, and shows the
    /// HQ at once if the photo on screen already has it.
    /// </summary>
    public async Task Brake()
    {
        if (IsBurst()) _cache.ResumeHq();
        _keyPressCounter = 0;
        if (_displayLevel != DisplayLevel.Hq && _photoList.GetPhoto(_nav.Key) is { Slots.Hq: not null } photo)
            await SetSourceAsync(photo, DisplayLevel.Hq);
    }

    // --- File info ---

    /// <summary>
    /// Raises <see cref="FileNameOrDetailsChanged"/> for the photo on screen: "[position/total]" once the listing is
    /// published, the file name, and its pixel size (the page's, for a multi-page photo) when that is shown.
    /// </summary>
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
                                !photo.Slots.IsErrorScreen(_displayLevel);

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
                    var (w, h) = photo.Slots.GetActualSize();
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
    // screen stays (the canvas holds its own lease) until the re-decoded one replaces it (UpgradeCurrent).
    public void InvalidateRawHqCache() => _cache.InvalidateHqForRawFiles();

    // UI thread: a cache window size changed; the cache re-plans now rather than at the next navigation.
    public void CacheSizeChanged() => _cache.Replan();

    // --- Deletion ---

    /// <summary>
    /// Whether delete may run now: the listing is published, no burst is on, and the HQ is on screen (the user sees
    /// what they delete). On false the caller tells the user to wait.
    /// </summary>
    public bool CanDeleteCurrentPhoto()
    {
        if (!_initialFileListingCompleted) return false; // the list is still just the opened photo
        if (IsBurst()) return false;
        if (_displayLevel != DisplayLevel.Hq) return false;
        return true;
    }

    /// <summary>
    /// Deletes the photo on screen (to the Recycle Bin when possible) and shows the next one, else the previous one.
    /// <see cref="DeleteResult.IsLastPhoto"/> tells the window to close when it was the only photo.
    /// </summary>
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

    // UI thread, inside _listChangeGate. Deletes the file, then removes it from the list as a Folder mode removal would.
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
        var delResult = await DeleteFile(deletedPath);
        if (!delResult.DeleteSuccess) return delResult;

        // The last photo is gone: the window closes the app.
        // TODO - Cleanup properly. If we call dispose now, the zoom out animation during
        // app close will crash as the draw call will try to display disposed canvas bitmaps.
        if (_photoList.Keys.Count <= 1) return new DeleteResult(true, true);

        // Same path as a Folder mode removal: the next photo is shown, else the previous one.
        await ApplyListChangeAsync(ListChangeBatch.Remove(deletedPath), goToFirst: false);
        return new DeleteResult(true, false);
    }

    /// <summary>Deletes a file from disk (to the Recycle Bin). Tests swap it, so they don't fill the Recycle Bin.</summary>
    internal Func<string, Task<DeleteResult>> DeleteFile { get; set; } = StorageOps.DeleteFileFromDisk;

    // --- List changes (delete, Folder mode) ---

    // Serializes everything that mutates the list on the UI thread: delete, rename, watcher batches, folder switches.
    // They all await (a disk operation, a folder listing), so without it a second change could start inside the first.
    private readonly SemaphoreSlim _listChangeGate = new(1, 1);

    /// <summary>
    /// UI thread, inside <see cref="_listChangeGate"/>. Applies one change to the list (see
    /// <see cref="PhotoList.ApplyChanges"/>) and keeps the viewer on the same file: its position is recomputed, and if
    /// it was removed the nearest surviving neighbour in the old order is shown — the next one, else the previous one.
    /// <paramref name="goToFirst"/> (a folder switch) shows the first photo instead.
    /// </summary>
    private async Task ApplyListChangeAsync(ListChangeBatch batch, bool goToFirst)
    {
        var oldKeys = _photoList.Keys;
        int oldPosition = _nav.Position;
        int currentKey = _nav.Key;
        var currentPath = _photoList.GetPhoto(currentKey)?.FilePath;

        // Renamed photos keep their key; the canvas knows them by path, so it has to follow.
        var removed = _photoList.ApplyChanges(batch, currentKey,
            onRenamed: _canvasController.OnPhotoRenamed);
        var keys = _photoList.Keys;
        if (ReferenceEquals(keys, oldKeys)) return; // nothing actually changed

        // Detach removed photos from the cache and the dictionary BEFORE navigating: the window move below
        // releases whatever loaded photo falls outside the new window, and removed photos are released by
        // Retire instead (once, not twice).
        _cache.Forget(removed.ConvertAll(r => r.Key));
        _photoList.Forget(removed);
        try
        {
            // Removed photos are already forgotten, so "still in the list" is exactly "survived".
            int newPosition = goToFirst ? 0 : ListChange.PositionAfter(oldKeys, oldPosition, keys, currentKey,
                _photoList.Contains,
                isReplacement: key => currentPath != null &&
                                      string.Equals(_photoList.GetPhoto(key)?.FilePath, currentPath, StringComparison.OrdinalIgnoreCase));
            if (keys[newPosition] == currentKey)
            {
                // Same photo, new position: nothing to re-show, but the cache, the strip and the counter must follow.
                _nav = new NavState(keys, newPosition);
                _cache.MoveWindow(_nav);
                _thumbNailController.RequestThumbnailRedraw();
                UpdateFileNameAndDetails();
            }
            else
            {
                await FlyTo(keys, newPosition);
            }
        }
        finally
        {
            // The canvas holds its own lease on whatever it still draws, and a load still running for a removed photo
            // releases what it brings, so retiring at once is safe. Done whatever happened above: they are gone.
            foreach (var (_, photo) in removed) photo.Slots.Retire();
        }
    }

    // --- Rename ---

    /// <summary>Whether rename may run now; the same rules as <see cref="CanDeleteCurrentPhoto"/>.</summary>
    public bool CanRenameCurrentPhoto()
    {
        if (!_initialFileListingCompleted) return false; // the list is still just the opened photo
        if (IsBurst()) return false;
        if (_displayLevel != DisplayLevel.Hq) return false;
        return true;
    }

    /// <summary>
    /// Renames the photo on screen to <paramref name="newFileName"/> in the same folder. The viewer stays on it: in
    /// place in Explorer mode, at its new sorted position in Folder mode.
    /// </summary>
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

    /// <summary>Copies the photo on screen (the file, and the image at the level shown) to the clipboard.</summary>
    public Task CopyFileToClipboardAsync()
    {
        if (_photoList.GetPhoto(_nav.Key) is not { } photo)
            return Task.CompletedTask;
        return PhotoClipboard.CopyToClipboardAsync(photo, _displayLevel);
    }

    // --- Queries ---

    // UI thread. A navigation key is being held (more presses than the threshold since the last Brake).
    private bool IsBurst() => _keyPressCounter > BurstThreshold;

    /// <summary>Only one photo in the list (also true until the listing is published).</summary>
    public bool IsSinglePhoto() => _photoList.Count <= 1;

    /// <summary>The path of the photo on screen.</summary>
    public string GetFullPathCurrentFile() => _photoList[_nav.Key].FilePath;

    /// <summary>Whether the on-screen photo is a RAW file. Lock-free read; safe to call cross-thread.</summary>
    public bool CurrentPhotoIsRaw =>
        _photoList.GetPhoto(_nav.Key) is { IsRaw: true };

    // --- For tests: the state the invariants are checked against (DisplayHarness). UI thread. ---

    /// <summary>The prefetch cache (tests step it by hand).</summary>
    internal PrefetchCache Cache => _cache;
    /// <summary>The photo list.</summary>
    internal PhotoList Photos => _photoList;
    /// <summary>Where the viewer is.</summary>
    internal NavState Nav => _nav;
    /// <summary>The level the canvas was last asked to show.</summary>
    internal DisplayLevel ShownLevel => _displayLevel;
    /// <summary>Whether the full listing has replaced the startup list.</summary>
    internal bool ListingPublished => _initialFileListingCompleted;

    /// <summary>Applies a list change the way Folder mode and delete do, inside the list-change gate.</summary>
    internal async Task ChangeListAsync(ListChangeBatch batch, bool goToFirst = false)
    {
        await _listChangeGate.WaitAsync();
        try { await ApplyListChangeAsync(batch, goToFirst); }
        finally { _listChangeGate.Release(); }
    }

    // --- Cleanup ---

    /// <summary>
    /// UI thread. Stops loading and releases every image the photos hold; the canvas keeps its own lease on the one
    /// it still draws (the close animation), and decodes still running release theirs when they return.
    /// </summary>
    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _firstPhotoLoadedTcs.TrySetCanceled();
        DisposeFolderMode();
        _cache.Dispose(); // first: once its thread has stopped, nothing else stores into the slots retired below
        foreach (int key in _photoList.Keys) _photoList.GetPhoto(key)?.Slots.Retire();
        Logger.Info("PhotoDisplayController disposed.");
    }
}
