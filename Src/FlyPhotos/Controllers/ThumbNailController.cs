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

internal partial class ThumbNailController : IThumbnailController
{
    // A shorter interval for more responsive UI updates.
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(150);

    private bool _canDrawThumbnails;
    private bool _invalidatePending;
    private bool _redrawNeeded; // Flag to track if a redraw is needed.

    private readonly CanvasControl _d2dCanvasThumbNail;
    private readonly PhotoSessionState _photoSessionState;
    private CanvasRenderTarget _thumbnailOffscreen;
    private ConcurrentDictionary<int, Photo> _cachedPreviews;

    // Renamed for clarity. This timer will now implement throttling.
    private readonly DispatcherTimer _throttledRedrawTimer = new()
    {
        Interval = ThrottleInterval
    };

    private int _numOfThumbNailsInOneDirection = 20;

    public event Action<int> ThumbnailClicked;

    public ThumbNailController(CanvasControl d2dCanvasThumbNail, PhotoSessionState photoSessionState)
    {
        _d2dCanvasThumbNail = d2dCanvasThumbNail;
        _photoSessionState = photoSessionState;
        _d2dCanvasThumbNail.Draw += D2dCanvasThumbNail_Draw;
        _d2dCanvasThumbNail.SizeChanged += D2dCanvasThumbNail_SizeChanged;
        _d2dCanvasThumbNail.Loaded += D2dCanvasThumbNail_Loaded;
        _throttledRedrawTimer.Tick += ThrottledRedrawTimer_Tick;
        _d2dCanvasThumbNail.PointerPressed += D2dCanvasThumbNail_PointerPressed;
    }

    // --- Public Methods ---

    public void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews)
    {
        _cachedPreviews = cachedPreviews;
    }

    public void ShowThumbnailBasedOnSettings()
    {
        if (AppConfig.Settings.ShowThumbnails)
        {
            _d2dCanvasThumbNail.Visibility = Visibility.Visible;
            _canDrawThumbnails = true; // Enable drawing when shown
            CreateThumbnailRibbonOffScreen();
        }
        else
        {
            _d2dCanvasThumbNail.Visibility = Visibility.Collapsed;
            // Optionally clear the offscreen buffer to save memory
            _thumbnailOffscreen?.Dispose();
            _thumbnailOffscreen = null;
        }
    }

    /// <summary>
    /// Called when an external thumbnail has been loaded.
    /// This method throttles redraw requests to prevent overwhelming the UI thread.
    /// </summary>
    public void RedrawThumbNailsIfNeeded(int index)
    {
        // Check if the updated thumbnail is actually visible
        if (index >= _photoSessionState.CurrentDisplayIndex - _numOfThumbNailsInOneDirection &&
            index <= _photoSessionState.CurrentDisplayIndex + _numOfThumbNailsInOneDirection)
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() =>
            {
                _canDrawThumbnails = true;
                _redrawNeeded = true;

                // If the timer is not already running, start it.
                // This ensures the redraw happens at most once per ThrottleInterval.
                if (!_throttledRedrawTimer.IsEnabled)
                {
                    _throttledRedrawTimer.Start();
                }
            });
    }

    // --- Event Handlers ---

    private void D2dCanvasThumbNail_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
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

    private void D2dCanvasThumbNail_Loaded(object sender, RoutedEventArgs e)
    {
        _d2dCanvasThumbNail.Visibility = AppConfig.Settings.ShowThumbnails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void D2dCanvasThumbNail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _numOfThumbNailsInOneDirection = (int)(_d2dCanvasThumbNail.ActualWidth / (2 * Constants.ThumbnailBoxSize)) + 1;
        CreateThumbnailRibbonOffScreen();
    }

    private void D2dCanvasThumbNail_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_thumbnailOffscreen != null && AppConfig.Settings.ShowThumbnails)
        {
            var drawRect = _thumbnailOffscreen.Bounds;
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.Identity;
            args.DrawingSession.DrawImage(_thumbnailOffscreen, drawRect, _thumbnailOffscreen.Bounds, 0.8f,
                CanvasImageInterpolation.NearestNeighbor);
        }
    }

    // --- Drawing Logic ---

    /// <summary>
    /// This now fires once per throttled interval.
    /// </summary>
    private void ThrottledRedrawTimer_Tick(object sender, object e)
    {
        // Stop the timer, making it a one-shot. It will be restarted by the next call
        // to RedrawThumbNailsIfNeeded if more updates come in.
        _throttledRedrawTimer.Stop();

        if (!_redrawNeeded) return;

        _redrawNeeded = false; // Reset the flag

        CreateThumbnailRibbonOffScreen();
    }

    /// <summary>
    /// Renders the entire visible thumbnail strip to an off-screen bitmap.
    /// This is the expensive operation that we are throttling.
    /// </summary>
    public void CreateThumbnailRibbonOffScreen()
    {
        // This method is now called by the throttled timer or direct UI actions.
        // We can stop the timer here as a safeguard, but it's primarily stopped in the Tick handler.
        _throttledRedrawTimer.Stop();

        if (_photoSessionState.PhotosCount <= 1 || !_canDrawThumbnails || !AppConfig.Settings.ShowThumbnails)
            return;

        if (_d2dCanvasThumbNail.ActualWidth <= 0) return; // Guard against drawing with no size

        // Recreate render target if needed (e.g., after size change)
        if (_thumbnailOffscreen == null || 
            (int)Math.Round(_thumbnailOffscreen.Size.Width) != (int)Math.Round(_d2dCanvasThumbNail.ActualWidth))
        {
            _thumbnailOffscreen?.Dispose();
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

                if (index < 0 || index >= _photoSessionState.PhotosCount) continue;

                var bitmap = (_cachedPreviews.TryGetValue(index, out var photo) && photo.Preview?.Bitmap != null)
                    ? photo.Preview.Bitmap
                    : Photo.GetLoadingIndicator().Bitmap;

                // Calculate the center square crop
                float bitmapWidth = bitmap.SizeInPixels.Width;
                float bitmapHeight = bitmap.SizeInPixels.Height;
                var cropSize = Math.Min(bitmapWidth, bitmapHeight);
                var cropX = (bitmapWidth - cropSize) / 2;
                var cropY = (bitmapHeight - cropSize) / 2;
                var destX = startX + i * Constants.ThumbnailBoxSize;

                dsThumbNail.DrawImage(
                    bitmap,
                    new Rect(destX, startY, Constants.ThumbnailBoxSize, Constants.ThumbnailBoxSize),
                    new Rect(cropX, cropY, cropSize, cropSize), 1f,
                    CanvasImageInterpolation.NearestNeighbor
                );

                if (index == _photoSessionState.CurrentDisplayIndex)
                {
                    dsThumbNail.DrawRectangle(new Rect(destX, startY, Constants.ThumbnailBoxSize, Constants.ThumbnailBoxSize),
                        Colors.GreenYellow, 3f);
                }
            }
        }

        RequestInvalidate();
    }

    /// <summary>
    /// Coalesces multiple Invalidate requests into a single one on the DispatcherQueue.
    /// </summary>
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

    // --- Cleanup ---

    public void Dispose()
    {
        _throttledRedrawTimer.Stop();
        _throttledRedrawTimer.Tick -= ThrottledRedrawTimer_Tick;

        if (_d2dCanvasThumbNail != null)
        {
            _d2dCanvasThumbNail.Draw -= D2dCanvasThumbNail_Draw;
            _d2dCanvasThumbNail.SizeChanged -= D2dCanvasThumbNail_SizeChanged;
            _d2dCanvasThumbNail.Loaded -= D2dCanvasThumbNail_Loaded;
            _d2dCanvasThumbNail.PointerPressed -= D2dCanvasThumbNail_PointerPressed;
        }

        _thumbnailOffscreen?.Dispose();
        _thumbnailOffscreen = null;
        ThumbnailClicked = null;
        _cachedPreviews = null;
    }
}