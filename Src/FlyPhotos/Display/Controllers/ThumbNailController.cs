using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.UI.Screens;
using FlyPhotos.UI.Views;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Display.Controllers;

internal partial class ThumbNailController : IThumbnailController
{
    // --- Events ---
    public event Action<int> ThumbnailClicked;

    // --- Private Settings ---
    private int _numOfThumbNailsInOneDirection = 20;
    private Color _thumbNailSelectionColor;
    private int _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;

    // --- Drawing Optimization related ---
    private bool _invalidatePending;
    private bool _redrawNeeded;
    private bool _canDrawThumbnails;
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(150);
    private readonly DispatcherTimer _throttledRedrawTimer = new()
    {
        Interval = ThrottleInterval
    };

    // --- References ---
    private readonly CanvasControl _d2dCanvasThumbNail;
    private readonly PhotoSessionState _photoSessionState;
    private CanvasRenderTarget _thumbnailOffscreen;
    private ConcurrentDictionary<int, Photo> _cachedPreviews;
    private List<int> _sortedPhotoKeys;

    public ThumbNailController(CanvasControl d2dCanvasThumbNail, PhotoSessionState photoSessionState)
    {
        _d2dCanvasThumbNail = d2dCanvasThumbNail;
        _photoSessionState = photoSessionState;
        _d2dCanvasThumbNail.Draw += D2dCanvasThumbNail_Draw;
        _d2dCanvasThumbNail.SizeChanged += D2dCanvasThumbNail_SizeChanged;
        _d2dCanvasThumbNail.Loaded += D2dCanvasThumbNail_Loaded;
        _throttledRedrawTimer.Tick += ThrottledRedrawTimer_Tick;
        _d2dCanvasThumbNail.PointerPressed += D2dCanvasThumbNail_PointerPressed;
        _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
    }

    // --- Public Methods ---

    public void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews)
    {
        _cachedPreviews = cachedPreviews;
    }

    public void SetSortedPhotoKeysReference(List<int> sortedPhotoKeys)
    {
        _sortedPhotoKeys = sortedPhotoKeys;
    }

    public void ShowHideThumbnailBasedOnSettings()
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
            _thumbnailOffscreen?.Dispose();
            _thumbnailOffscreen = null;
        }
    }

    public void RefreshThumbnail()
    {
        _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
        _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;
        if (AppConfig.Settings.ShowThumbnails)
        {
            CreateThumbnailRibbonOffScreen();
        }
    }

    /// <summary>
    /// Called when an external thumbnail has been loaded.
    /// This method throttles redraw requests to prevent overwhelming the UI thread.
    /// </summary>
    public void RedrawThumbNailsIfNeeded(int updatedKey)
    {
        // Guard against calls before references are set.
        if (_sortedPhotoKeys == null || _sortedPhotoKeys.Count == 0) return;

        // Find the POSITION of the current and updated keys.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        int updatedPosition = _sortedPhotoKeys.BinarySearch(updatedKey);

        // If either key isn't found (e.g., already deleted), we can't proceed.
        if (currentPosition < 0 || updatedPosition < 0) return;

        // Check if the updated thumbnail is within the visible POSITIONAL range.
        if (updatedPosition >= currentPosition - _numOfThumbNailsInOneDirection &&
            updatedPosition <= currentPosition + _numOfThumbNailsInOneDirection)
        {
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() =>
            {
                _canDrawThumbnails = true;
                _redrawNeeded = true;
                if (!_throttledRedrawTimer.IsEnabled)
                {
                    _throttledRedrawTimer.Start();
                }
            });
        }
    }

    // --- Event Handlers ---


    private void D2dCanvasThumbNail_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_sortedPhotoKeys == null || _sortedPhotoKeys.Count <= 1) return;

        var pos = e.GetCurrentPoint(_d2dCanvasThumbNail).Position;
        double canvasCenterX = _d2dCanvasThumbNail.ActualWidth / 2;
        double clickedX = pos.X;

        // 1. Calculate the positional offset. This logic was correct.
        int offset = (int)Math.Round((clickedX - canvasCenterX) / _thumbnailBoxSize);

        if (offset == 0) return; // No navigation needed if the center thumbnail is clicked.

        // 2. Find the POSITION of the current key.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return;

        // 3. Calculate the new target POSITION.
        int newPosition = currentPosition + offset;

        // 4. Validate the NEW POSITION against the bounds of the sorted key list.
        if (newPosition >= 0 && newPosition < _sortedPhotoKeys.Count)
        {
            // 5. Fire the event. The PhotoDisplayController will handle the navigation.
            ThumbnailClicked?.Invoke(offset);
        }
    }

    private void D2dCanvasThumbNail_Loaded(object sender, RoutedEventArgs e)
    {
        _d2dCanvasThumbNail.Visibility = AppConfig.Settings.ShowThumbnails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void D2dCanvasThumbNail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _numOfThumbNailsInOneDirection = (int)(_d2dCanvasThumbNail.ActualWidth / (2 * _thumbnailBoxSize)) + 1;
        CreateThumbnailRibbonOffScreen();
    }

    private void D2dCanvasThumbNail_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_thumbnailOffscreen != null && AppConfig.Settings.ShowThumbnails)
        {
            var drawRect = _thumbnailOffscreen.Bounds;
            args.DrawingSession.Transform = System.Numerics.Matrix3x2.Identity;
            args.DrawingSession.DrawImage(_thumbnailOffscreen, drawRect, _thumbnailOffscreen.Bounds, 0.8f,
                CanvasImageInterpolation.Linear);
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


        if (_sortedPhotoKeys == null || _sortedPhotoKeys.Count < 1 || !_canDrawThumbnails || !AppConfig.Settings.ShowThumbnails)
        {
            _thumbnailOffscreen?.Dispose();
            _thumbnailOffscreen = null;
            RequestInvalidate();
            return;
        }

        if (_d2dCanvasThumbNail.ActualWidth <= 0) return; // Guard against drawing with no size

        // Recreate render target if needed (e.g., after size change)
        if (_thumbnailOffscreen == null ||
            (int)Math.Round(_thumbnailOffscreen.Size.Width) != (int)Math.Round(_d2dCanvasThumbNail.ActualWidth) ||
            (int)Math.Round(_thumbnailOffscreen.Size.Height) != (int)Math.Round(_d2dCanvasThumbNail.ActualHeight))
        {
            _thumbnailOffscreen?.Dispose();
            _numOfThumbNailsInOneDirection = (int)(_d2dCanvasThumbNail.ActualWidth / (2 * _thumbnailBoxSize)) + 1;
            _thumbnailOffscreen = new CanvasRenderTarget(_d2dCanvasThumbNail, (float)_d2dCanvasThumbNail.ActualWidth, _thumbnailBoxSize);
        }

        // Find the position of the currently displayed photo.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        if (currentPosition < 0) return; // Can't draw if the current photo is invalid.

        using var dsThumbNail = _thumbnailOffscreen.CreateDrawingSession();
        dsThumbNail.Clear(Colors.Transparent);

        if (_cachedPreviews != null)
        {
            var startX = (int)_d2dCanvasThumbNail.ActualWidth / 2 - _thumbnailBoxSize / 2;
            var startY = 0;


            for (var i = -_numOfThumbNailsInOneDirection; i <= _numOfThumbNailsInOneDirection; i++)
            {

                var thumbnailPosition = currentPosition + i;

                if (thumbnailPosition < 0 || thumbnailPosition >= _sortedPhotoKeys.Count) continue;

                var key = _sortedPhotoKeys[thumbnailPosition];

                var bitmap = (_cachedPreviews.TryGetValue(key, out var photo) && photo.Preview?.Bitmap != null)
                    ? photo.Preview.Bitmap
                    : Photo.GetLoadingIndicator().Bitmap;

                // Calculate the source crop rectangle (same as before, this was correct)
                float bitmapWidth = bitmap.SizeInPixels.Width;
                float bitmapHeight = bitmap.SizeInPixels.Height;
                var cropSize = Math.Min(bitmapWidth, bitmapHeight);
                var cropX = (bitmapWidth - cropSize) / 2;
                var cropY = (bitmapHeight - cropSize) / 2;
                var sourceRect = new Rect(cropX, cropY, cropSize, cropSize);

                // Calculate the destination rectangle on our canvas (same as before, this was correct)
                var destX = startX + i * _thumbnailBoxSize;

                var destRect = new Rect(
                    destX + Constants.ThumbnailPadding, 
                    startY + Constants.ThumbnailPadding, 
                    _thumbnailBoxSize - (Constants.ThumbnailPadding * 2), 
                    _thumbnailBoxSize - (Constants.ThumbnailPadding * 2));

                // 1. Define the clipping shape (our rounded rectangle)
                using (var clipGeometry = CanvasGeometry.CreateRoundedRectangle(dsThumbNail, destRect, Constants.ThumbnailCornerRadius, Constants.ThumbnailCornerRadius))
                {
                    // 2. Create a clipping layer. All drawing inside this 'using' block will be clipped to the geometry.
                    using (dsThumbNail.CreateLayer(1.0f, clipGeometry))
                    {
                        dsThumbNail.DrawImage(bitmap, destRect, sourceRect, 1f, CanvasImageInterpolation.NearestNeighbor);
                    } // The clipping layer is automatically disposed and removed here.
                }

                // Draw the selection indicator on top, without any clipping.
                if (key == _photoSessionState.CurrentPhotoKey)
                {
                    // Use DrawRoundedRectangle to match the shape of the thumbnail.
                    dsThumbNail.DrawRoundedRectangle(destRect, Constants.ThumbnailCornerRadius, Constants.ThumbnailCornerRadius, _thumbNailSelectionColor, Constants.ThumbnailSelectionBorderThickness);
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
        _sortedPhotoKeys = null;
    }
}