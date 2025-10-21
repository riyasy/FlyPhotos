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
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            _photoSessionState.CurrentDisplayKey = Util.FindSelectedFileIndex(selectedFileName, files);
            _photos[_photoSessionState.CurrentDisplayKey] = _firstPhoto;
            _cachedHqImages[_photoSessionState.CurrentDisplayKey] = _firstPhoto;

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
                        var index = item;
                        _previewsBeingCached[index] = true;
                        var photo = _photos[index];
                        await photo.LoadPreview(_d2dCanvas); // TODO In a future refactor, this could accept a token
                        _cachedPreviews[index] = photo;
                        _previewsBeingCached.Remove(index, out _);
                        if (photo.Preview?.Origin == Origin.Disk) { _toBeDiskCachedPreviews.Push(item); }
                        if (_photoSessionState.CurrentDisplayKey == index)
                            UpgradeImageIfNeeded(photo, DisplayLevel.PlaceHolder, DisplayLevel.Preview);
                        _thumbNailController.RedrawThumbNailsIfNeeded(index);
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
                        var index = item;
                        _hqsBeingCached[index] = true;
                        var photo = _photos[index];
                        await photo.LoadHq(_d2dCanvas); // TODO In a future refactor, this could accept a token
                        _cachedHqImages[index] = photo;
                        _hqsBeingCached.Remove(index, out _);
                        if (_photoSessionState.CurrentDisplayKey == index)
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

                if (!_toBeDiskCachedPreviews.TryPop(out var index)) continue;

                _diskCacheTaskThrottler.Wait(token);

                Task.Run(async () =>
                {
                    try
                    {
                        if (_cachedPreviews.TryGetValue(index, out var image) &&
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
        var curIdx = _photoSessionState.CurrentDisplayKey;
        int currentPositionInList = _sortedPhotoKeys.BinarySearch(curIdx);
        if (currentPositionInList < 0)
        {
            Logger.Warn($"Current photo key {curIdx} not found in key list. Aborting cache update.");
            return;
        }

        var sw = Stopwatch.StartNew();

        var desiredHqKeys = FindNeighborKeys(currentPositionInList, AppConfig.Settings.CacheSizeOneSideHqImages);
        var desiredPreviewKeys = FindNeighborKeys(currentPositionInList, AppConfig.Settings.CacheSizeOneSidePreviews);
        SyncCacheState(desiredHqKeys, _cachedHqImages, _hqsBeingCached, _toBeCachedHqImages,
            photo => { photo.Hq?.Dispose(); photo.Hq = null; });
        SyncCacheState(desiredPreviewKeys, _cachedPreviews, _previewsBeingCached, _toBeCachedPreviews,
            photo => { photo.Preview?.Dispose(); photo.Preview = null; });

        sw.Stop();
        Logger.Trace($"UpdateCacheLists took {sw.ElapsedMilliseconds} ms");
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
        var currentKey = _photoSessionState.CurrentDisplayKey;
        int currentPosition = _sortedPhotoKeys.BinarySearch(currentKey);
        if (currentPosition < 0) return; // TODO - Should never happen. Photo not found, can't update status

        var totalFileCount = _sortedPhotoKeys.Count;
        var noOfFilesOnLeft = currentPosition;
        var noOfFilesOnRight = totalFileCount - 1 - currentPosition;

        // TODO - Get rid of Binary Search somwhow.. NOt here everywhere where _sortedPhotoKeys is used.
        var noOfCachedItemsOnLeft = _cachedPreviews.Keys.Count(key => _sortedPhotoKeys.BinarySearch(key) < currentPosition);
        var noOfCachedItemsOnRight = _cachedPreviews.Keys.Count(key => _sortedPhotoKeys.BinarySearch(key) > currentPosition);
        var cacheProgressStatus = $"{noOfCachedItemsOnLeft}/{noOfFilesOnLeft} < Cache > {noOfCachedItemsOnRight}/{noOfFilesOnRight}";

        var fileName = Path.GetFileName(_photos[currentKey].FileName);
        var indexAndFileName = $"[{currentPosition + 1}/{totalFileCount}] {fileName}";

        StatusUpdated?.Invoke(this, new StatusUpdateEventArgs(indexAndFileName, cacheProgressStatus));
    }


    public async Task CopyFileToClipboardAsync()
    {
        try
        {
            var photo = _photos[_photoSessionState.CurrentDisplayKey];
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
        int currentPosition = _sortedPhotoKeys.BinarySearch(_photoSessionState.CurrentDisplayKey);
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
        int currentPosition = _sortedPhotoKeys.BinarySearch(_photoSessionState.CurrentDisplayKey);
        if (currentPosition < 0) return;
        int newPosition = Math.Clamp(currentPosition + shiftBy, 0, _sortedPhotoKeys.Count - 1);
        int newKey = _sortedPhotoKeys[newPosition];
        await FlyTo(newKey, true);
    }

    private async Task FlyTo(int toKey, bool triggerHqCaching)
    {
        // Safety check if the target key is valid
        if (!_photos.ContainsKey(toKey)) return;
        _photoSessionState.CurrentDisplayKey = toKey;
        await DisplayPhotoAtKey(_photoSessionState.CurrentDisplayKey, triggerHqCaching);
    }

    public async Task DeleteCurrentPhoto()
    {
        var keyToDelete = _photoSessionState.CurrentDisplayKey;

        // Find where to navigate next BEFORE deleting
        int currentPosition = _sortedPhotoKeys.BinarySearch(keyToDelete);
        int nextPosition = (currentPosition >= _sortedPhotoKeys.Count - 1)
            ? currentPosition - 1 // If last, go to previous
            : currentPosition;    // Otherwise, the next element will slide into current position

        // 1. Remove from all data structures
        _photos.TryRemove(keyToDelete, out _);
        _sortedPhotoKeys.Remove(keyToDelete); // CRITICAL STEP

        // ... remove from caches, etc. ...

        // 2. Navigate to the new photo
        if (_sortedPhotoKeys.Count > 0)
        {
            int newKey = _sortedPhotoKeys[nextPosition];
            await FlyTo(newKey, true); // Fire-and-forget
        }
        else
        {
            // Handle case where last photo was deleted
        }
    }

    public async Task Brake()
    {
        if (_photoSessionState.CurrentDisplayLevel != DisplayLevel.Hq && _cachedHqImages.TryGetValue(_photoSessionState.CurrentDisplayKey, out var hqImage))
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
        return _photos[_photoSessionState.CurrentDisplayKey].FileName;
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

internal class StatusUpdateEventArgs(string indexAndFileName, string cacheProgressStatus) : EventArgs
{
    public string IndexAndFileName { get; } = indexAndFileName;
    public string CacheProgressStatus { get; } = cacheProgressStatus;
}