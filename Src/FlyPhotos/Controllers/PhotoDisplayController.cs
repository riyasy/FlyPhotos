#nullable enable
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FlyPhotos.Controllers;

internal partial class PhotoDisplayController
{
    public event EventHandler<StatusUpdateEventArgs>? StatusUpdated;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _previewTaskThrottler = new(Environment.ProcessorCount);
    private readonly SemaphoreSlim _hqTaskThrottler = new(Environment.ProcessorCount);
    private readonly SemaphoreSlim _diskCacheTaskThrottler = new(Environment.ProcessorCount);

    private readonly TaskCompletionSource<bool> _firstPhotoLoadedTcs = new();
    private Photo _firstPhoto;

    private readonly List<int> _sortedPhotoKeys = [];
    private readonly ConcurrentDictionary<int, Photo> _photos = [];

    private readonly ConcurrentStack<int> _toBeCachedHqImages = new();
    private readonly ConcurrentDictionary<int, Photo> _cachedHqImages = new();
    private readonly AutoResetEvent _hqCachingCanStart = new(false);
    private readonly ConcurrentDictionary<int, bool> _hqsBeingCached = new();

    private readonly ConcurrentStack<int> _toBeCachedPreviews = new();
    private readonly ConcurrentDictionary<int, Photo> _cachedPreviews = new();
    private readonly AutoResetEvent _previewCachingCanStart = new(false);
    private readonly ConcurrentDictionary<int, bool> _previewsBeingCached = new();

    private readonly ConcurrentStack<int> _toBeDiskCachedPreviews = new();
    private readonly ManualResetEvent _diskCachingCanStart = new(false);

    private int _keyPressCounter;

    private readonly CanvasControl _d2dCanvas;
    private readonly ICanvasController _canvasController;
    private readonly IThumbnailController _thumbNailController;
    private readonly PhotoSessionState _photoSessionState;

    public PhotoDisplayController(CanvasControl d2dCanvas, ICanvasController canvasController, IThumbnailController thumbNailController,
        PhotoSessionState photoSessionState)
    {
        _d2dCanvas = d2dCanvas;
        _canvasController = canvasController;
        _thumbNailController = thumbNailController;
        _photoSessionState = photoSessionState;
        _thumbNailController.SetPreviewCacheReference(_cachedPreviews);
        _thumbNailController.SetSortedPhotoKeysReference(_sortedPhotoKeys);
        _firstPhoto = Photo.Empty();

        var thread = new Thread(() => { DoStartupActivities(_photoSessionState.FirstPhotoPath, _cts.Token); });
        thread.SetApartmentState(ApartmentState.STA); // This is needed for COM interaction.
        thread.Start();
    }

    public async Task LoadFirstPhoto()
    {
        await ImageUtil.Initialize(_d2dCanvas);

        _firstPhoto = new Photo(_photoSessionState.FirstPhotoPath);
        bool continueLoadingHq = await _firstPhoto.LoadPreviewFirstPhoto(_d2dCanvas);

        if (continueLoadingHq)
        {
            await _canvasController.SetSource(_firstPhoto, DisplayLevel.Preview);
            await _firstPhoto.LoadHqFirstPhoto(_d2dCanvas);
        }

        await _canvasController.SetSource(_firstPhoto, DisplayLevel.Hq);
        if (AppConfig.Settings.OpenExitZoom)
            await Task.Delay(Constants.PanZoomAnimationDurationNormal);
        _firstPhotoLoadedTcs.SetResult(true);
    }

    private void DoStartupActivities(string selectedFileName, CancellationToken token)
    {
        try
        {
            var files = FileDiscoveryService.DiscoverFiles(selectedFileName);

            _photoSessionState.PhotosCount = files.Count;

            for (int i = 0; i < files.Count; i++)
            {
                _sortedPhotoKeys.Add(i);
                _photos[i] = new Photo(files[i]);
            }

            _firstPhotoLoadedTcs.Task.Wait(token);

            var currentDisplayIndex = Util.FindSelectedFileIndex(selectedFileName, files);
            // In the starting Keys and Index will be same as the keys are continous.
            _photoSessionState.SetCurrentPhotoKeyAndListPosition(currentDisplayIndex, currentDisplayIndex);

            _photos[_photoSessionState.CurrentPhotoKey] = _firstPhoto;
            _cachedHqImages[_photoSessionState.CurrentPhotoKey] = _firstPhoto;

            UpdateCacheLists();

            var previewCachingThread = new Thread(() => PreviewCacheBuilderDoWork(_cts.Token)) { IsBackground = true };
            previewCachingThread.Start();
            var hqCachingThread = new Thread(() => HqImageCacheBuilderDoWork(_cts.Token))
            { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            hqCachingThread.Start();
            var previewDiskCachingThread = new Thread(() => PreviewDiskCacherDoWork(_cts.Token)) { IsBackground = true };
            previewDiskCachingThread.Start();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void PreviewCacheBuilderDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_toBeCachedPreviews.IsEmpty)
                {
                    _diskCachingCanStart.Set();
                    WaitHandle.WaitAny([_previewCachingCanStart, token.WaitHandle]);
                    if (token.IsCancellationRequested) break;
                    _diskCachingCanStart.Reset();
                }

                if (!_toBeCachedPreviews.TryPop(out var item)) continue;

                _previewTaskThrottler.Wait(token);

                Task.Run(async () =>
                {
                    try
                    {
                        var key = item;
                        _previewsBeingCached[key] = true;
                        var photo = _photos[key];
                        await photo.LoadPreview(_d2dCanvas); // TODO In a future refactor, this could accept a token
                        _cachedPreviews[key] = photo;
                        _previewsBeingCached.Remove(key, out _);
                        if (photo.Preview?.Origin == Origin.Disk) { _toBeDiskCachedPreviews.Push(item); }
                        if (_photoSessionState.CurrentPhotoKey == key)
                            UpgradeImageIfNeeded(photo, DisplayLevel.PlaceHolder, DisplayLevel.Preview);
                        _thumbNailController.RedrawThumbNailsIfNeeded(key);
                        UpdateProgressStatusDebug();
                    }
                    finally { _previewTaskThrottler.Release(); }
                }, token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
        //Logger.Info("Preview Caching thread has shut down gracefully.");
    }

    private void HqImageCacheBuilderDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_toBeCachedHqImages.IsEmpty || IsContinuousKeyPress())
                {
                    WaitHandle.WaitAny([_hqCachingCanStart, token.WaitHandle]);
                    if (token.IsCancellationRequested) break;
                }

                if (!_toBeCachedHqImages.TryPop(out var item)) continue;

                _hqTaskThrottler.Wait(token);

                Task.Run(async () =>
                {
                    try
                    {
                        var key = item;
                        _hqsBeingCached[key] = true;
                        var photo = _photos[key];
                        await photo.LoadHq(_d2dCanvas); // TODO In a future refactor, this could accept a token
                        _cachedHqImages[key] = photo;
                        _hqsBeingCached.Remove(key, out _);
                        if (_photoSessionState.CurrentPhotoKey == key)
                            UpgradeImageIfNeeded(photo, DisplayLevel.Preview, DisplayLevel.Hq);
                    }
                    finally { _hqTaskThrottler.Release(); }
                }, token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
        //Logger.Info("HQ Image Caching thread has shut down gracefully.");
    }

    private void PreviewDiskCacherDoWork(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                WaitHandle.WaitAny([_diskCachingCanStart, token.WaitHandle]);
                if (token.IsCancellationRequested) break;

                if (_toBeDiskCachedPreviews.IsEmpty)
                {
                    _diskCachingCanStart.Reset();
                    continue;
                }

                if (!_toBeDiskCachedPreviews.TryPop(out var key)) continue;

                _diskCacheTaskThrottler.Wait(token);

                Task.Run(async () =>
                {
                    try
                    {
                        if (_cachedPreviews.TryGetValue(key, out var image) &&
                            image.Preview?.Origin == Origin.Disk)
                        {
                            var (actualWidth, actualHeight) = image.GetActualSize();
                            await DiskCacherWithSqlite.Instance.PutInCache(image.FileName, image.Preview.Bitmap, (int)Math.Round(actualWidth), (int)Math.Round(actualHeight));
                        }
                    }
                    finally { _diskCacheTaskThrottler.Release(); }
                }, token);
            }
        }
        catch (OperationCanceledException) { } // Expected on shutdown.
        //Logger.Info("Preview Disk Caching thread has shut down gracefully.");
    }

    private void UpgradeImageIfNeeded(Photo photo, DisplayLevel fromDisplayState,
        DisplayLevel toDisplayState)
    {
        if (_photoSessionState.CurrentDisplayLevel == fromDisplayState)
            _d2dCanvas.DispatcherQueue.TryEnqueue(() =>
            {
                _canvasController.SetSource(photo, toDisplayState);
            });
    }
    

    private void UpdateCacheLists()
    {
        if (_photos.IsEmpty)
        {
            _cachedHqImages.Clear();
            _cachedPreviews.Clear();
            return;
        }
        var currentKey = _photoSessionState.CurrentPhotoKey;
        int currentPos = _photoSessionState.CurrentPhotoListPosition;

        if (currentPos < 0)
        {
            Logger.Warn($"Current photo key {currentKey} not found in key list. Aborting cache update.");
            return;
        }

        var desiredHqKeys = FindNeighborKeys(currentPos, AppConfig.Settings.CacheSizeOneSideHqImages);
        var desiredPreviewKeys = FindNeighborKeys(currentPos, AppConfig.Settings.CacheSizeOneSidePreviews);
        SyncCacheState(desiredHqKeys, _cachedHqImages, _hqsBeingCached, _toBeCachedHqImages,
            photo => { photo.Hq?.Dispose(); photo.Hq = null; });
        SyncCacheState(desiredPreviewKeys, _cachedPreviews, _previewsBeingCached, _toBeCachedPreviews,
            photo => { photo.Preview?.Dispose(); photo.Preview = null; });
    }

    private void SyncCacheState(List<int> desiredKeys, ConcurrentDictionary<int, Photo> cachedItems,
        ConcurrentDictionary<int, bool> itemsBeingCached, ConcurrentStack<int> toBeCached, Action<Photo> disposeAction)
    {
        // Remove unwanted cached items
        var keysToKeep = new HashSet<int>(desiredKeys);
        var keysToRemove = new List<int>();
        foreach (int key in cachedItems.Keys)
            if (!keysToKeep.Contains(key))
                keysToRemove.Add(key);

        foreach (int key in keysToRemove)
        {
            if (_photos.TryGetValue(key, out var photo))
                disposeAction(photo);
            cachedItems.TryRemove(key, out _);
        }

        // Create new list of to be cached items
        toBeCached.Clear();
        var keysToPush = new List<int>();
        foreach (int key in desiredKeys)
            if (!cachedItems.ContainsKey(key) && !itemsBeingCached.ContainsKey(key))
                keysToPush.Add(key);

        if (keysToPush.Count > 0)
            toBeCached.PushRange([.. keysToPush]);
    }

    private List<int> FindNeighborKeys(int currentPosition, int cacheSizeOneSide)
    {
        var desiredKeys = new List<int>();
        for (int i = cacheSizeOneSide; i >= 1; i--)
        {
            int nextPos = currentPosition + i;
            if (nextPos < _sortedPhotoKeys.Count)
                desiredKeys.Add(_sortedPhotoKeys[nextPos]);

            int prevPos = currentPosition - i;
            if (prevPos >= 0)
                desiredKeys.Add(_sortedPhotoKeys[prevPos]);
        }
        desiredKeys.Add(_sortedPhotoKeys[currentPosition]);
        return desiredKeys;
    }

    private void UpdateProgressStatusDebug()
    {
        var currentKey = _photoSessionState.CurrentPhotoKey;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return;

        var totalFileCount = _sortedPhotoKeys.Count;
        var fileName = Path.GetFileName(_photos[currentKey].FileName);
        var listPositionAndFileName = $"[{currentPosition + 1}/{totalFileCount}] {fileName}";

        var noOfFilesOnLeft = currentPosition;
        var noOfFilesOnRight = totalFileCount - 1 - currentPosition;
        int noOfCachedItemsOnLeft = 0;
        int noOfCachedItemsOnRight = 0;

        foreach (int key in _cachedPreviews.Keys)
        {
            if (key < currentKey)            
                noOfCachedItemsOnLeft++;            
            else if (key > currentKey)            
                noOfCachedItemsOnRight++;            
        }
        var cacheProgressStatus = $"{noOfCachedItemsOnLeft}/{noOfFilesOnLeft} < Cache > {noOfCachedItemsOnRight}/{noOfFilesOnRight}";

        StatusUpdated?.Invoke(this, new StatusUpdateEventArgs(listPositionAndFileName, cacheProgressStatus));
    }


    public async Task CopyFileToClipboardAsync()
    {
        try
        {
            var photo = _photos[_photoSessionState.CurrentPhotoKey];
            var filePath = photo.FileName;

            if (!File.Exists(filePath)) return;

            var sourceFile = await StorageFile.GetFileFromPathAsync(filePath);
            var dataPackage = new DataPackage();
            if (sourceFile != null)
                dataPackage.SetStorageItems((List<IStorageItem>)[sourceFile]);

            var canvasBitmap = _photoSessionState.CurrentDisplayLevel switch
            {
                DisplayLevel.Hq => photo.Hq?.Bitmap,
                DisplayLevel.Preview => photo.Preview?.Bitmap,
                _ => null
            };

            if (canvasBitmap != null)
            {
                var memoryStream = new InMemoryRandomAccessStream();
                await canvasBitmap.SaveAsync(memoryStream, CanvasBitmapFileFormat.Png);
                memoryStream.Seek(0);
                var streamReference = RandomAccessStreamReference.CreateFromStream(memoryStream);
                dataPackage.SetBitmap(streamReference);
            }
            Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to copy CanvasBitmap to clipboard.");
        }
    }

    public async Task Fly(NavDirection direction)
    {
        _keyPressCounter++;
        if (_sortedPhotoKeys.Count <= 1) return;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return; // TODO Should not happen if state is consistent
        int newPosition = currentPosition + (direction == NavDirection.Next ? 1 : -1);
        if (newPosition < 0 || newPosition >= _sortedPhotoKeys.Count) return;
        int newKey = _sortedPhotoKeys[newPosition];
        await FlyTo(newKey, false); // Assuming FlyTo is modified as below
    }
    public async Task FlyToFirst()
    {
        if (_sortedPhotoKeys.Count <= 1) return;
        int firstKey = _sortedPhotoKeys[0];
        await FlyTo(firstKey, true);
    }

    public async Task FlyToLast()
    {
        if (_sortedPhotoKeys.Count <= 1) return;
        int lastKey = _sortedPhotoKeys[^1]; // or _sortedPhotoKeys[_sortedPhotoKeys.Count - 1]
        await FlyTo(lastKey, true);
    }

    public async Task FlyBy(int shiftBy)
    {
        if (_sortedPhotoKeys.Count <= 1) return;
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return;
        int newPosition = Math.Clamp(currentPosition + shiftBy, 0, _sortedPhotoKeys.Count - 1);
        int newKey = _sortedPhotoKeys[newPosition];
        await FlyTo(newKey, true);
    }

    private async Task FlyTo(int toKey, bool triggerHqCaching)
    {
        // Safety check if the target key is valid
        if (!_photos.ContainsKey(toKey)) return;

        _photoSessionState.SetCurrentPhotoKeyAndListPosition(toKey, _sortedPhotoKeys.BinarySearch(toKey));

        await DisplayPhotoAtKey(_photoSessionState.CurrentPhotoKey, triggerHqCaching);
    }

    public bool CanDeleteCurrentPhoto()
    {        
        if (_sortedPhotoKeys.Count == 0) return false;
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

        var delResult = await DeleteFileFromDisk(keyToDelete);
        if (!delResult.DeleteSuccess) return delResult;

        // --- Step 2: Determine the Next Photo to Display ---
        int nextPosition = (currentPosition >= _sortedPhotoKeys.Count - 1) ? currentPosition - 1 : currentPosition;

        // --- Step 3: Remove the Photo from All In-Memory Collections ---
        _photos.TryRemove(keyToDelete, out var deletedPhoto);
        _sortedPhotoKeys.RemoveAt(currentPosition);
        _cachedHqImages.TryRemove(keyToDelete, out _);
        _cachedPreviews.TryRemove(keyToDelete, out _);

        // --- Step 4: Navigate to the New Photo or Handle Empty State ---
        if (_sortedPhotoKeys.Count > 0)
        {
            int newKey = _sortedPhotoKeys[nextPosition];
            await FlyTo(newKey, true);

            // --- Step 5: Clean Up Resources ---
            if (deletedPhoto != null)
            {
                deletedPhoto.Hq?.Dispose();
                deletedPhoto.Preview?.Dispose();
            }
            return new DeleteResult(true, false);
        }
        else
        {
            // TODO - Cleanup properly. If we call dispose now, the zoom out animation during
            // app close will crash as the draw call will try to display disposed canvas bitmaps.
            return new DeleteResult(true, true);            
        }
    }

    private async Task<DeleteResult> DeleteFileFromDisk(int keyToDelete)
    {
        if (!_photos.TryGetValue(keyToDelete, out var photo))
        {
            Logger.Error($"DeleteFileFromDiskAsync failed: Key {keyToDelete} not found in the collection.");
            return new DeleteResult(false, false, "App - Inconsistent State");
        }
        var filePath = photo.FileName;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            await file.DeleteAsync(StorageDeleteOption.Default);
            Logger.Info($"Successfully deleted file: {filePath}");
            return new DeleteResult(true, false);
        }
        catch (Exception ex)
        {
            return new DeleteResult(false, false, $"Exeption : {ex.Message}");
        }
    }

    public async Task Brake()
    {
        if (_photoSessionState.CurrentDisplayLevel != DisplayLevel.Hq && _cachedHqImages.TryGetValue(_photoSessionState.CurrentPhotoKey, out var hqImage))
            await _canvasController.SetSource(hqImage, DisplayLevel.Hq);
        _hqCachingCanStart.Set();
        _keyPressCounter = 0;
    }

    private async Task DisplayPhotoAtKey(int key, bool triggerHqCaching)
    {
        var photo = _photos[key];

        if (!IsContinuousKeyPress() && _cachedHqImages.ContainsKey(key))
            await _canvasController.SetSource(photo, DisplayLevel.Hq);
        else if (_cachedPreviews.ContainsKey(key))
            await _canvasController.SetSource(photo, DisplayLevel.Preview);
        else
            await _canvasController.SetSource(photo, DisplayLevel.PlaceHolder);

        UpdateCacheLists();
        UpdateProgressStatusDebug();

        _previewCachingCanStart.Set();
        if (triggerHqCaching)
            _hqCachingCanStart.Set();
    }

    private bool IsContinuousKeyPress()
    {
        return _keyPressCounter > 1;
    }

    public bool IsSinglePhoto()
    {
        return _cachedPreviews.Count <= 1;
    }

    public string GetFullPathCurrentFile()
    {
        return _photos[_photoSessionState.CurrentPhotoKey].FileName;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _firstPhotoLoadedTcs.TrySetCanceled();
        _previewCachingCanStart.Set();
        _hqCachingCanStart.Set();
        _diskCachingCanStart.Set();

        // TODO, sort of wait for the three workers or the GetFileList thread to exit before
        // proceeding with disposing everything.
        _cts.Dispose();
        _previewTaskThrottler.Dispose();
        _hqTaskThrottler.Dispose();
        _diskCacheTaskThrottler.Dispose();

        _previewCachingCanStart.Dispose();
        _hqCachingCanStart.Dispose();
        _diskCachingCanStart.Dispose();

        Logger.Info("PhotoDisplayController disposed.");
    }
}

internal class StatusUpdateEventArgs(string listPositionAndFileName, string cacheProgressStatus) : EventArgs
{
    public string ListPositionAndFileName { get; } = listPositionAndFileName;
    public string CacheProgressStatus { get; } = cacheProgressStatus;
}

internal class DeleteResult(bool deleteSuccess, bool isLastPhoto, string failMessage = "")
{
    public bool DeleteSuccess { get; } = deleteSuccess;
    public bool IsLastPhoto { get; } = isLastPhoto;
    public string FailMessage { get; } = failMessage;
}