using System;
using FlyPhotos.Core;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// One axis of a damped spring, stepped toward a target each frame. <see cref="ViewMotion"/> runs three: the log of
/// the scale and the X and Y of the pinned point; the thumbnail strip scrolls with one too. The stiffness and damping
/// are in <see cref="Constants"/>.
/// </summary>
internal sealed class SpringAxis
{
    /// <summary>Where the axis is now. Public so a drag can move it directly mid-animation.</summary>
    public float Position;

    /// <summary>Position units per second. Kept across retargets, so quick successive steps glide.</summary>
    public float Velocity;

    /// <summary>Starts again at <paramref name="position"/>, at rest.</summary>
    public void Reset(float position)
    {
        Position = position;
        Velocity = 0f;
    }

    /// <summary>Close enough to <paramref name="target"/> and slow enough to stop without a visible jump.</summary>
    public bool IsSettled(float target, double positionEpsilon, double velocityEpsilon) =>
        Math.Abs(Position - target) < positionEpsilon && Math.Abs(Velocity) < velocityEpsilon;

    /// <summary>
    /// Advances <paramref name="dt"/> seconds toward <paramref name="target"/> in fixed sub-steps, so a long frame
    /// can't overshoot and the motion is the same at any frame rate.
    /// </summary>
    public void Step(float target, float dt)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(dt / Constants.SpringMaxSubStepSeconds));
        var subStep = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            // Semi-implicit Euler: the velocity first, then the position from the new velocity (stable for springs).
            var acceleration = -Constants.SpringStiffness * (Position - target) - Constants.SpringDamping * Velocity;
            Velocity += acceleration * subStep;
            Position += Velocity * subStep;
        }
    }
}
