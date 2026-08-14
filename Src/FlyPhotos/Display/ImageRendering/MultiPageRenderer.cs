using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Renderer for multi-page images (e.g., multipage TIFF, multi-frame ICO). It decodes pages on demand
/// from the provided byte array and renders the currently selected page. Page index can be changed to
/// navigate through pages. A <c>pageOrder</c> map lets a caller present the frames in an order other
/// than the file's own (ICO shows them largest first).
/// </summary>
internal partial class MultiPageRenderer : IRenderer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasAnimatedControl _canvas;
    private CanvasImageBrush _checkeredBrush;
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }
    private readonly Action _invalidate;
    private CanvasBitmap _currentBitmap;
    // Guards the _currentBitmap swap (LoadPageAsync completes on a ThreadPool continuation) against
    // the Draw read on the W2D thread.
    private readonly Lock _bitmapLock = new();
    private byte[] _fileBytes;
    private int _currentPageIndex;
    private readonly bool _supportsTransparency;
    private volatile bool _isDisposed;
    private int _latestPageLoadId;

    // The stream and decoder are created once and reused for all page loads.
    private readonly InMemoryRandomAccessStream _fileStream = new();
    private readonly Task<BitmapDecoder> _decoderTask;

    // Display page -> decoder frame index. Identity for TIFF, largest-frame-first for ICO.
    private readonly int[] _pageOrder;

    public MultiPageRenderer(CanvasAnimatedControl canvas, byte[] fileBytes, int initialPageIndex,
        bool supportsTransparency, Action invalidate, int[] pageOrder)
    {
        _canvas = canvas;
        _supportsTransparency = supportsTransparency;
        _fileBytes = fileBytes;
        _currentPageIndex = initialPageIndex;
        _invalidate = invalidate;
        _pageOrder = pageOrder;

        _decoderTask = InitDecoderAsync();
        _ = LoadPageAsync(_currentPageIndex);
    }

    private async Task<BitmapDecoder> InitDecoderAsync()
    {
        using (var outStream = _fileStream.GetOutputStreamAt(0))
        using (var writer = new DataWriter(outStream))
        {
            writer.WriteBytes(_fileBytes);
            await writer.StoreAsync();
            await outStream.FlushAsync();
        }
        _fileBytes = null; // bytes are now in _fileStream; release the array
        _fileStream.Seek(0);
        return await BitmapDecoder.CreateAsync(_fileStream);
    }

    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        session.Units = CanvasUnits.Pixels;

        // Hold the lock across DrawImage so a ThreadPool page-load swap cannot dispose the bitmap
        // mid-draw. Contention is negligible — page loads are rare and the body is cheap.
        lock (_bitmapLock)
        {
            if (_currentBitmap == null) return;

            var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
            session.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
            if (drawCheckeredBackground)
            {
                var brushScale = viewState.MatInv.M11;
                _checkeredBrush.Transform = System.Numerics.Matrix3x2.CreateScale(brushScale);
                session.FillRectangle(viewState.ImageRect, _checkeredBrush);
            }

            session.DrawImage(_currentBitmap, viewState.ImageRect, _currentBitmap.Bounds, 1f, quality);
        }
    }

    public void HandleScalingMethodChange() => _invalidate();

    public void Dispose()
    {
        _isDisposed = true;
        lock (_bitmapLock)
        {
            _currentBitmap?.Dispose();
            _currentBitmap = null;
        }
        _fileStream.Dispose();
    }

    /// <summary>
    /// Loads a page, returning its pixel size so the caller can re-fit the view when pages differ in
    /// size (ICO frames do). Returns null when there is nothing to fit — the load failed, or a newer
    /// load superseded it. The caller is responsible for passing an in-range page.
    /// </summary>
    public async Task<Size> LoadPageAsync(int pageIndex)
    {
        var operationId = Interlocked.Increment(ref _latestPageLoadId);
        try
        {
            var decoder = await _decoderTask;

            var frame = await decoder.GetFrameAsync((uint)_pageOrder[pageIndex]);
            var pixelData = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var pixelBytes = pixelData.DetachPixelData();

            // All the heavy decoding is done. Before touching shared state, check if this
            // operation is still the latest, and that the renderer hasn't been disposed.
            if (_isDisposed || operationId != _latestPageLoadId) return null;

            // CreateFromBytes is synchronous and uploads directly to the GPU — no PNG round-trip.
            // 96f DPI matches the original LoadAsync-from-PNG behaviour (PNG default DPI = 96).
            var newBitmap = CanvasBitmap.CreateFromBytes(
                _canvas, pixelBytes, (int)frame.PixelWidth, (int)frame.PixelHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f);

            // Re-check and swap under the lock — another page load or disposal may have raced in
            // during the async pixel extraction, and Draw may be reading _currentBitmap on the W2D thread.
            lock (_bitmapLock)
            {
                if (_isDisposed || operationId != _latestPageLoadId)
                {
                    newBitmap.Dispose();
                    return null;
                }

                _currentBitmap?.Dispose();
                _currentBitmap = newBitmap;
                _currentPageIndex = pageIndex;
            }
            _invalidate();
            return new Size(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MultiPageRenderer - LoadPageAsync failed");
            return null;
        }
    }

    public int CurrentPageIndex => _currentPageIndex;

    public int PageCount => _pageOrder.Length;
}
