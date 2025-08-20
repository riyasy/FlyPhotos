using System;
using Windows.Foundation;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Controllers.Renderers
{
    internal class StaticImageRenderer : IRenderer
    {
        private readonly CanvasBitmap _sourceBitmap;
        private readonly Action _invalidateCanvas;
        private readonly DispatcherTimer _offscreenDrawTimer;
        private CanvasRenderTarget _offscreen;
        private readonly CanvasViewState _canvasViewState;

        public bool SupportsTransparency { get; }
        public Rect SourceBounds => _sourceBitmap.Bounds;

        public StaticImageRenderer(ICanvasResourceCreator resourceCreator, CanvasBitmap bitmap, bool supportsTransparency, Action invalidateCanvas, CanvasViewState canvasViewState)
        {
            _sourceBitmap = bitmap;
            SupportsTransparency = supportsTransparency;
            _invalidateCanvas = invalidateCanvas;
            _canvasViewState = canvasViewState;

            _offscreenDrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(410) };
            _offscreenDrawTimer.Tick += OnTimerTick;
        }

        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, CanvasImageBrush checkeredBrush)
        {
            if (AppConfig.Settings.CheckeredBackground && SupportsTransparency)
            {
                session.FillRectangle(viewState.ImageRect, checkeredBrush);
            }

            if (_offscreen != null)
            {
                session.DrawImage(_offscreen, viewState.ImageRect, _offscreen.Bounds, 1f, quality);
            }
            else
            {
                session.DrawImage(_sourceBitmap, viewState.ImageRect, _sourceBitmap.Bounds, 1f, quality);
            }
        }

        public void RestartOffScreenDrawTimer()
        {
            // Discard the old buffer; the timer will create a new one.
            DestroyOffscreen();
            _offscreenDrawTimer.Stop();
            _offscreenDrawTimer.Start();
        }

        private void OnTimerTick(object sender, object e)
        {
            _offscreenDrawTimer.Stop();
            CreateOffscreen();
            _invalidateCanvas();
        }

        private void CreateOffscreen()
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

            if (_offscreen == null && imageWidth < Photo.D2dCanvas.ActualWidth * 1.5)
            {
                var tempOffScreen = new CanvasRenderTarget(Photo.D2dCanvas, (float)imageWidth, (float)imageHeight);
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
            _offscreenDrawTimer.Tick -= OnTimerTick;
            _offscreenDrawTimer.Stop();
            DestroyOffscreen();
        }
    }
}