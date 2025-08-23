using System;
using System.Collections.Concurrent;
using Windows.Foundation;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Controllers;

internal class ThumbNailController : IThumbnailController
{
    private bool _canDrawThumbnails;
    private bool _invalidatePending;

    private readonly CanvasControl _d2dCanvasThumbNail;
    private readonly PhotoSessionState _photoSessionState;
    private CanvasRenderTarget _thumbnailOffscreen;
    private ConcurrentDictionary<int, Photo> _cachedPreviews;

    private readonly DispatcherTimer _redrawThumbnailTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 500)
    };

    private int _numOfThumbNailsInOneDirection = 20;

    public event Action<int> ThumbnailClicked;

    public ThumbNailController(CanvasControl d2dCanvasThumbNail, PhotoSessionState photoSessionState)
    {
        _d2dCanvasThumbNail = d2dCanvasThumbNail;
        _photoSessionState = photoSessionState;
        _d2dCanvasThumbNail.Draw += _d2dCanvasThumbNail_Draw;
        _d2dCanvasThumbNail.SizeChanged += _d2dCanvasThumbNail_SizeChanged;
        _d2dCanvasThumbNail.Loaded += _d2dCanvasThumbNail_Loaded;
        _redrawThumbnailTimer.Tick += RedrawThumbnailTimer_Tick;
        _d2dCanvasThumbNail.PointerPressed += _d2dCanvasThumbNail_PointerPressed;
    }

    private void _d2dCanvasThumbNail_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_d2dCanvasThumbNail).Position;
        double canvasCenterX = _d2dCanvasThumbNail.ActualWidth / 2;
        double clickedX = pos.X;

        int offset = (int)Math.Round((clickedX - canvasCenterX) / Constants.ThumbnailBoxSize);
        if (offset != 0 && _photoSessionState.PhotosCount > 1)
        {
            int newIndex = _photoSessionState.CurrentDisplayIndex + offset;
            if (newIndex >= 0 && newIndex < _photoSessionState.PhotosCount)
            {
                ThumbnailClicked?.Invoke(offset);
            }
        }
    }

    private void _d2dCanvasThumbNail_Loaded(object sender, RoutedEventArgs e)
    {
        _d2dCanvasThumbNail.Visibility = AppConfig.Settings.ShowThumbnails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void _d2dCanvasThumbNail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _numOfThumbNailsInOneDirection = (int)(_d2dCanvasThumbNail.ActualWidth / (2 * Constants.ThumbnailBoxSize)) + 1;
        CreateThumbnailRibbonOffScreen();
        RequestInvalidate();
    }

    public void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews)
    {
        _cachedPreviews = cachedPreviews;
    }

    private void _d2dCanvasThumbNail_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_thumbnailOffscreen != null && AppConfig.Settings.ShowThumbnails)
        {
            var drawRect = _thumbnailOffscreen.Bounds;
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.Identity;
            args.DrawingSession.DrawImage(_thumbnailOffscreen, drawRect, _thumbnailOffscreen.Bounds, 0.8f,
                CanvasImageInterpolation.NearestNeighbor);
        }
    }

    public void ShowThumbnailBasedOnSettings()
    {
        if (AppConfig.Settings.ShowThumbnails)
        {
            _d2dCanvasThumbNail.Visibility = Visibility.Visible;
            CreateThumbnailRibbonOffScreen();
            RequestInvalidate();
        }
        else
        {
            _d2dCanvasThumbNail.Visibility = Visibility.Collapsed;
            RequestInvalidate();
        }
    }

    public void RedrawThumbNailsIfNeeded(int index)
    {
        if (index >= _photoSessionState.CurrentDisplayIndex - _numOfThumbNailsInOneDirection &&
            index <= _photoSessionState.CurrentDisplayIndex + _numOfThumbNailsInOneDirection)
        {
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() =>
            {
                _canDrawThumbnails = true;
                _redrawThumbnailTimer.Stop();
                _redrawThumbnailTimer.Start();
            });
        }
    }

    private void RedrawThumbnailTimer_Tick(object sender, object e)
    {
        CreateThumbnailRibbonOffScreen();
        RequestInvalidate();
    }

    public void CreateThumbnailRibbonOffScreen()
    {
        _redrawThumbnailTimer.Stop();
        if (_photoSessionState.PhotosCount <= 1 || !_canDrawThumbnails || !AppConfig.Settings.ShowThumbnails)
            return;

        if (_thumbnailOffscreen == null)
        {
            _numOfThumbNailsInOneDirection = (int)(_d2dCanvasThumbNail.ActualWidth / (2 * Constants.ThumbnailBoxSize)) + 1;
            _thumbnailOffscreen = new CanvasRenderTarget(_d2dCanvasThumbNail, (float)_d2dCanvasThumbNail.ActualWidth, Constants.ThumbnailBoxSize);
        }

        using var dsThumbNail = _thumbnailOffscreen.CreateDrawingSession();
        dsThumbNail.Clear(Colors.Transparent);

        if (_cachedPreviews != null)
        {
            var startX = (int)_d2dCanvasThumbNail.ActualWidth / 2 - Constants.ThumbnailBoxSize / 2;
            var startY = 0;

            for (var i = -_numOfThumbNailsInOneDirection; i <= _numOfThumbNailsInOneDirection; i++)
            {
                var index = _photoSessionState.CurrentDisplayIndex + i;

                if (index < 0 || index >= (_photoSessionState.PhotosCount)) continue;

                CanvasBitmap bitmap;

                if (_cachedPreviews.TryGetValue(index, out var photo) && photo.Preview != null && photo.Preview.Bitmap != null)
                {
                    bitmap = photo.Preview.Bitmap;
                }
                else
                {
                    bitmap = Photo.GetLoadingIndicator().Bitmap;
                }

                // Calculate the center square crop
                float bitmapWidth = bitmap.SizeInPixels.Width;
                float bitmapHeight = bitmap.SizeInPixels.Height;
                var cropSize = Math.Min(bitmapWidth, bitmapHeight);

                var cropX = (bitmapWidth - cropSize) / 2;
                var cropY = (bitmapHeight - cropSize) / 2;

                var destX = startX + i * Constants.ThumbnailBoxSize;

                // Draw the cropped center of the image
                dsThumbNail.DrawImage(
                    bitmap,
                    new Rect(destX, startY, Constants.ThumbnailBoxSize, Constants.ThumbnailBoxSize),
                    new Rect(cropX, cropY, cropSize, cropSize), 1f,
                    CanvasImageInterpolation.NearestNeighbor // Source rectangle for the crop
                );
                if (index == _photoSessionState.CurrentDisplayIndex)
                {
                    dsThumbNail.DrawRectangle(new Rect(destX, startY, Constants.ThumbnailBoxSize, Constants.ThumbnailBoxSize),
                        Colors.GreenYellow, 3f);
                }
            }
            RequestInvalidate();
        }
    }
    private void RequestInvalidate()
    {
        if (_invalidatePending) return;
        _invalidatePending = true;

        _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() =>
        {
            _invalidatePending = false;
            _d2dCanvasThumbNail.Invalidate();
        });
    }

    public void Dispose()
    {
        _redrawThumbnailTimer.Stop();
        _redrawThumbnailTimer.Tick -= RedrawThumbnailTimer_Tick;

        if (_d2dCanvasThumbNail != null)
        {
            _d2dCanvasThumbNail.Draw -= _d2dCanvasThumbNail_Draw;
            _d2dCanvasThumbNail.SizeChanged -= _d2dCanvasThumbNail_SizeChanged;
            _d2dCanvasThumbNail.Loaded -= _d2dCanvasThumbNail_Loaded;
            _d2dCanvasThumbNail.PointerPressed -= _d2dCanvasThumbNail_PointerPressed;
        }

        _thumbnailOffscreen?.Dispose();
        _thumbnailOffscreen = null;

        ThumbnailClicked = null;

        _cachedPreviews = null;
    }
}


