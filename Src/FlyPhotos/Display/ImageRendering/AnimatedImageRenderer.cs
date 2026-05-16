using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Display.Animators;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlyPhotos.Display.ImageRendering;

internal partial class AnimatedImageRenderer : IRenderer
{
    private readonly CanvasControl _canvas;
    private readonly IAnimator _animator;
    private readonly Action _invalidateCanvas;
    private readonly Stopwatch _stopwatch = new();
    private readonly SemaphoreSlim _animatorLock = new(1, 1); // owned entirely by this class
    private readonly bool _supportsTransparency;
    private readonly CanvasImageBrush _checkeredBrush;
    private bool _isDisposed;

    public Rect SourceBounds => _animator.Surface.GetBounds(_canvas);

    public AnimatedImageRenderer(CanvasControl canvas, CanvasImageBrush checkeredBrush, IAnimator animator,
        bool supportsTransparency, Action invalidateCanvas)  // removed SemaphoreSlim parameter
    {
        _canvas = canvas;
        _animator = animator;
        _invalidateCanvas = invalidateCanvas;
        _supportsTransparency = supportsTransparency;
        _checkeredBrush = checkeredBrush;
        _stopwatch.Start();

        CompositionTarget.Rendering += OnCompositionTargetRendering;
    }

    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality)
    {
        session.Units = CanvasUnits.Pixels;
        if (_animator?.Surface == null) return;

        var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
        session.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (drawCheckeredBackground)
        {
            var brushScale = viewState.MatInv.M11;
            _checkeredBrush.Transform = Matrix3x2.CreateScale(brushScale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }

        session.DrawImage(_animator.Surface, viewState.ImageRect, _animator.Surface.GetBounds(_canvas), 1.0f, quality);
    }

    private async void OnCompositionTargetRendering(object sender, object e)
    {
        if (_isDisposed) return;

        // Skip frame if previous UpdateAsync is still running, rather than queuing up.
        if (!await _animatorLock.WaitAsync(0)) return;

        try
        {
            if (_isDisposed) return; // re-check: Dispose() may have run during the WaitAsync(0) yield

            await _animator.UpdateAsync(_stopwatch.Elapsed);

            if (!_isDisposed) // don't invalidate a canvas we no longer own
                _invalidateCanvas();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Animation update failed: {ex.Message}");
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

    public void TryRedrawOffScreen(bool forceCreate)
    {
        // This concept does not apply to animated images.
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _animatorLock.Wait();
        _animatorLock.Release();
        _stopwatch.Stop();
        _animator?.Dispose();
    }
}