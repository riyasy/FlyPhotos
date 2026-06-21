using System;
using Windows.Foundation;
using FlyPhotos.Core;

namespace FlyPhotos.Display.Controllers.Animation;

/// <summary>
/// Cursor/keyboard/side-button anchored zoom: scale springs in log space while the pan is derived each frame
/// to keep a screen anchor point stationary. Owns the fragile snap-fix state — the PURE anchor track (pan
/// tied to scale, with NO grid offset baked in so it can't feed back and accumulate) and the constant ≤0.5 px
/// grid-alignment offset that is blended into the displayed pan over the settle tail.
/// </summary>
internal sealed class AnchoredZoomAnimation : IViewAnimation
{
    private readonly IAnimationHost _host;
    private Point _zoomCenter;
    private double _anchorX;
    private double _anchorY;
    private double _gridOffsetX;
    private double _gridOffsetY;

    public AnchoredZoomAnimation(IAnimationHost host) => _host = host;

    /// <summary>
    /// Points the zoom at <paramref name="targetScale"/> anchored at <paramref name="anchor"/>. On a fresh
    /// start (<paramref name="resetAnchorTrack"/> = true) the anchor track is seeded from the live, already
    /// grid-aligned position; on a re-target it is kept so the constant grid offset never feeds back and
    /// accumulates. The grid offset δ is (re)computed against the new target either way.
    /// </summary>
    public void Aim(float targetScale, Point anchor, bool resetAnchorTrack)
    {
        var view = _host.View;
        _zoomCenter = anchor;
        if (resetAnchorTrack)
        {
            _anchorX = view.ImagePos.X;
            _anchorY = view.ImagePos.Y;
        }
        _host.TargetScale = targetScale;

        // Precompute the constant grid-alignment offset δ (≤0.5 px/axis): take where the exact-anchor zoom
        // would settle (the anchor-preserving pan at the target scale), nudge it so the composed translation
        // lands on whole pixels, and store the difference. Tick blends it in over the settle tail so the
        // resting frame is grid-clean.
        var startScale = view.Scale;
        if (startScale > 0f)
        {
            var anchorFinal = ZoomGeometry.AnchorPreservingPan(_zoomCenter, new Point(_anchorX, _anchorY), startScale, targetScale);
            var aligned = view.SnapImagePosToPixelGrid(targetScale, anchorFinal);
            _gridOffsetX = aligned.X - anchorFinal.X;
            _gridOffsetY = aligned.Y - anchorFinal.Y;
        }
    }

    public void Tick()
    {
        if (!_host.TryGetDt(out var dt)) return;

        var view = _host.View;
        var scale = _host.ScaleSpring;
        var targetLog = (float)Math.Log(_host.TargetScale);
        scale.Step(targetLog, dt);

        var settled = scale.IsSettled(targetLog, Constants.SpringScaleSettleEpsilon, Constants.SpringScaleVelocitySettle);

        float newScale;
        if (settled)
        {
            scale.SettleTo(targetLog);
            newScale = _host.TargetScale;
        }
        else
        {
            newScale = (float)Math.Exp(scale.Position);
        }

        // Advance the PURE anchor track: pan tied to scale so the zoom anchor stays pinned every frame with
        // zero wobble. The track never includes the grid offset, so it can't feed back into itself and drift.
        var track = ZoomGeometry.AnchorPreservingPan(_zoomCenter, new Point(_anchorX, _anchorY), view.Scale, newScale);
        _anchorX = track.X;
        _anchorY = track.Y;
        view.Scale = newScale;

        // Blend the constant ≤0.5 px grid-alignment offset in over the settle tail: w = 0 through the bulk of
        // the zoom (anchor pinned exactly), ramping to 1 at settle so the resting frame lands on the device-
        // pixel grid (the at-rest snap is then a no-op). No start jump, no end snap; the only drift is this
        // ≤0.5 px glide near the very end.
        var w = settled
            ? 1.0
            : Math.Clamp(1.0 - Math.Abs(scale.Position - targetLog) / Constants.ZoomGridAlignBlendRangeLog, 0.0, 1.0);
        view.ImagePos.X = _anchorX + w * _gridOffsetX;
        view.ImagePos.Y = _anchorY + w * _gridOffsetY;
        view.UpdateTransform();
        _host.RaiseViewChanged();
        _host.RaiseZoomChanged();

        if (settled) _host.FinishSpring();
    }

    public void CompleteImmediately()
    {
        var view = _host.View;
        // Jump the pure anchor track to the exact target scale, then apply the full grid offset so the
        // forced-stop resting frame lands grid-clean (matches a natural settle).
        var oldScale = view.Scale;
        if (oldScale > 0)
        {
            var track = ZoomGeometry.AnchorPreservingPan(_zoomCenter, new Point(_anchorX, _anchorY), oldScale, _host.TargetScale);
            _anchorX = track.X;
            _anchorY = track.Y;
        }
        view.Scale = _host.TargetScale;
        view.ImagePos.X = _anchorX + _gridOffsetX;
        view.ImagePos.Y = _anchorY + _gridOffsetY;
        view.UpdateTransform();
    }
}
