using System;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas.Brushes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using FlyPhotos.AppSettings;
using NLog;

namespace FlyPhotos.Controllers.Renderers
{
    /// <summary>
    /// Renderer for multi-page images (e.g., multipage TIFF). It decodes pages on demand from the
    /// provided byte array and renders the currently selected page. Page index can be changed to
    /// navigate through pages.
    /// </summary>
    internal partial class MultiPageRenderer : IRenderer
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly CanvasControl _canvas;
        private readonly CanvasImageBrush _checkeredBrush;
        private readonly Action _invalidate;
        private CanvasBitmap _currentBitmap;
        private readonly byte[] _fileBytes;
        private int _currentPageIndex;
        private readonly bool _supportsTransparency;

        public MultiPageRenderer(CanvasControl canvas, CanvasViewState canvasViewState,  byte[] fileBytes, int initialPageIndex, CanvasImageBrush checkeredBrush, bool supportsTransparency, Action invalidate)
        {
            _canvas = canvas;
            _supportsTransparency = supportsTransparency;
            _checkeredBrush = checkeredBrush;
            _fileBytes = fileBytes;
            _currentPageIndex = initialPageIndex;
            _invalidate = invalidate;

            // Load initial page
            _ = LoadPageAsync(_currentPageIndex);
        }

        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality)
        {
            session.Units = CanvasUnits.Pixels;
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

        public void RestartOffScreenDrawTimer()
        {
            // Not applicable
        }

        public void TryRedrawOffScreen()
        {
            _invalidate();
        }

        public void Dispose()
        {
            _currentBitmap?.Dispose();
            _currentBitmap = null;
        }

        public async Task<bool> LoadPageAsync(int pageIndex)
        {
            try
            {
                using var ms = new InMemoryRandomAccessStream();
                using (var outStream = ms.GetOutputStreamAt(0))
                using (var writer = new DataWriter(outStream))
                {
                    writer.WriteBytes(_fileBytes);
                    await writer.StoreAsync();
                    await outStream.FlushAsync();
                }
                ms.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(ms);
                if (pageIndex < 0 || pageIndex >= decoder.FrameCount) return false;

                // Decode the requested frame to a SoftwareBitmap then to CanvasBitmap.
                var frame = await decoder.GetFrameAsync((uint)pageIndex);
                var softwareBitmap = await frame.GetSoftwareBitmapAsync();

                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(softwareBitmap);
                await encoder.FlushAsync();
                stream.Seek(0);

                // Dispose old
                _currentBitmap?.Dispose();
                _currentBitmap = await CanvasBitmap.LoadAsync(_canvas, stream);

                _currentPageIndex = pageIndex;
                _invalidate();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MultiPageRenderer - LoadPageAsync failed");
                return false;
            }
        }

        public int CurrentPageIndex => _currentPageIndex;
    }
}
