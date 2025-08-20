using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.AppSettings;
using FlyPhotos.Controllers.Animators;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;

namespace FlyPhotos.Controllers.Renderers
{
    internal class AnimatedImageRenderer : IRenderer
    {
        private readonly IAnimator _animator;
        private readonly Action _invalidateCanvas;
        private readonly Stopwatch _stopwatch = new();
        private readonly SemaphoreSlim _animatorLock;

        public bool SupportsTransparency => true; // Animated formats typically support transparency
        public Rect SourceBounds => _animator.Surface.GetBounds(Photo.D2dCanvas);

        public AnimatedImageRenderer(IAnimator animator, Action invalidateCanvas, SemaphoreSlim animatorLock)
        {
            _animator = animator;
            _invalidateCanvas = invalidateCanvas;
            _animatorLock = animatorLock;
            _stopwatch.Start();
        }



        public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, CanvasImageBrush checkeredBrush)
        {
            if (_animator?.Surface == null) return;

            if (AppConfig.Settings.CheckeredBackground && SupportsTransparency)
            {
                session.FillRectangle(viewState.ImageRect, checkeredBrush);
            }

            session.DrawImage(_animator.Surface, viewState.ImageRect, _animator.Surface.GetBounds(Photo.D2dCanvas), 1.0f, quality);

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