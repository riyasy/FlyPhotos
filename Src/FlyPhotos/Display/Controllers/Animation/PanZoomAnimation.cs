using System;
using Windows.Foundation;
using FlyPhotos.Core;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers.Animation;

/// <summary>
/// Combined pan+zoom toward an explicit target (Fit, 100%, centred step, double-click, launch open-zoom,
/// exit zoom-out). Scale springs in log space; pan X and pan Y spring independently in pixel space toward a
/// pre-grid-aligned target so the settled frame lands on the device-pixel grid (the at-rest snap is a no-op).
/// </summary>
internal sealed class PanZoomAnimation : IViewAnimation
{
    private readonly IAnimationHost _host;
    private Point _panTarget;
    private Size _targetCanvasSize;

    public PanZoomAnimation(IAnimationHost host) => _host = host;

    /// <summary>The image-centre position the spring settles at (pixel-grid aligned).</summary>
    public Point PanTarget => _panTarget;

    /// <summary>Don't show the zoom readout while this runs (the launch open-zoom and the exit zoom-out).</summary>
    public bool SuppressZoomReadout { get; set; }

    /// <summary>
    /// Points the spring at <paramref name="targetScale"/> / <paramref name="targetPosition"/>. The pan target
    /// is pre-aligned to the pixel grid so the at-rest snap is a no-op. <paramref name="reseedPan"/> seeds the
    /// pan springs from the live position (whenever the previous animation wasn't already a pan spring, or on a
    /// forced reseed); otherwise pan velocity carries forward for a seamless re-target.
    /// </summary>
    public void Aim(float targetScale, Point targetPosition, Size targetCanvasSize, bool reseedPan)
    {
        var view = _host.View;
        if (reseedPan)
        {
            _host.PanXSpring.Reset((float)view.ImagePos.X);
            _host.PanYSpring.Reset((float)view.ImagePos.Y);
        }
        view.TargetScale = targetScale;
        _panTarget = view.SnapImagePosToPixelGrid(targetScale, targetPosition);
        _targetCanvasSize = targetCanvasSize;
    }

    public void Tick()
    {
        if (!_host.TryGetDt(out var dt)) return;

        var view = _host.View;
        var scale = _host.ScaleSpring;
        var panX = _host.PanXSpring;
        var panY = _host.PanYSpring;

        var targetLog = (float)Math.Log(view.TargetScale);
        scale.Step(targetLog, dt);
        panX.Step((float)_panTarget.X, dt);
        panY.Step((float)_panTarget.Y, dt);

        var scaleSettled = scale.IsSettled(targetLog, Constants.SpringScaleSettleEpsilon, Constants.SpringScaleVelocitySettle);
        var panSettled = panX.IsSettled((float)_panTarget.X, Constants.SpringPanSettleEpsilon, Constants.SpringPanVelocitySettle)
                         && panY.IsSettled((float)_panTarget.Y, Constants.SpringPanSettleEpsilon, Constants.SpringPanVelocitySettle);
        var settled = scaleSettled && panSettled;

        if (settled)
        {
            scale.SettleTo(targetLog);
            panX.SettleTo((float)_panTarget.X);
            panY.SettleTo((float)_panTarget.Y);
            view.Scale = view.TargetScale;
        }
        else
        {
            view.Scale = (float)Math.Exp(scale.Position);
        }
        view.ImagePos.X = panX.Position;
        view.ImagePos.Y = panY.Position;
        view.UpdateTransform();
        _host.RaiseViewChanged();
        if (!SuppressZoomReadout) _host.RaiseZoomChanged();

        if (settled)
        {
            _host.ReportSettledFit(_targetCanvasSize); // truth-set fitted / 1:1 from the settled scale
            _host.FinishSpring();
        }
    }

    public void NudgePan(double dx, double dy)
    {
        // Translate the pan target AND the current pan-spring state by the drag delta so the whole pan
        // trajectory shifts with the cursor; the springs keep converging (now toward the shifted target)
        // instead of dragging the image back to the original target next Tick.
        _panTarget.X += dx;
        _panTarget.Y += dy;
        _host.PanXSpring.Position += (float)dx;
        _host.PanYSpring.Position += (float)dy;

        var view = _host.View;
        view.ImagePos.X += dx;
        view.ImagePos.Y += dy;
        view.UpdateTransform();
    }

    public void ScalePanAbout(Point centre, double kx, double ky)
    {
        var view = _host.View;
        // Re-aligned to the pixel grid for the new image, as Aim does.
        _panTarget = view.SnapImagePosToPixelGrid(view.TargetScale, ZoomGeometry.ScaleAbout(_panTarget, centre, kx, ky));
        _host.PanXSpring.ScaleAbout(centre.X, kx);
        _host.PanYSpring.ScaleAbout(centre.Y, ky);
    }

    public void CompleteImmediately()
    {
        var view = _host.View;
        view.Scale = view.TargetScale;
        view.ImagePos = _panTarget;
        view.UpdateTransform();
    }
}
