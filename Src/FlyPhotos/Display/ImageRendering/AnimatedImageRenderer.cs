using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Display.Animators;
using FlyPhotos.Display.Canvas;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Draws an animator's surface and advances it. Between frames the canvas sleeps: a frame due soon is waited for by
/// keeping the render loop running (a timer is too coarse), one further off by a single timed wake.
/// </summary>
internal partial class AnimatedImageRenderer : IRenderer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const double KeepRunningWithinMs = 20; // a little over a 60 Hz frame

    private readonly CanvasAnimatedControl _canvas;
    private readonly IAnimator _animator;
    private readonly Action _requestInvalidate;
    private readonly DelayedWake _wake;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly SemaphoreSlim _animatorLock = new(1, 1); // one frame update at a time
    private volatile bool _isDisposed;
    // When the next frame is due, as _stopwatch.Elapsed ticks; long.MaxValue for never. Written by the update task.
    private long _nextFrameTicks;

    public AnimatedImageRenderer(CanvasAnimatedControl canvas, IAnimator animator, Action requestInvalidate)
    {
        _canvas = canvas;
        _animator = animator;
        _requestInvalidate = requestInvalidate;
        _wake = new DelayedWake(requestInvalidate);
    }

    public void Draw(CanvasDrawingSession session, in ViewFrame frame, CanvasImageInterpolation quality, bool isAnimating)
    {
        if (_animator?.Surface is not { } surface) return;
        session.DrawImage(surface, frame.ImageRect, surface.GetBounds(_canvas), 1.0f, quality);
    }

    /// <summary>W2D thread, every Update. Returns whether the render loop should keep running for this animation.</summary>
    public bool OnUpdate()
    {
        if (_isDisposed) return false;
        var next = Volatile.Read(ref _nextFrameTicks);
        if (next == long.MaxValue) return false; // a still frame, or the animation failed

        var now = _stopwatch.Elapsed;
        var waitMs = (TimeSpan.FromTicks(next) - now).TotalMilliseconds;
        if (waitMs > KeepRunningWithinMs)
        {
            _wake.After((int)(waitMs - KeepRunningWithinMs));
            return false;
        }
        if (waitMs <= 0 && _animatorLock.Wait(0))
            _ = UpdateFrameAsync(now);
        return true;
    }

    private async Task UpdateFrameAsync(TimeSpan now)
    {
        try
        {
            if (_isDisposed) return; // finally releases the lock
            await _animator.UpdateAsync(now);
            var wait = _animator.TimeToNextFrame;
            Volatile.Write(ref _nextFrameTicks, wait == TimeSpan.MaxValue ? long.MaxValue : (now + wait).Ticks);
            if (!_isDisposed) _requestInvalidate();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AnimatedImageRenderer - animation update failed; the current frame stays up");
            Volatile.Write(ref _nextFrameTicks, long.MaxValue);
        }
        finally
        {
            _animatorLock.Release();
        }
    }

    public void HandleScalingMethodChange() { }

    /// <summary>Returns at once; the animator is disposed on the thread pool once a running frame update finishes.</summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _wake.Cancel();
        _ = Task.Run(async () =>
        {
            await _animatorLock.WaitAsync();
            _animatorLock.Release();
            _animator?.Dispose();
        });
    }
}
