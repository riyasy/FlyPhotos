using FlyPhotos.AppSettings;
using FlyPhotos.Controllers.Animators;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FlyPhotos.Controllers.Renderers
{
    internal class AnimatedImageRenderer : IRenderer
    {
        private readonly CanvasControl _canvas;
        private readonly IAnimator _animator;
        private readonly Action _invalidateCanvas;
        private readonly Stopwatch _stopwatch = new();
        private readonly SemaphoreSlim _animatorLock;
        private readonly bool _supportsTransparency;
        private readonly CanvasImageBrush _checkeredBrush;

        public Rect SourceBounds => _animator.Surface.GetBounds(_canvas);

        public AnimatedImageRenderer(CanvasControl canvas, CanvasImageBrush checkeredBrush, IAnimator animator,
            SemaphoreSlim animatorLock, bool supportsTransparency, Action invalidateCanvas)
        {
            _canvas = canvas;
            _animator = animator;
            _invalidateCanvas = invalidateCanvas;
            _animatorLock = animatorLock;
            _supportsTransparency = supportsTransparency;
            _checkeredBrush = checkeredBrush;
            _stopwatch.Start();
        }

        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality)
        {
            if (_animator?.Surface == null) return;

            var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
            // Antialiasing can cause fine lines visible at edge of images when drawing checkerboard
            session.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
            if (drawCheckeredBackground)
                session.FillRectangle(viewState.ImageRect, _checkeredBrush);
            session.DrawImage(_animator.Surface, viewState.ImageRect, _animator.Surface.GetBounds(_canvas), 1.0f, quality);
            _ = RunAnimationLoop();
        }

        private async Task RunAnimationLoop()
        {
            if (!await _animatorLock.WaitAsync(0)) return;
            try
            {
                if (_animator == null) return;
                await _animator.UpdateAsync(_stopwatch.Elapsed);
                _invalidateCanvas();
            }
            catch
            {
                _stopwatch.Stop();
            }
            finally
            {
                _animatorLock.Release();
            }
        }

        public void RestartOffScreenDrawTimer()
        {
            // This concept does not apply to animated images.
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _animator?.Dispose();
        }
    }
}