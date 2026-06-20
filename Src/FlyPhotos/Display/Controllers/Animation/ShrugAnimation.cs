using System;
using Windows.Foundation;
using FlyPhotos.Core;

namespace FlyPhotos.Display.Controllers.Animation;

/// <summary>
/// A decaying horizontal sine "shake" that signals a rejected action (e.g. a failed delete). Time-based
/// rather than a spring: it wiggles for <see cref="Constants.ShrugAnimationDurationMs"/> and then snaps the
/// image back to exactly where it started.
/// </summary>
internal sealed class ShrugAnimation : IViewAnimation
{
    private readonly IAnimationHost _host;
    private Point _startPosition;

    public ShrugAnimation(IAnimationHost host) => _host = host;

    /// <summary>Captures the position the wiggle oscillates around and returns to.</summary>
    public void Start() => _startPosition = _host.View.ImagePos;

    public void Tick()
    {
        var view = _host.View;
        var t = Math.Clamp(_host.ElapsedMs / Constants.ShrugAnimationDurationMs, 0.0, 1.0);

        if (t >= 1.0)
        {
            // Finished: ensure the image is back to its exact starting position, then re-snap and redraw.
            view.ImagePos = _startPosition;
            _host.FinishShrug();
            return;
        }

        // (1 - t) fades the shake out; the sine gives the back-and-forth motion. Y is left unchanged.
        var damping = 1 - t;
        var wave = Math.Sin(t * Constants.ShrugFrequency * 2 * Math.PI);
        var xOffset = Constants.ShrugAmplitude * wave * damping;

        view.ImagePos.X = _startPosition.X + xOffset;
        view.UpdateTransform();
        _host.RaiseViewChanged();
    }

    /// <summary>A shrug has no settled target to jump to, so a forced stop simply ends the wiggle.</summary>
    public void CompleteImmediately() { }
}
