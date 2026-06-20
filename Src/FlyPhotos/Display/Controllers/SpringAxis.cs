using System;
using FlyPhotos.Core;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// One axis of a damped harmonic oscillator: a <see cref="Position"/> and <see cref="Velocity"/> that you
/// <see cref="Step"/> toward a target each frame and ask whether it has settled. Pure motion state with no
/// knowledge of what it drives — scale springs in LOG space, pan X/Y spring in pixel space (see the
/// animation model on <see cref="CanvasViewManager"/>). Held as a long-lived field and mutated in place.
/// </summary>
internal sealed class SpringAxis
{
    /// <summary>Current value (e.g. log-scale, or a pan coordinate in pixels).</summary>
    public float Position;

    /// <summary>Current rate of change, in Position-units per second.</summary>
    public float Velocity;

    /// <summary>Re-seeds the axis at <paramref name="position"/> with zero velocity (a deliberate reset).</summary>
    public void Reset(float position)
    {
        Position = position;
        Velocity = 0f;
    }

    /// <summary>Snaps exactly onto <paramref name="target"/> and stops — used when the settle test passes.</summary>
    public void SettleTo(float target)
    {
        Position = target;
        Velocity = 0f;
    }

    /// <summary>
    /// True once both the remaining displacement to <paramref name="target"/> and the velocity are under the
    /// given thresholds.
    /// </summary>
    public bool IsSettled(float target, double positionEpsilon, double velocityEpsilon) =>
        Math.Abs(Position - target) < positionEpsilon && Math.Abs(Velocity) < velocityEpsilon;

    /// <summary>
    /// Advances one frame toward <paramref name="target"/> over <paramref name="dt"/> seconds. The frame is
    /// integrated in fixed sub-steps (<see cref="Constants.SpringMaxSubStepSeconds"/>) so a large dt — e.g. the
    /// first frame after launch — can't make a single Euler step overshoot. Pure math; the canvas still draws
    /// once per frame, and the motion is frame-rate independent.
    /// </summary>
    public void Step(float target, float dt)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(dt / Constants.SpringMaxSubStepSeconds));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var displacement = Position - target;
            var acceleration = -Constants.SpringStiffness * displacement - Constants.SpringDamping * Velocity;
            Velocity += acceleration * h;
            Position += Velocity * h;
        }
    }
}
