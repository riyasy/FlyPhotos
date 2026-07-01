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

        _cache = new PrefetchCache(_photoList.GetPhoto, _d2dCanvas, IsContinuousKeyPress);
        _cache.PreviewReady += OnPreviewReady;
        _cache.HqReady += OnHqReady;
        _cache.CacheStatusChanged += status => CacheStatusChanged?.Invoke(status);

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
        try
        {
            // Secondary instances only display the single opened image.
            // No folder or Explorer-window discovery is performed.
            IReadOnlyList<string> files = AppConfig.Volatile.IsSecondaryInstance ?
                [selectedFilePath] : FileDiscovery.DiscoverFiles(selectedFilePath, _photoSessionState.FlyLaunchedExternally);

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
            UpgradeImageIfNeeded(photo, DisplayLevel.PlaceHolder, DisplayLevel.Preview);
        _thumbNailController.RedrawThumbNailsIfNeeded(key);
    }

    private void OnHqReady(int key)
    {
        if (_photoSessionState.CurrentPhotoKey == key && _photoList.GetPhoto(key) is { } photo)
            UpgradeImageIfNeeded(photo, DisplayLevel.Preview, DisplayLevel.Hq);
    }

    private async Task DisplayPhotoAtKey(int key, bool triggerHqCaching)
    {
        if (_photoList.GetPhoto(key) is not { } photo) return;

        if (!IsContinuousKeyPress() && _cache.IsHqLoaded(key))
            await SetSourceAsync(photo, DisplayLevel.Hq);
        else if (_cache.IsPreviewLoaded(key))
            await SetSourceAsync(photo, DisplayLevel.Preview);
        else
            await SetSourceAsync(photo, DisplayLevel.PlaceHolder);

        _cache.MoveWindow(_photoSessionState.CurrentPhotoListPosition, _photoList.Keys, signalHqLoading: triggerHqCaching);
    }

    private void UpgradeImageIfNeeded(Photo photo, DisplayLevel fromDisplayState,
        DisplayLevel toDisplayState)
    {
        if (_photoSessionState.CurrentDisplayLevel == fromDisplayState)
            _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
            {
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
        Interlocked.Increment(ref _keyPressCounter);
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return; // TODO Should not happen if state is consistent
        int newPosition = currentPosition + (direction == NavDirection.Next ? 1 : -1);
        if (newPosition < 0 || newPosition >= keys.Count) return;
        int newKey = keys[newPosition];
        await FlyTo(newKey, newPosition, false);
    }

    public async Task FlyToFirst()
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
        await FlyTo(keys[0], 0, true);
    }

    public async Task FlyToLast()
    {
        var keys = _photoList.Keys;
        if (keys.Count <= 1) return;
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
        _cache.SignalHqLoading();
        _keyPressCounter = 0;
    }

    // --- File info ---

    private void UpdateFileNameAndDetails()
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
                var (w, h) = photo.GetActualSize();
                dimensionText = $"({w} x {h})";
            }

            FileNameOrDetailsChanged?.Invoke(new FileDisplayDetails(positionText, fileName, dimensionText));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public void RefreshFileNameAndDetails()
    {
        UpdateFileNameAndDetails();
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
        // --- Step 1: Guard Clauses and State Capture ---
        var keyToDelete = _photoSessionState.CurrentPhotoKey;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        // Safety check: If the current key isn't in our sorted list, something is wrong.
        if (currentPosition < 0)
        {
            Logger.Error($"Inconsistent state: Could not find key {keyToDelete} in sorted list during deletion.");
            return new DeleteResult(false, false, "App - Inconsistent State");
        }

        if (_photoList.GetPhoto(keyToDelete) is not { } photo)
        {
            Logger.Error($"DeleteFileFromDiskAsync failed: Key {keyToDelete} not found in the collection.");
            return new DeleteResult(false, false, "App - Inconsistent State");
        }

        var delResult = await StorageOps.DeleteFileFromDisk(photo.FilePath);
        if (!delResult.DeleteSuccess) return delResult;

        // --- Step 2: Determine the Next Photo to Display ---
        var currentKeys = _photoList.Keys; // capture snapshot before mutating
        int nextPosition = (currentPosition >= currentKeys.Count - 1) ? currentPosition - 1 : currentPosition;

        // --- Step 3: Remove from the collection (drops the dictionary entry and republishes the snapshot) ---
        var deletedPhoto = _photoList.RemoveAt(currentPosition);
        _cache.Evict(keyToDelete);

        // --- Step 4: Navigate to the New Photo or Handle Empty State ---
        var updatedKeys = _photoList.Keys;
        if (updatedKeys.Count > 0)
        {
            await FlyTo(updatedKeys[nextPosition], nextPosition, true);
            // --- Step 5: Clean Up Resources ---
            deletedPhoto?.Dispose();
            return new DeleteResult(true, false);
        }
        else
        {
            // TODO - Cleanup properly. If we call dispose now, the zoom out animation during
            // app close will crash as the draw call will try to display disposed canvas bitmaps.
            return new DeleteResult(true, true);
        }
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

    public string GetFullPathCurrentFile() => _photoList[_photoSessionState.CurrentPhotoKey].FilePath;

    // --- Cleanup ---

    public void Dispose()
    {
        _cts.Cancel();
        _firstPhotoLoadedTcs.TrySetCanceled();
        _cache.Dispose();
        _cts.Dispose();
        Logger.Info("PhotoDisplayController disposed.");
    }
}
