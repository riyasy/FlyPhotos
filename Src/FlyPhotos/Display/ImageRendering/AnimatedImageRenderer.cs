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

    // Set to true by UpdateFrameAsync when a new frame has been decoded and is ready to display.
    // Read by CanvasController.D2dCanvas_Update to decide whether to keep the control un-paused.
    private volatile bool _frameReady;

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

    /// <summary>
    /// Called by CanvasController on the Win2D background thread each Update tick.
    /// Tries to advance the animator by one frame (non-blocking; skips if previous update still running).
    /// Returns true if a new frame is ready and the canvas should stay un-paused for another Draw.
    /// </summary>
    public bool OnUpdate()
    {
        if (_isDisposed) return false;

        // Non-blocking: skip this tick if the previous async update hasn't finished yet.
        if (!_animatorLock.Wait(0)) return _frameReady;

        // Lock was acquired; fire-and-forget the async decode.
        // The lock is released inside UpdateFrameAsync when it completes.
        _ = UpdateFrameAsync();

        return _frameReady;
    }

    private async Task UpdateFrameAsync()
    {
        try
        {
            if (_isDisposed)
            {
                _animatorLock.Release();
                return;
            }

            _frameReady = false;
            await _animator.UpdateAsync(_stopwatch.Elapsed);

            if (!_isDisposed)
            {
                _frameReady = true;
                // Wake the canvas if it paused while this async decode was in flight.
                _requestInvalidate();
            }
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
        // Drain any in-flight UpdateFrameAsync before disposing the animator.
        _animatorLock.Wait();
        _animatorLock.Release();
        _stopwatch.Stop();
        _animator?.Dispose();
    }
}