using Microsoft.Graphics.Canvas;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System.Diagnostics;
using Vanara.PInvoke;

namespace FlyPhotos.Controllers;
public class PhotoController : IDisposable
{
    private readonly CanvasControl _d2dCanvas;
    private List<string> _photoPaths;
    private int _currentIndex;

    private Photo _currentPhotoThumbnail;
    private Photo _currentPhotoHighQuality;

    private readonly ConcurrentDictionary<int, Photo> _thumbnailCache;
    private readonly ConcurrentDictionary<int, Photo> _highQualityCache;

    private readonly ManualResetEventSlim _thumbnailLoaderSignal;
    private readonly ManualResetEventSlim _highQualityLoaderSignal;
    private CancellationTokenSource _cancellationTokenSource;

    internal Photo CurrentPhotoThumbnail
    {
        get => _currentPhotoThumbnail;
        private set => _currentPhotoThumbnail = value;
    }

    internal Photo CurrentPhotoHighQuality
    {
        get => _currentPhotoHighQuality;
        private set => _currentPhotoHighQuality = value;
    }

    public PhotoController(string path, CanvasControl d2dCanvas)
    {
        _d2dCanvas = d2dCanvas;
        var directory = Path.GetDirectoryName(path);
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff" };

        _photoPaths = Directory.GetFiles(directory)
            .Where(file => supportedExtensions.Contains(Path.GetExtension(file).ToLower()))
            .OrderBy(file => file)
            .ToList();

        _currentIndex = _photoPaths.IndexOf(path);

        _thumbnailCache = new ConcurrentDictionary<int, Photo>();
        _highQualityCache = new ConcurrentDictionary<int, Photo>();

        _thumbnailLoaderSignal = new ManualResetEventSlim(false);
        _highQualityLoaderSignal = new ManualResetEventSlim(false);
        _cancellationTokenSource = new CancellationTokenSource();

        StartThumbnailLoaderThread();
        StartHighQualityLoaderThread();

        LoadCurrentPhoto();
        SignalLoaders();
    }

    private async void LoadCurrentPhoto()
    {
        if (!_thumbnailCache.TryGetValue(_currentIndex, out _currentPhotoThumbnail))
        {
            CurrentPhotoThumbnail = await ImageUtil.GetPreview(_d2dCanvas, _photoPaths[_currentIndex]);
            _thumbnailCache[_currentIndex] = CurrentPhotoThumbnail;
        }

        if (!_highQualityCache.TryGetValue(_currentIndex, out _currentPhotoHighQuality))
        {
            CurrentPhotoHighQuality = await ImageUtil.GetHqImage(_d2dCanvas, _photoPaths[_currentIndex]);
            _highQualityCache[_currentIndex] = CurrentPhotoHighQuality;
        }
    }

    public void GoNext()
    {
        if (_currentIndex < _photoPaths.Count - 1)
        {
            _currentIndex++;
            RefreshCurrentPhoto();
        }
    }

    public void GoPrevious()
    {
        if (_currentIndex > 0)
        {
            _currentIndex--;
            RefreshCurrentPhoto();
        }
    }

    private void RefreshCurrentPhoto()
    {
        // Cancel any ongoing operations
        _cancellationTokenSource.Cancel();

        // Create a new CancellationTokenSource
        _cancellationTokenSource = new CancellationTokenSource();

        LoadCurrentPhoto();
        SignalLoaders();
        ManageCache();
    }

    private void SignalLoaders()
    {
        _thumbnailLoaderSignal.Set();
        _highQualityLoaderSignal.Set();
    }

    private void StartThumbnailLoaderThread()
    {
        Task.Run(() =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {


                try
                {
                    _thumbnailLoaderSignal.Wait(_cancellationTokenSource.Token);
                    _thumbnailLoaderSignal.Reset();

                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    var token = _cancellationTokenSource.Token;

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        CancellationToken = token
                    };

                    Parallel.ForEach(GetIndexRange(_currentIndex, 10), parallelOptions, (index, loopState) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            loopState.Stop();
                            return;
                        }

                        if (!_thumbnailCache.ContainsKey(index))
                        {
                            var path = _photoPaths[index];
                            var thumbnail = ImageUtil.GetPreview(_d2dCanvas, path).Result;
                            _thumbnailCache[index] = thumbnail;

                            // Log the index and path name after getting the thumbnail
                            Debug.WriteLine($"[ThumbnailLoader] Loaded index: {index}, path: {path}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Handle the cancellation gracefully
                    Debug.WriteLine("[ThumbnailLoader] Operation was canceled.");
                }
            }
        }, _cancellationTokenSource.Token);
    }

    private void StartHighQualityLoaderThread()
    {
        Task.Run(() =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {


                try
                {
                    _highQualityLoaderSignal.Wait(_cancellationTokenSource.Token);
                    _highQualityLoaderSignal.Reset();

                    if (_cancellationTokenSource.Token.IsCancellationRequested) break;

                    var token = _cancellationTokenSource.Token;

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        CancellationToken = token
                    };

                    Parallel.ForEach(GetIndexRange(_currentIndex, 2), parallelOptions, (index, loopState) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            loopState.Stop();
                            return;
                        }

                        if (!_highQualityCache.ContainsKey(index))
                        {
                            var path = _photoPaths[index];
                            var highQuality = ImageUtil.GetHqImage(_d2dCanvas, path).Result;
                            _highQualityCache[index] = highQuality;

                            // Log the index and path name after getting the high-quality image
                            Debug.WriteLine($"[HighQualityLoader] Loaded index: {index}, path: {path}");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Handle the cancellation gracefully
                    Debug.WriteLine("[HighQualityLoader] Operation was canceled.");
                }
            }
        }, _cancellationTokenSource.Token);
    }





    private IEnumerable<int> GetIndexRange(int currentIndex, int range)
    {
        int startIndex = Math.Max(currentIndex - range, 0);
        int endIndex = Math.Min(currentIndex + range, _photoPaths.Count - 1);

        return Enumerable.Range(startIndex, endIndex - startIndex + 1);
    }

    private void ManageCache()
    {
        // Define the valid range for caching
        var validThumbnailRange = GetIndexRange(_currentIndex, 10).ToHashSet();
        var validHighQualityRange = GetIndexRange(_currentIndex, 2).ToHashSet();

        // Remove out-of-range items from the thumbnail cache
        foreach (var key in _thumbnailCache.Keys.ToList())
        {
            if (!validThumbnailRange.Contains(key))
            {
                _thumbnailCache.TryRemove(key, out _);
            }
        }

        // Remove out-of-range items from the high-quality cache
        foreach (var key in _highQualityCache.Keys.ToList())
        {
            if (!validHighQualityRange.Contains(key))
            {
                _highQualityCache.TryRemove(key, out _);
            }
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _thumbnailLoaderSignal.Set();
        _highQualityLoaderSignal.Set();
        _cancellationTokenSource.Dispose();
    }
}
