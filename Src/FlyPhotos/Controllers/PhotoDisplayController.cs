#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Controllers;

internal class PhotoDisplayController : IPhotoDisplayController, IDisposable
{
    public event EventHandler<StatusUpdateEventArgs> StatusUpdated;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly int MaxConcurrentTasksHqImages = Environment.ProcessorCount;
    private readonly int MaxConcurrentTasksPreviews = Environment.ProcessorCount;

    private readonly List<Photo> _photos = [];

    private bool _firstPhotoLoaded;
    private readonly AutoResetEvent _firstPhotoLoadEvent = new(false);

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
    private int _hqCacheTasksCount;
    private int _previewCacheTasksCount;
    private Photo _firstPhoto;

    public PhotoDisplayController(CanvasControl d2dCanvas, ICanvasController canvasController, IThumbnailController thumbNailController,
        PhotoSessionState photoSessionState)
    {
        _d2dCanvas = d2dCanvas;
        _canvasController = canvasController;
        _thumbNailController = thumbNailController;
        _photoSessionState = photoSessionState;
        _thumbNailController.SetPreviewCacheReference(_cachedPreviews);

        var thread = new Thread(() => { GetFileListFromExplorer(_photoSessionState.FirstPhotoPath); });
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
        _firstPhotoLoaded = true;
        _firstPhotoLoadEvent.Set();
    }

    private void GetFileListFromExplorer(string selectedFileName)
    {
        try
        {
            List<string> files = [];
            var supportedExtensions = Util.SupportedExtensions;
            if (!App.Debug)
            {
                files = Util.FindAllFilesFromExplorerWindowNative();
            }
            if (files.Count == 0)
            {
                files = Util.FindAllFilesFromDirectory(Path.GetDirectoryName(selectedFileName));
            }
            files = files.Where(s =>
                supportedExtensions.Contains(Path.GetExtension(s).ToUpperInvariant()) || s == selectedFileName).ToList();
            if (files.Count == 0)
            {
                files.Add(selectedFileName);
            }
            _photoSessionState.PhotosCount = files.Count;

            foreach (var s in files)
                _photos.Add(new Photo(s));


            if (!_firstPhotoLoaded) _firstPhotoLoadEvent.WaitOne();

            _photoSessionState.CurrentDisplayIndex = Util.FindSelectedFileIndex(selectedFileName, files);
            _photos[_photoSessionState.CurrentDisplayIndex] = _firstPhoto;
            _cachedHqImages[_photoSessionState.CurrentDisplayIndex] = _firstPhoto;

            UpdateCacheLists();

            var previewCachingThread = new Thread(PreviewCacheBuilderDoWork) { IsBackground = true };
            previewCachingThread.Start();
            var hqCachingThread = new Thread(HqImageCacheBuilderDoWork)
            { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            hqCachingThread.Start();
            var previewDiskCachingThread = new Thread(PreviewDiskCacherDoWork) { IsBackground = true };
            previewDiskCachingThread.Start();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void PreviewCacheBuilderDoWork()
    {
        var waitForSomeGap = new AutoResetEvent(false);

        while (true)
        {
            if (_toBeCachedPreviews.IsEmpty)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                _diskCachingCanStart.Set();
                _previewCachingCanStart.WaitOne();
                _diskCachingCanStart.Reset();
            }

            if (_toBeCachedPreviews.IsEmpty) continue;

            var maxxedOut = _previewCacheTasksCount >= MaxConcurrentTasksPreviews;
            if (maxxedOut) waitForSomeGap.WaitOne();

            if (!_toBeCachedPreviews.TryPop(out var item)) continue;

            Task.Run(() =>
            {
                var index = item;
                _previewsBeingCached[index] = true;
                var photo = _photos[index];
                photo.LoadPreview(_d2dCanvas);
                _cachedPreviews[index] = photo;
                _previewsBeingCached.Remove(index, out _);

                if (photo.Preview.PreviewFrom == PreviewSource.FromDisk) { _toBeDiskCachedPreviews.Push(item); }

                if (_photoSessionState.CurrentDisplayIndex == index)
                    UpgradeImageIfNeeded(photo, DisplayLevel.PlaceHolder, DisplayLevel.Preview);

                _thumbNailController.RedrawThumbNailsIfNeeded(index);

                UpdateProgressStatusDebug();
                _previewCacheTasksCount--;
                if (_previewCacheTasksCount == MaxConcurrentTasksPreviews - 1) waitForSomeGap.Set();
            });
            _previewCacheTasksCount++;
        }
    }

    private void HqImageCacheBuilderDoWork()
    {
        var waitForSomeGap = new AutoResetEvent(false);

        while (true)
        {
            if (_toBeCachedHqImages.IsEmpty || IsContinuousKeyPress()) _hqCachingCanStart.WaitOne();

            if (_toBeCachedHqImages.IsEmpty) continue;

            var maxxedOut = _hqCacheTasksCount >= MaxConcurrentTasksHqImages;
            if (maxxedOut) waitForSomeGap.WaitOne();

            if (!_toBeCachedHqImages.TryPop(out var item)) continue;

            Task.Run(() =>
            {
                var index = item;
                _hqsBeingCached[index] = true;
                var photo = _photos[index];
                photo.LoadHq(_d2dCanvas);
                _cachedHqImages[index] = photo;
                _hqsBeingCached.Remove(index, out _);

                if (_photoSessionState.CurrentDisplayIndex == index)
                    UpgradeImageIfNeeded(photo, DisplayLevel.Preview, DisplayLevel.Hq);
                _hqCacheTasksCount--;
                if (_hqCacheTasksCount == MaxConcurrentTasksHqImages - 1) waitForSomeGap.Set();
            });
            _hqCacheTasksCount++;
        }
    }

    private void PreviewDiskCacherDoWork()
    {
        SemaphoreSlim semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        while (true)
        {
            _diskCachingCanStart.WaitOne();
            if (_toBeDiskCachedPreviews.IsEmpty)
            {
                _diskCachingCanStart.Reset();
                continue;
            }

            if (!_toBeDiskCachedPreviews.TryPop(out var index)) 
                continue;

            semaphore.Wait();

            Task.Run(async () =>
            {
                try
                {
                    if (_cachedPreviews.TryGetValue(index, out var image) &&
                        image.Preview != null &&
                        image.Preview.PreviewFrom == PreviewSource.FromDisk)
                    {
                        await DiskCacherWithSqlite.Instance.PutInCache(image.FileName, image.Preview.Bitmap);
                    }
                }
                finally { semaphore.Release(); }
            });
        }
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
        var curIdx = _photoSessionState.CurrentDisplayIndex;

        var keysToRemove = _cachedHqImages.Keys.Where(key =>
            key > curIdx + AppConfig.Settings.CacheSizeOneSideHqImages ||
            key < curIdx - AppConfig.Settings.CacheSizeOneSideHqImages).ToList();
        keysToRemove.ForEach(delegate (int key)
        {
            _photos[key].Hq = null;
            _cachedHqImages.TryRemove(key, out _);
        });

        keysToRemove = _cachedPreviews.Keys.Where(key =>
            key > curIdx + AppConfig.Settings.CacheSizeOneSidePreviews ||
            key < curIdx - AppConfig.Settings.CacheSizeOneSidePreviews).ToList();
        keysToRemove.ForEach(delegate (int key)
        {
            _photos[key].Preview = null;
            _cachedPreviews.TryRemove(key, out _);
        });

        // Create new list of to be cached HQ Images.
        var cacheIndexesHqImages = new List<int>();
        for (var i = AppConfig.Settings.CacheSizeOneSideHqImages; i >= 1; i--)
        {
            cacheIndexesHqImages.Add(curIdx + i);
            cacheIndexesHqImages.Add(curIdx - i);
        }

        cacheIndexesHqImages.Add(curIdx);

        _toBeCachedHqImages.Clear();
        var tempArray = (from cacheIdx in cacheIndexesHqImages
                         where !_cachedHqImages.ContainsKey(cacheIdx) && !_hqsBeingCached.ContainsKey(cacheIdx) && cacheIdx >= 0 &&
                               cacheIdx < _photos.Count
                         select cacheIdx).ToArray();
        if (tempArray.Length > 0) _toBeCachedHqImages.PushRange(tempArray);

        // Create new list of to be cached Previews.
        var cacheIndexesPreviews = new List<int>();
        for (var i = AppConfig.Settings.CacheSizeOneSidePreviews; i >= 1; i--)
        {
            cacheIndexesPreviews.Add(curIdx + i);
            cacheIndexesPreviews.Add(curIdx - i);
        }

        cacheIndexesPreviews.Add(curIdx);

        _toBeCachedPreviews.Clear();

        tempArray = (from cacheIdx in cacheIndexesPreviews
                     where !_cachedPreviews.ContainsKey(cacheIdx) && !_previewsBeingCached.ContainsKey(cacheIdx) &&
                           cacheIdx >= 0 && cacheIdx < _photos.Count
                     select cacheIdx).ToArray();
        if (tempArray.Length > 0) _toBeCachedPreviews.PushRange(tempArray);
    }

    private void UpdateProgressStatusDebug()
    {
        // Debug code
        var totalFileCount = _photos.Count;
        var noOfFilesOnLeft = _photoSessionState.CurrentDisplayIndex;
        var noOfFilesOnRight = totalFileCount - 1 - _photoSessionState.CurrentDisplayIndex;
        var noOfCachedItemsOnLeft = _cachedPreviews.Keys.Count(i => i < _photoSessionState.CurrentDisplayIndex);
        var noOfCachedItemsOnRight = _cachedPreviews.Keys.Count(i => i > _photoSessionState.CurrentDisplayIndex);
        var cacheProgressStatus =
            $"{noOfCachedItemsOnLeft}/{noOfFilesOnLeft} < Cache > {noOfCachedItemsOnRight}/{noOfFilesOnRight}";

        var fileName = Path.GetFileName(_photos[_photoSessionState.CurrentDisplayIndex].FileName);
        var indexAndFileName = $"[{_photoSessionState.CurrentDisplayIndex + 1}/{_photos.Count}] {fileName}";

        StatusUpdated?.Invoke(this, new StatusUpdateEventArgs(indexAndFileName, cacheProgressStatus));
    }

    public async Task CopyFileToClipboardAsync()
    {
        try
        {
            string filePath = GetFullPathCurrentFile();
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            DataPackage dataPackage = new DataPackage();
            dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            dataPackage.SetStorageItems(new List<IStorageItem> { file });
            Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public async Task Fly(NavDirection direction)
    {
        _keyPressCounter++;
        if (_cachedPreviews.Count <= 1) return;

        await DisplayNextPhoto(direction);
        UpdateProgressStatusDebug();
        _previewCachingCanStart.Set();
    }

    public async Task Brake()
    {
        if (_photoSessionState.CurrentDisplayLevel != DisplayLevel.Hq && _cachedHqImages.TryGetValue(_photoSessionState.CurrentDisplayIndex, out var hqImage))
        {
            await _canvasController.SetSource(hqImage, DisplayLevel.Hq);
        }

        _hqCachingCanStart.Set();
        _keyPressCounter = 0;
    }

    public async Task FlyBy(int shiftBy)
    {
        if (_cachedPreviews.Count <= 1) return;

        _photoSessionState.CurrentDisplayIndex += shiftBy;

        if (AppConfig.Settings.ResetPanZoomOnNextPhoto) _canvasController.SetHundredPercent(false);

        var photo = _photos[_photoSessionState.CurrentDisplayIndex];

        if (!IsContinuousKeyPress() && _cachedHqImages.ContainsKey(_photoSessionState.CurrentDisplayIndex))
        {
            await _canvasController.SetSource(photo, DisplayLevel.Hq);
        }
        else if (_cachedPreviews.ContainsKey(_photoSessionState.CurrentDisplayIndex))
        {
            await _canvasController.SetSource(photo, DisplayLevel.Preview);
        }
        else
        {
            await _canvasController.SetSource(photo, DisplayLevel.PlaceHolder);
        }
        UpdateCacheLists();
        UpdateProgressStatusDebug();
        _previewCachingCanStart.Set();
        _hqCachingCanStart.Set();
    }

    private bool IsContinuousKeyPress()
    {
        return _keyPressCounter > 1;
    }

    private async Task DisplayNextPhoto(NavDirection direction)
    {
        switch (direction)
        {
            case NavDirection.Next when _photoSessionState.CurrentDisplayIndex >= _photos.Count - 1:
            case NavDirection.Prev when _photoSessionState.CurrentDisplayIndex <= 0:
                return;
            case NavDirection.Next:
                _photoSessionState.CurrentDisplayIndex++;
                break;
            case NavDirection.Prev:
                _photoSessionState.CurrentDisplayIndex--;
                break;
        }

        if (AppConfig.Settings.ResetPanZoomOnNextPhoto) _canvasController.SetHundredPercent(false);

        var photo = _photos[_photoSessionState.CurrentDisplayIndex];

        if (!IsContinuousKeyPress() && _cachedHqImages.ContainsKey(_photoSessionState.CurrentDisplayIndex))
        {
            await _canvasController.SetSource(photo, DisplayLevel.Hq);
        }
        else if (_cachedPreviews.ContainsKey(_photoSessionState.CurrentDisplayIndex))
        {
            await _canvasController.SetSource(photo, DisplayLevel.Preview);
        }
        else
        {
            await _canvasController.SetSource(photo, DisplayLevel.PlaceHolder);
        }
        UpdateCacheLists();
    }

    public bool IsSinglePhoto()
    {
        return _cachedPreviews.Count <= 1;
    }

    public string GetFullPathCurrentFile()
    {
        return _photos[_photoSessionState.CurrentDisplayIndex].FileName;
    }



    public void Dispose()
    {
        
    }
}

internal class StatusUpdateEventArgs(string indexAndFileName, string cacheProgressStatus) : EventArgs
{
    public string IndexAndFileName { get; } = indexAndFileName;
    public string CacheProgressStatus { get; } = cacheProgressStatus;
}