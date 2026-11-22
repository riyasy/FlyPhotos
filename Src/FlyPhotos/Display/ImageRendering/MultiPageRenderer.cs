using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Display.ImageReading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// A multi-page TIFF or multi-frame ICO. Page 1 is the item's own bitmap, shown at once; the other pages are decoded
/// on demand from the file's bytes. <c>pageOrder</c> maps pages to decoder frames (ICO shows the largest first).
/// </summary>
internal partial class MultiPageRenderer : IRenderer, IMultiPage
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasAnimatedControl _canvas;
    private readonly Action _invalidate;
    private readonly CanvasBitmap _firstPage;   // the item's; the canvas holds its lease while this renderer is up
    private CanvasBitmap _currentBitmap;        // _firstPage, or a decoded page this renderer owns
    // Guards the swap of _currentBitmap (page loads finish on the thread pool) against Draw on the W2D thread.
    private readonly Lock _bitmapLock = new();
    private volatile bool _isDisposed;
    private int _currentPageIndex;
    private int _latestPageLoadId;
    private readonly int[] _pageOrder;

    // One decoder for all pages, opened at once in the background.
    private readonly InMemoryRandomAccessStream _fileStream = new();
    private byte[] _fileBytes;
    private readonly Task<BitmapDecoder> _decoderTask;

    public MultiPageRenderer(CanvasAnimatedControl canvas, CanvasBitmap firstPage, byte[] fileBytes, Action invalidate,
        int[] pageOrder)
    {
        _canvas = canvas;
        _firstPage = _currentBitmap = firstPage;
        _fileBytes = fileBytes;
        _invalidate = invalidate;
        _pageOrder = pageOrder;
        _decoderTask = OpenDecoderAsync();
    }

    public int CurrentPageIndex => _currentPageIndex;
    public int PageCount => _pageOrder.Length;

    public void Draw(CanvasDrawingSession session, in ViewFrame frame, CanvasImageInterpolation quality, bool isAnimating)
    {
        // Held across DrawImage, so a page swap can't dispose the bitmap mid-draw. Page loads are rare.
        lock (_bitmapLock)
        {
            if (_currentBitmap == null) return;
            session.DrawImage(_currentBitmap, frame.ImageRect, _currentBitmap.Bounds, 1f, quality);
        }
    }

    public void HandleScalingMethodChange() => _invalidate();

    /// <summary>
    /// Shows a page and returns its pixel size, so the canvas can re-fit when pages differ in size (ICO). Null when
    /// it failed or a newer load superseded it. The page must be in range.
    /// </summary>
    public async Task<Size> LoadPageAsync(int pageIndex)
    {
        var loadId = Interlocked.Increment(ref _latestPageLoadId);
        try
        {
            if (pageIndex == 0) return Show(_firstPage, 0, loadId);

            var decoder = await _decoderTask;
            var frame = await decoder.GetFrameAsync((uint)_pageOrder[pageIndex]);
            using var softwareBitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            if (_isDisposed || loadId != _latestPageLoadId) return null;

            // Uploaded at 96 DPI, so its size in DIPs is its pixel size.
            return Show(softwareBitmap.ToCanvasBitmap(_canvas), pageIndex, loadId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MultiPageRenderer - LoadPageAsync failed");
            return null;
        }
    }

    // Swaps in a page's bitmap unless superseded or disposed meanwhile (checked under the lock, against Draw).
    private Size Show(CanvasBitmap bitmap, int pageIndex, int loadId)
    {
        lock (_bitmapLock)
        {
            if (_isDisposed || loadId != _latestPageLoadId)
            {
                DisposeIfOwned(bitmap);
                return null;
            }
            DisposeIfOwned(_currentBitmap);
            _currentBitmap = bitmap;
            _currentPageIndex = pageIndex;
        }
        _invalidate();
        var size = bitmap.SizeInPixels;
        return new Size(size.Width, size.Height);
    }

    private async Task<BitmapDecoder> OpenDecoderAsync()
    {
        using (var outStream = _fileStream.GetOutputStreamAt(0))
        using (var writer = new DataWriter(outStream))
        {
            writer.WriteBytes(_fileBytes);
            await writer.StoreAsync();
            await outStream.FlushAsync();
        }
        _fileBytes = null; // in the stream now
        _fileStream.Seek(0);
        return await BitmapDecoder.CreateAsync(_fileStream);
    }

    public void Dispose()
    {
        _isDisposed = true;
        lock (_bitmapLock)
        {
            DisposeIfOwned(_currentBitmap);
            _currentBitmap = null;
        }
        _fileStream.Dispose();
    }

    // Page 1 is the item's bitmap: the item frees it.
    private void DisposeIfOwned(CanvasBitmap bitmap)
    {
        if (bitmap != _firstPage) bitmap?.Dispose();
    }
}
