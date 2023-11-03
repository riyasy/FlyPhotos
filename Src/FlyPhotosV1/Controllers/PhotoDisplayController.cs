using FlyPhotos.Data;
using FlyPhotos.Utils;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FlyPhotos.Controllers;

internal class PhotoDisplayController
{
    public enum DisplayLevel
    {
        None,
        PlaceHolder,
        Preview,
        Hq
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int CacheSizeOneSideHqImages = 2;
    private const int CacheSizeOneSidePreviews = 300;
    private readonly int MaxConcurrentTasksHqImages = Environment.ProcessorCount;
    private readonly int MaxConcurrentTasksPreviews = Environment.ProcessorCount;

    private int _currentIndex;
    private List<string> _files = new();

    private bool _firstPhotoLoaded;
    private readonly AutoResetEvent _firstPhotoLoadEvent = new(false);

    private readonly ConcurrentStack<int> _toBeCachedHqImages = new();
    private readonly ConcurrentDictionary<int, Photo> _cachedHqImages = new();
    private readonly AutoResetEvent _waitForHqImagesToBeCached = new(false);
    private readonly ConcurrentDictionary<int, bool> _hqsBeingCached = new();

    private readonly ConcurrentStack<int> _toBeCachedPreviews = new();
    private readonly ConcurrentDictionary<int, Photo> _cachedPreviews = new();
    private readonly AutoResetEvent _waitForPreviewsToBeCached = new(false);
    private readonly ConcurrentDictionary<int, bool> _previewsBeingCached = new();

    private int _keyPressCounter;
    private DisplayLevel _currentDisplayLevel;

    private readonly Action<string, string> _progressUpdateCallback;
    private readonly WpfImageController _canvasController;
    private readonly Dispatcher _dispatcher;

    private int _hqCacheTasksCount;
    private int _previewCacheTasksCount;

    public PhotoDisplayController(WpfImageController canvasController, Action<string, string> statusCallback,
        Dispatcher dispatcher)
    {
        _progressUpdateCallback = statusCallback;
        _dispatcher = dispatcher;
        _canvasController = canvasController;

        var thread = new Thread(() => { GetFileListFromExplorer(App.SelectedFileName); });
        thread.SetApartmentState(ApartmentState.STA); // This is needed for COM interaction.
        thread.Start();
    }

    public void LoadFirstPhoto()
    {
        var preview = ImageUtil.GetFirstPreviewSpecialHandling(App.SelectedFileName, out var continueLoadingHq);

        _canvasController.Source = preview;
        _currentDisplayLevel = DisplayLevel.Hq;

        if (!continueLoadingHq)
        {
            _firstPhotoLoaded = true;
            _firstPhotoLoadEvent.Set();
            return;
        }

        void GetHqImage()
        {
            var hqImage = ImageUtil.GetHqImage(App.SelectedFileName);
            _dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(delegate
                {
                    _canvasController.Source = hqImage;
                    _firstPhotoLoaded = true;
                    _firstPhotoLoadEvent.Set();
                }));
        }
        _ = Task.Run(GetHqImage);
    }

    private void GetFileListFromExplorer(string selectedFileName)
    {
        try
        {
            var supportedExtensions = Util.SupportedExtensions;
            _files = App.Debug
                ? Util.FindAllFilesFromDirectory(App.DebugTestFolder)
                : Util.FindAllFilesFromExplorerWindowNative();
            _files = _files.Where(s =>
                supportedExtensions.Contains(Path.GetExtension(s).ToUpperInvariant())).ToList();

            if (!_firstPhotoLoaded) _firstPhotoLoadEvent.WaitOne();

            if (_files.Count <= 0) return;

            _currentIndex = Util.FindSelectedFileIndex(selectedFileName, _files);

            _cachedHqImages[_currentIndex] = _canvasController.Source;
            UpdateCacheLists();

            var previewCachingThread = new Thread(PreviewCacheBuilderDoWork) { IsBackground = true };
            previewCachingThread.Start();
            var hqCachingThread = new Thread(HqImageCacheBuilderDoWork) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            hqCachingThread.Start();
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
                _waitForPreviewsToBeCached.WaitOne();
            }

            if (_toBeCachedPreviews.IsEmpty) continue;

            var maxxedOut = _previewCacheTasksCount >= MaxConcurrentTasksPreviews;
            if (maxxedOut) waitForSomeGap.WaitOne();

            if (!_toBeCachedPreviews.TryPop(out var item)) continue;

            Task.Run(() =>
            {
                var index = item;
                _previewsBeingCached[index] = true;
                var image = ImageUtil.GetPreview(_files[index]);
                _cachedPreviews[index] = image;
                _previewsBeingCached.Remove(index, out bool temp);
                if (_currentIndex == index)
                    UpgradeImageIfNeeded(image, DisplayLevel.PlaceHolder, DisplayLevel.Preview);
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
            if (_toBeCachedHqImages.IsEmpty || IsContinuousKeyPress()) _waitForHqImagesToBeCached.WaitOne();

            if (_toBeCachedHqImages.IsEmpty) continue;

            var maxxedOut = _hqCacheTasksCount >= MaxConcurrentTasksHqImages;
            if (maxxedOut) waitForSomeGap.WaitOne();

            if (!_toBeCachedHqImages.TryPop(out var item)) continue;

            Task.Run(() =>
            {
                var index = item;
                _hqsBeingCached[index] = true;
                var image = ImageUtil.GetHqImage(_files[index]);
                _cachedHqImages[index] = image;
                _hqsBeingCached.Remove(index, out bool temp);
                if (_currentIndex == index)
                    UpgradeImageIfNeeded(image, DisplayLevel.Preview, DisplayLevel.Hq);
                _hqCacheTasksCount--;
                if (_hqCacheTasksCount == MaxConcurrentTasksHqImages - 1) waitForSomeGap.Set();
            });
            _hqCacheTasksCount++;
        }
    }

    private void UpgradeImageIfNeeded(Photo image, DisplayLevel fromDisplayState,
        DisplayLevel toDisplayState)
    {
        if (_currentDisplayLevel == fromDisplayState)
            _dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(delegate
                {
                    _canvasController.Source = image;
                    _currentDisplayLevel = toDisplayState;
                }));
    }

    private void UpdateCacheLists()
    {
        var curIdx = _currentIndex;

        var keysToRemove = _cachedHqImages.Keys.Where(key =>
            key > curIdx + CacheSizeOneSideHqImages || key < curIdx - CacheSizeOneSideHqImages).ToList();
        keysToRemove.ForEach(key => _cachedHqImages.TryRemove(key, out _));

        keysToRemove = _cachedPreviews.Keys.Where(key =>
            key > curIdx + CacheSizeOneSidePreviews || key < curIdx - CacheSizeOneSidePreviews).ToList();
        keysToRemove.ForEach(key => _cachedPreviews.TryRemove(key, out _));

        // Create new list of to be cached HQ Images.
        var cacheIndexesHqImages = new List<int>();
        for (var i = CacheSizeOneSideHqImages; i >= 1; i--)
        {
            cacheIndexesHqImages.Add(curIdx + i);
            cacheIndexesHqImages.Add(curIdx - i);
        }

        cacheIndexesHqImages.Add(curIdx);

        _toBeCachedHqImages.Clear();
        var tempArray = (from cacheIdx in cacheIndexesHqImages
                         where !_cachedHqImages.ContainsKey(cacheIdx) && !_hqsBeingCached.ContainsKey(cacheIdx) && cacheIdx >= 0 && cacheIdx < _files.Count
                         select cacheIdx).ToArray();
        if (tempArray.Length > 0) _toBeCachedHqImages.PushRange(tempArray);

        // Create new list of to be cached Previews.
        var cacheIndexesPreviews = new List<int>();
        for (var i = CacheSizeOneSidePreviews; i >= 1; i--)
        {
            cacheIndexesPreviews.Add(curIdx + i);
            cacheIndexesPreviews.Add(curIdx - i);
        }

        cacheIndexesPreviews.Add(curIdx);

        _toBeCachedPreviews.Clear();

        tempArray = (from cacheIdx in cacheIndexesPreviews
                     where !_cachedPreviews.ContainsKey(cacheIdx) && !_previewsBeingCached.ContainsKey(cacheIdx) && cacheIdx >= 0 && cacheIdx < _files.Count
                     select cacheIdx).ToArray();
        if (tempArray.Length > 0) _toBeCachedPreviews.PushRange(tempArray);
    }

    private void UpdateProgressStatusDebug()
    {
        // Debug code
        var totalFileCount = _files.Count;
        var noOfFilesOnLeft = _currentIndex;
        var noOfFilesOnRight = totalFileCount - 1 - _currentIndex;
        var noOfCachedItemsOnLeft = _cachedPreviews.Keys.Count(i => i < _currentIndex);
        var noOfCachedItemsOnRight = _cachedPreviews.Keys.Count(i => i > _currentIndex);
        var cacheProgressStatus =
            $"{noOfCachedItemsOnLeft}/{noOfFilesOnLeft} < Cache > {noOfCachedItemsOnRight}/{noOfFilesOnRight}";

        var fileName = Path.GetFileName(_files[_currentIndex]);
        var indexAndFileName = $"[{_currentIndex + 1}/{_files.Count}] {fileName}";

        _dispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(delegate { _progressUpdateCallback(indexAndFileName, cacheProgressStatus); }));
    }

    public void Fly(NavDirection direction)
    {
        _keyPressCounter++;
        if (_cachedPreviews.Count <= 1) return;

        DisplayNextPhoto(direction);
        UpdateProgressStatusDebug();
        _waitForPreviewsToBeCached.Set();
    }

    public void Brake()
    {
        if (_cachedHqImages.TryGetValue(_currentIndex, out var hqImage))
        {
            _canvasController.Source = hqImage;
            _currentDisplayLevel = DisplayLevel.Hq;
        }

        _waitForHqImagesToBeCached.Set();
        _keyPressCounter = 0;
    }

    private bool IsContinuousKeyPress()
    {
        return _keyPressCounter > 1;
    }

    private void DisplayNextPhoto(NavDirection direction)
    {
        switch (direction)
        {
            case NavDirection.Next when _currentIndex >= _files.Count - 1:
            case NavDirection.Prev when _currentIndex <= 0:
                return;
            case NavDirection.Next:
                _currentIndex++;
                break;
            case NavDirection.Prev:
                _currentIndex--;
                break;
        }

        if (!IsContinuousKeyPress() && _cachedHqImages.TryGetValue(_currentIndex, out var hqImage))
        {
            _canvasController.Source = hqImage;
            _currentDisplayLevel = DisplayLevel.Hq;
        }
        else if (_cachedPreviews.TryGetValue(_currentIndex, out var preview))
        {
            _canvasController.Source = preview;
            _currentDisplayLevel = DisplayLevel.Preview;
        }
        else
        {
            _canvasController.Source = ImageUtil.LoadingIndicator;
            _currentDisplayLevel = DisplayLevel.PlaceHolder;
        }

        UpdateCacheLists();
    }

    public bool IsSinglePhoto()
    {
        return _cachedPreviews.Count <= 1;
    }

    public enum NavDirection
    {
        Next,
        Prev
    }
}