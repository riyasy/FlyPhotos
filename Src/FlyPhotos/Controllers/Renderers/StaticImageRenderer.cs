using FlyPhotos.AppSettings;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using FlyPhotos.Data;

namespace FlyPhotos.Controllers.Renderers
{
	// TODO - 
	// 1. Now antialiasing is disabled when drawing checkerboard. Find another way
    internal partial class StaticImageRenderer : IRenderer
    {
        private readonly CanvasBitmap _sourceBitmap;
        private readonly Action _invalidateCanvas;
        private readonly bool _createOffScreen;
        private readonly DispatcherTimer _offscreenDrawTimer;
        private CanvasRenderTarget _offscreen;
        private readonly CanvasViewState _canvasViewState;
        private readonly CanvasImageBrush _checkeredBrush;
        private readonly bool _supportsTransparency;
        private readonly CanvasControl _canvas;

        public Rect SourceBounds => _sourceBitmap.Bounds;

        public StaticImageRenderer(CanvasControl canvas, CanvasViewState canvasViewState, CanvasBitmap sourceBitmap,
            CanvasImageBrush checkeredBrush, bool supportsTransparency, Action invalidateCanvas, bool createOffScreen = true)
        {
            _sourceBitmap = sourceBitmap;
            _supportsTransparency = supportsTransparency;
            _invalidateCanvas = invalidateCanvas;
            _createOffScreen = createOffScreen;
            _canvasViewState = canvasViewState;
            _checkeredBrush = checkeredBrush;
            _canvas = canvas;

            _offscreenDrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(410) };
            _offscreenDrawTimer.Tick += OffScreenDrawTimer_Tick;
        }


        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality)
        {
            session.Units = CanvasUnits.Pixels;
            var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
            session.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
            if (drawCheckeredBackground)
            {
                var brushScale = _canvasViewState.MatInv.M11;
                _checkeredBrush.Transform = Matrix3x2.CreateScale(brushScale);
                session.FillRectangle(viewState.ImageRect, _checkeredBrush);
            }


            if (_offscreen != null)
                session.DrawImage(_offscreen, viewState.ImageRect, _offscreen.Bounds, 1f, quality);
            else
                session.DrawImage(_sourceBitmap, viewState.ImageRect, _sourceBitmap.Bounds, 1f, quality);

            // DrawDebugInfo(session, viewState);
        }

        private void DrawDebugInfo(CanvasDrawingSession session, CanvasViewState viewState)
        {
            // Save the current transform
            var originalTransform = session.Transform;

            // Reset transform to identity to draw the debug text at a fixed position
            session.Transform = Matrix3x2.Identity;

            // Build the debug text including canvas properties
            string debugText = $"--Canvas Properties--\n" +
                               $"Width = {_canvas.ActualWidth:0.00}, Height = {_canvas.ActualHeight:0.00}\n" +
                               $"Dpi = {_canvas.Dpi}, DpiScale = {_canvas.DpiScale:0.00}\n\n" +
                               $"--Display Source Properties--\n" +
                               $"Width = {SourceBounds.Width:0.00}, Height = {SourceBounds.Height:0.00}, Dpi = {_sourceBitmap.Dpi}\n\n" +
                               viewState.GetAsString();

            var textFormat = new CanvasTextFormat()
            {
                FontSize = 14,
                WordWrapping = CanvasWordWrapping.NoWrap
            };

            var textBrush = new CanvasSolidColorBrush(session, Colors.White);

            // Measure and layout the text
            using (var layout = new CanvasTextLayout(session, debugText, textFormat, 0.0f, 0.0f))
            {
                var textPadding = 4f;
                var backgroundRect = new Rect(
                    10 - textPadding,
                    10 - textPadding,
                    layout.DrawBounds.Width + 2 * textPadding,
                    layout.DrawBounds.Height + 2 * textPadding);

                session.FillRectangle(backgroundRect, Color.FromArgb(128, 0, 0, 0)); // semi-transparent black
                session.DrawTextLayout(layout, 10, 10, textBrush);
            }

            // Restore the original transform
            session.Transform = originalTransform;
        }

        public void RestartOffScreenDrawTimer()
        {
            if (!_createOffScreen) return;
            _offscreenDrawTimer.Stop();
            _offscreenDrawTimer.Start();
        }

        public void TryRedrawOffScreen()
        {
            if (!_createOffScreen) return;
            CreateOffscreen(true);
            _invalidateCanvas();
        }

        private void OffScreenDrawTimer_Tick(object sender, object e)
        {
            _offscreenDrawTimer.Stop();
            CreateOffscreen(false);
            _invalidateCanvas();
        }

        private void CreateOffscreen(bool forceCreate)
        {
            if (_canvasViewState.ImageRect.Width < _canvas.GetSize().Width * 1.5 &&
                _canvasViewState.ImageRect.Height < _canvas.GetSize().Height * 1.5)
            {
                return;
            }

            var scaledImageWidth = _canvasViewState.ImageRect.Width * _canvasViewState.Scale;
            var scaledImageHeight = _canvasViewState.ImageRect.Height * _canvasViewState.Scale;

            var offScreenDimensionsChanged =
                _offscreen != null &&
                (!(_offscreen.SizeInPixels.Width == (int)scaledImageWidth &&
                   _offscreen.SizeInPixels.Height == (int)scaledImageHeight));

            if (forceCreate || offScreenDimensionsChanged)
            {
                DestroyOffscreen();
            }

            // offscreen already exists and is valid
            if (_offscreen != null) 
                return;
            // invalid size
            if (scaledImageWidth <= 0 || scaledImageHeight <= 0)  
                return;
            // too zoomed-in to bother with offscreen
            if (scaledImageWidth > _canvas.GetSize().Width * 1.5 &&
                scaledImageHeight > _canvas.GetSize().Height * 1.5)
                return;

            var drawingQuality = AppConfig.Settings.HighQualityInterpolation
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.NearestNeighbor;

            var tempOffScreen = new CanvasRenderTarget(_canvas, (float)scaledImageWidth, (float)scaledImageHeight, 96);
            using (var ds = tempOffScreen.CreateDrawingSession())
            {
                var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
                ds.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
                if (drawCheckeredBackground)
                {
                    _checkeredBrush.Transform = Matrix3x2.Identity;
                    ds.FillRectangle(new Rect(0, 0, scaledImageWidth, scaledImageHeight), _checkeredBrush);
                }
                else
                {
                    ds.Clear(Colors.Transparent);
                }

                ds.DrawImage(_sourceBitmap, new Rect(0, 0, scaledImageWidth, scaledImageHeight),
                    _sourceBitmap.Bounds, 1, drawingQuality);
            }
            _offscreen = tempOffScreen;
        }

        private void DestroyOffscreen()
        {
            _offscreen?.Dispose();
            _offscreen = null;
        }

        public void Dispose()
        {
            _offscreenDrawTimer.Tick -= OffScreenDrawTimer_Tick;
            _offscreenDrawTimer.Stop();
            DestroyOffscreen();
        }
    }
}