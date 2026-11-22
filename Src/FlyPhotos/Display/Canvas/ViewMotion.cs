using System;
using System.Numerics;
using Windows.Foundation;
using FlyPhotos.Core;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The one view animation. The scale springs (in log space, as zoom is felt multiplicatively), and so does the screen
/// position of one pinned point of the image; the image position follows from those two. Two ways to use it:
/// <list type="bullet">
///   <item><see cref="MoveTo"/> pins the image centre and moves it to a target: fit, 100%, steps, open and exit.</item>
///   <item><see cref="ZoomAround"/> pins the image point under an anchor and keeps it there: wheel and keys.</item>
/// </list>
/// Retargeting keeps the scale's velocity, so quick notches glide. The pin keeps its velocity only when the image
/// centre is retargeted again; a new pin starts at rest, as its old velocity describes another point.
/// <para>It holds no view of its own: each call takes the current <see cref="View"/> and returns the next one.</para>
/// </summary>
/// <param name="nowMs">The animation clock in ms; the tests step it by hand.</param>
internal sealed class ViewMotion(Func<double> nowMs)
{
    /// <summary>The springs: the log of the scale, and the pinned point's screen X and Y.</summary>
    private readonly SpringAxis _logScale = new(), _pinX = new(), _pinY = new();

    /// <summary>The pinned image point, from the image centre, in layout pixels; zero when the centre is pinned.</summary>
    private Vector2 _pinOffset;

    /// <summary>Where the pinned point should end up on screen, as asked.</summary>
    private Point _pinAim;

    /// <summary><see cref="_pinAim"/> nudged by up to half a pixel so the view comes to rest on whole pixels.</summary>
    private Point _pinTarget;

    /// <summary>The clock at the last step; the next step advances the springs by the time since.</summary>
    private double _lastMs;

    /// <summary>An animation is in flight; <see cref="Step"/> must be called each frame until it settles.</summary>
    public bool Running { get; private set; }

    /// <summary>The scale the animation ends at.</summary>
    public float TargetScale { get; private set; }

    /// <summary>The image centre is pinned: a pan + zoom toward <see cref="CentreTarget"/> (<see cref="MoveTo"/>).</summary>
    public bool PinsCentre { get; private set; }

    /// <summary>Where the image centre ends up, when <see cref="PinsCentre"/>.</summary>
    public Point CentreTarget => _pinTarget;

    /// <summary>Don't show the zoom readout for this animation (the open and exit zooms). Set by <see cref="MoveTo"/>.</summary>
    public bool SilentReadout { get; private set; }

    /// <summary>Moves the image centre to <paramref name="target"/> at <paramref name="scale"/>.</summary>
    /// <param name="v">The view on screen now; the animation starts from it.</param>
    /// <param name="scale">The scale to end at.</param>
    /// <param name="target">Where the image centre ends up, in canvas pixels.</param>
    /// <param name="restart">Start over from <paramref name="v"/>, dropping any velocity (the open-zoom).</param>
    /// <param name="silentReadout">See <see cref="SilentReadout"/>.</param>
    public void MoveTo(View v, float scale, Point target, bool restart = false, bool silentReadout = false)
    {
        var keepPin = Running && PinsCentre && !restart;
        Aim(v, scale, v.Pos, Vector2.Zero, target, keepPin, restart);
        PinsCentre = true;
        SilentReadout = silentReadout;
    }

    /// <summary>Zooms to <paramref name="scale"/> keeping the image point under <paramref name="anchor"/> still.</summary>
    public void ZoomAround(View v, float scale, Point anchor)
    {
        Aim(v, scale, anchor, ImageOffsetAt(anchor, v), anchor, keepPinVelocity: false, restart: false);
        PinsCentre = false;
        SilentReadout = false;
    }

    /// <summary>
    /// Starts or retargets the animation. A running scale spring keeps its velocity unless <paramref name="restart"/>;
    /// the pin springs restart at <paramref name="pinNow"/> unless <paramref name="keepPinVelocity"/>.
    /// </summary>
    /// <param name="v">The view on screen now.</param>
    /// <param name="scale">The scale to end at.</param>
    /// <param name="pinNow">Where the pinned point is on screen now.</param>
    /// <param name="offset">The pinned point, from the image centre, in layout pixels.</param>
    /// <param name="aim">Where the pinned point should end up on screen.</param>
    /// <param name="keepPinVelocity">The same point is pinned again: keep its springs' velocity.</param>
    /// <param name="restart">Drop the scale spring's velocity too, and start the clock over.</param>
    private void Aim(View v, float scale, Point pinNow, Vector2 offset, Point aim, bool keepPinVelocity, bool restart)
    {
        if (!Running || restart)
        {
            _logScale.Reset(MathF.Log(v.Scale));
            _lastMs = nowMs();
        }
        if (!keepPinVelocity)
        {
            _pinX.Reset((float)pinNow.X);
            _pinY.Reset((float)pinNow.Y);
        }
        _pinOffset = offset;
        _pinAim = aim;
        TargetScale = scale;
        _pinTarget = OnPixelGrid(v, aim);
        Running = true;
    }

    /// <summary>
    /// The next frame of <paramref name="v"/>. <c>Moved</c> is false when no time has passed; <c>Settled</c> when it
    /// arrived, exactly on target (and the animation has stopped).
    /// </summary>
    public (View View, bool Moved, bool Settled) Step(View v)
    {
        var now = nowMs();
        // Capped, so a frame after a long stall (a debugger break, a blocked thread) doesn't fling the view.
        var dt = (float)Math.Min((now - _lastMs) / 1000.0, Constants.SpringMaxDtSeconds);
        _lastMs = now;
        if (!Running || dt <= 0f) return (v, false, false);

        var targetLog = MathF.Log(TargetScale);
        _logScale.Step(targetLog, dt);
        _pinX.Step((float)_pinTarget.X, dt);
        _pinY.Step((float)_pinTarget.Y, dt);

        var settled = _logScale.IsSettled(targetLog, Constants.SpringScaleSettleEpsilon, Constants.SpringScaleVelocitySettle)
                      && _pinX.IsSettled((float)_pinTarget.X, Constants.SpringPanSettleEpsilon, Constants.SpringPanVelocitySettle)
                      && _pinY.IsSettled((float)_pinTarget.Y, Constants.SpringPanSettleEpsilon, Constants.SpringPanVelocitySettle);
        if (settled) return (Finish(v), true, true);

        var scale = MathF.Exp(_logScale.Position);
        return (v with { Scale = scale, Pos = CentrePosFor(new Point(_pinX.Position, _pinY.Position), scale, v.Rotation) }, true, false);
    }

    /// <summary>Jumps to the end: the view at the target, at rest, and the animation stopped.</summary>
    public View Finish(View v)
    {
        Running = false;
        _logScale.Reset(MathF.Log(TargetScale));
        _pinX.Reset((float)_pinTarget.X);
        _pinY.Reset((float)_pinTarget.Y);
        return v with { Scale = TargetScale, Pos = CentrePosFor(_pinTarget, TargetScale, v.Rotation) };
    }

    /// <summary>A drag while animating: the pin and its target move with it, so the drag is kept.</summary>
    public void Nudge(double dx, double dy)
    {
        _pinX.Position += (float)dx;
        _pinY.Position += (float)dy;
        _pinAim = new Point(_pinAim.X + dx, _pinAim.Y + dy);
        _pinTarget = new Point(_pinTarget.X + dx, _pinTarget.Y + dy);
    }

    /// <summary>
    /// The image under the animation changed (<paramref name="v"/>, already rescaled by <paramref name="ratio"/>, or
    /// turned): the scale target follows, and the pin becomes whatever image point is now at its place on screen.
    /// </summary>
    public void Rebase(View v, float ratio)
    {
        if (ratio > 0f) // a non-positive ratio would put log(0) in the scale spring
        {
            TargetScale *= ratio;
            _logScale.Position += MathF.Log(ratio);
        }
        if (!PinsCentre) _pinOffset = ImageOffsetAt(new Point(_pinX.Position, _pinY.Position), v);
        _pinTarget = OnPixelGrid(v, _pinAim);
    }

    /// <summary>Where the image centre is on screen when the pinned point is at screen point <paramref name="pin"/>.</summary>
    private Point CentrePosFor(Point pin, float scale, int rotation)
    {
        var o = Vector2.Transform(_pinOffset * scale, View.RotationMatrix(rotation));
        return new Point(pin.X - o.X, pin.Y - o.Y);
    }

    /// <summary>The image point under screen point <paramref name="p"/>, from the image centre, in layout pixels.</summary>
    private static Vector2 ImageOffsetAt(Point p, View v)
    {
        if (v.Scale <= 0f) return Vector2.Zero;
        var d = new Vector2((float)(p.X - v.Pos.X), (float)(p.Y - v.Pos.Y)) / v.Scale;
        return Vector2.Transform(d, View.RotationMatrix(-v.Rotation));
    }

    /// <summary>The pin target that makes the view at the target scale rest on whole pixels (a nudge of at most half a pixel).</summary>
    private Point OnPixelGrid(View v, Point aim)
    {
        var rest = CentrePosFor(aim, TargetScale, v.Rotation);
        var snapped = v.OnPixelGrid(TargetScale, rest);
        return new Point(aim.X + snapped.X - rest.X, aim.Y + snapped.Y - rest.Y);
    }
}
