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

namespace FlyPhotos.Display.ImageRendering;

internal partial class AnimatedImageRenderer : IRenderer
{
    private readonly CanvasAnimatedControl _canvas;
    private readonly IAnimator _animator;
    private readonly Action _requestInvalidate;
    private readonly Stopwatch _stopwatch = new();
    private readonly SemaphoreSlim _animatorLock = new(1, 1);
    private readonly bool _supportsTransparency;
    private CanvasImageBrush _checkeredBrush;
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }
    // Read on the W2D thread (OnUpdate) and the ThreadPool decode continuation; written in Dispose.
    private volatile bool _isDisposed;

    public Rect SourceBounds => _animator.Surface.GetBounds(_canvas);

    public AnimatedImageRenderer(CanvasAnimatedControl canvas, IAnimator animator, bool supportsTransparency,
        Action requestInvalidate)
    {
        _canvas = canvas;
        _animator = animator;
        _supportsTransparency = supportsTransparency;
        _requestInvalidate = requestInvalidate;
        _stopwatch.Start();
        // No CompositionTarget.Rendering subscription — the Update loop in CanvasController drives us.
    }

    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
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

    /// <summary>
    /// Called by CanvasController on the Win2D background thread each Update tick.
    /// Fires an async frame-advance if the previous one has completed (non-blocking).
    /// Always returns true — animated images keep the canvas running continuously.
    /// </summary>
    public bool OnUpdate()
    {
        if (_isDisposed) 
            return false;
        if (_animatorLock.Wait(0))
            _ = UpdateFrameAsync();
        return true;
    }

    private async Task UpdateFrameAsync()
    {
        try
        {
            if (_isDisposed) { _animatorLock.Release(); return; }

            await _animator.UpdateAsync(_stopwatch.Elapsed);

            if (!_isDisposed)
                _requestInvalidate(); // defensive: wakes the canvas if something externally paused it
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

    public void RestartOffScreenDrawTimer() { }
    public void CancelOffScreenTimer() { }
    public void TryRedrawOffScreen(bool forceCreate) { }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        // Drain any in-flight UpdateFrameAsync before disposing the animator.
        _animatorLock.Wait();
        _animatorLock.Release();
        _stopwatch.Stop();
        _animator?.Dispose();
    }
}