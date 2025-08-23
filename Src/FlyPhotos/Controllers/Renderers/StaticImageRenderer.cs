using FlyPhotos.AppSettings;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Numerics;
using Windows.Foundation;

namespace FlyPhotos.Controllers.Renderers
{
	// TODO - 
	// 1. Now antialiasing is disabled when drawing checkerboard. Find another way
    internal class StaticImageRenderer : IRenderer
    {
        private readonly CanvasBitmap _sourceBitmap;
        private readonly Action _invalidateCanvas;
        private readonly DispatcherTimer _offscreenDrawTimer;
        private CanvasRenderTarget _offscreen;
        private readonly CanvasViewState _canvasViewState;
        private readonly CanvasImageBrush _checkeredBrush;
        private readonly bool _supportsTransparency;
        private readonly CanvasControl _canvas;

        public Rect SourceBounds => _sourceBitmap.Bounds;

        public StaticImageRenderer(CanvasControl canvas, CanvasViewState canvasViewState, CanvasBitmap sourceBitmap,
            CanvasImageBrush checkeredBrush, bool supportsTransparency, Action invalidateCanvas)
        {
            _sourceBitmap = sourceBitmap;
            _supportsTransparency = supportsTransparency;
            _invalidateCanvas = invalidateCanvas;
            _canvasViewState = canvasViewState;
            _checkeredBrush = checkeredBrush;
            _canvas = canvas;

            _offscreenDrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(410) };
            _offscreenDrawTimer.Tick += OffScreenDrawTimer_Tick;
        }


        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality)
        {
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
        }

        public void RestartOffScreenDrawTimer()
        {
            _offscreenDrawTimer.Stop();
            _offscreenDrawTimer.Start();
        }

        public void TryRedrawOffScreen()
        {
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
            var imageWidth = _canvasViewState.ImageRect.Width * _canvasViewState.Scale;
            var imageHeight = _canvasViewState.ImageRect.Height * _canvasViewState.Scale;

            var offScreenDimensionsChanged =
                _offscreen != null &&
                (_offscreen.SizeInPixels.Width != (int)imageWidth ||
                 _offscreen.SizeInPixels.Height != (int)imageHeight);

            if (forceCreate || offScreenDimensionsChanged)
            {
                DestroyOffscreen();
            }

            var drawingQuality = AppConfig.Settings.HighQualityInterpolation
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.NearestNeighbor;

            if (_offscreen == null && imageWidth > 0 && imageHeight > 0 && imageWidth < _canvas.ActualWidth * 1.5)
            {
                var tempOffScreen = new CanvasRenderTarget(_canvas, (float)imageWidth, (float)imageHeight, 96);
                using (var ds = tempOffScreen.CreateDrawingSession())
                {
                    var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
                    ds.Antialiasing = drawCheckeredBackground
                        ? CanvasAntialiasing.Aliased
                        : CanvasAntialiasing.Antialiased;
                    if (drawCheckeredBackground)
                    {
                        _checkeredBrush.Transform = Matrix3x2.Identity;
                        ds.FillRectangle(new Rect(0, 0, imageWidth, imageHeight), _checkeredBrush);
                    }
                    else
                    {
                        ds.Clear(Colors.Transparent);
                    }

                    ds.DrawImage(_sourceBitmap, new Rect(0, 0, imageWidth, imageHeight),
                        _sourceBitmap.Bounds, 1, drawingQuality);
                }
                _offscreen = tempOffScreen;
            }
        }

		/// TODO, decide and remove
        private void CreateOffscreen_OLD()
        {
            var imageWidth = _canvasViewState.ImageRect.Width * _canvasViewState.Scale;
            var imageHeight = _canvasViewState.ImageRect.Height * _canvasViewState.Scale;

            if (_offscreen != null &&
                (_offscreen.SizeInPixels.Width != (int)imageWidth ||
                 _offscreen.SizeInPixels.Height != (int)imageHeight))
            {
                DestroyOffscreen();
            }

            var drawingQuality = AppConfig.Settings.HighQualityInterpolation
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.NearestNeighbor;

            if (_offscreen == null && imageWidth < _canvas.ActualWidth * 1.5)
            {
                var tempOffScreen = new CanvasRenderTarget(_canvas, (float)imageWidth, (float)imageHeight);
                using var ds = tempOffScreen.CreateDrawingSession();
                ds.Clear(Colors.Transparent);
                ds.DrawImage(_sourceBitmap, new Rect(0, 0, imageWidth, imageHeight),
                    _sourceBitmap.Bounds, 1, drawingQuality);
                _offscreen = tempOffScreen;
            }

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