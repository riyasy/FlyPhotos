using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
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
    private Func<IReadOnlyList<int>> _getSortedPhotoKeys;
    private CanvasBitmap _loadingIndicatorBitmap;

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

    private void RunOnUiThread(Action action)
    {
        if (!_d2dCanvasThumbNail.DispatcherQueue.HasThreadAccess)
        {
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(() => action());
            return;
        }
        action();
    }

    // --- Public Methods ---

    public void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews)
    {
        _cachedPreviews = cachedPreviews;
    }

    public void SetSortedPhotoKeysProvider(Func<IReadOnlyList<int>> provider)
    {
        _getSortedPhotoKeys = provider;
    }

    public void ShowHideThumbnailBasedOnSettings()
    {
        RunOnUiThread(() =>
        {
            if (AppConfig.Settings.ShowThumbnails)
            {
                _d2dCanvasThumbNail.Visibility = Visibility.Visible;
                _canDrawThumbnails = true;
                CreateThumbnailRibbonOffScreen();
            }
            else
            {
                _d2dCanvasThumbNail.Visibility = Visibility.Collapsed;
                _thumbnailOffscreen?.Dispose();
                _thumbnailOffscreen = null;
            }
        });
    }

    public void RefreshThumbnail()
    {
        RunOnUiThread(() =>
        {
            _thumbNailSelectionColor = ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor);
            _thumbnailBoxSize = AppConfig.Settings.ThumbnailSize;
            if (AppConfig.Settings.ShowThumbnails)
                CreateThumbnailRibbonOffScreen();
        });
    }

    /// <summary>
    /// Called when an external thumbnail has been loaded.
    /// This method throttles redraw requests to prevent overwhelming the UI thread.
    /// </summary>
    public void RedrawThumbNailsIfNeeded(int updatedKey)
    {
        // Capture the snapshot once — this method is called from a ThreadPool continuation and the
        // list may be swapped by DeleteCurrentPhoto on the UI thread at any moment.
        var keys = _getSortedPhotoKeys?.Invoke();
        if (keys == null || keys.Count == 0) return;

        // Find the POSITION of the current and updated keys.
        int currentPosition = _photoSessionState.CurrentPhotoListPosition;
        int updatedPosition = keys.BinarySearch(updatedKey);

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
        var keys = _getSortedPhotoKeys?.Invoke();
        if (keys == null || keys.Count <= 1) return;

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
        if (newPosition >= 0 && newPosition < keys.Count)
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
        if (!_d2dCanvasThumbNail.DispatcherQueue.HasThreadAccess)
        {
            _d2dCanvasThumbNail.DispatcherQueue.TryEnqueue(CreateThumbnailRibbonOffScreen);
            return;
        }

        _throttledRedrawTimer.Stop();


        // Capture snapshot once for the entire draw pass — always called on the UI thread so no
        // race with DeleteCurrentPhoto, but a single capture avoids repeated volatile reads.
        var keys = _getSortedPhotoKeys?.Invoke();
        if (keys == null || keys.Count < 1 || !_canDrawThumbnails || !AppConfig.Settings.ShowThumbnails)
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
                if (thumbnailPosition < 0 || thumbnailPosition >= keys.Count) continue;

                var key = keys[thumbnailPosition];
                _cachedPreviews.TryGetValue(key, out var photo);

                // Lazy-create the GPU bitmap on first draw. Pixels were rendered on the main canvas
                // device during preview load; CreateFromBytes transfers them to the thumbnail device.
                if (photo?.Thumbnail != null && photo.Thumbnail.Bitmap == null)
                {
                    try
                    {
                        int s = Constants.ThumbnailPixelBufferSize;
                        photo.Thumbnail.Bitmap = CanvasBitmap.CreateFromBytes(_d2dCanvasThumbNail,
                            photo.Thumbnail.Pixels, s, s, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    }
                    catch { }
                }

                var bitmapToDraw = photo?.Thumbnail?.Bitmap ?? GetOrCreateLoadingIndicatorBitmap();
                if (bitmapToDraw == null) continue;

                var sourceRect = new Rect(0, 0, bitmapToDraw.SizeInPixels.Width, bitmapToDraw.SizeInPixels.Height);
                var destX = startX + i * _thumbnailBoxSize;
                var destRect = new Rect(
                    destX + Constants.ThumbnailPadding,
                    startY + Constants.ThumbnailPadding,
                    _thumbnailBoxSize - (Constants.ThumbnailPadding * 2),
                    _thumbnailBoxSize - (Constants.ThumbnailPadding * 2));

                using (var clipGeometry = CanvasGeometry.CreateRoundedRectangle(dsThumbNail, destRect, Constants.ThumbnailCornerRadius, Constants.ThumbnailCornerRadius))
                using (dsThumbNail.CreateLayer(1.0f, clipGeometry))
                {
                    // Rotation is baked into the bitmap at creation time — plain DrawImage suffices.
                    dsThumbNail.DrawImage(bitmapToDraw, destRect, sourceRect, 1f, CanvasImageInterpolation.HighQualityCubic);
                }

                if (key == _photoSessionState.CurrentPhotoKey)
                    dsThumbNail.DrawRoundedRectangle(destRect, Constants.ThumbnailCornerRadius, Constants.ThumbnailCornerRadius, _thumbNailSelectionColor, Constants.ThumbnailSelectionBorderThickness);
            }
        }

        RequestInvalidate();
    }

    private CanvasBitmap GetOrCreateLoadingIndicatorBitmap()
    {
        if (_loadingIndicatorBitmap != null) return _loadingIndicatorBitmap;
        var src = Photo.GetLoadingIndicator().Bitmap;
        if (src == null) return null;
        var pixels = src.GetPixelBytes();
        _loadingIndicatorBitmap = CanvasBitmap.CreateFromBytes(_d2dCanvasThumbNail,
            pixels, (int)src.SizeInPixels.Width, (int)src.SizeInPixels.Height, src.Format);
        return _loadingIndicatorBitmap;
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
        _loadingIndicatorBitmap?.Dispose();
        _loadingIndicatorBitmap = null;
        ThumbnailClicked = null;
        _cachedPreviews = null;
        _getSortedPhotoKeys = null;
    }
}