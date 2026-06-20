using FlyPhotos.Display.State;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers.Animation;

/// <summary>
/// A view animation (anchored zoom, pan+zoom, or shrug) advanced once per frame by
/// <see cref="CanvasViewManager"/>. Each implementation owns its own type-specific geometry and motion;
/// the shared physics substrate (springs, clock, target scale) and all plumbing (events, snapping, finish)
/// live on the <see cref="IAnimationHost"/> it is given.
/// </summary>
internal interface IViewAnimation
{
    /// <summary>Advances the animation one frame, writing into the host's view and firing host events.</summary>
    void Tick();

    /// <summary>
    /// Forces the view immediately to this animation's settled target (no events). Used when navigating to a
    /// new photo so the inherited view is the settled one rather than a mid-flight frame.
    /// </summary>
    void CompleteImmediately();
}

/// <summary>
/// The slice of <see cref="CanvasViewManager"/> an <see cref="IViewAnimation"/> needs: the live view, the
/// shared spring bank + frame clock + target scale (kept shared so velocity/clock carry continuously across
/// re-targets and across animation types), and the event / fit-state / completion plumbing.
/// </summary>
internal interface IAnimationHost
{
    CanvasViewState View { get; }

    SpringAxis ScaleSpring { get; } // springs LOG(scale)
    SpringAxis PanXSpring { get; }  // springs ImagePos.X (pixels)
    SpringAxis PanYSpring { get; }  // springs ImagePos.Y (pixels)

    /// <summary>The scale the active spring is converging to (shared between the spring animations).</summary>
    float TargetScale { get; set; }

    /// <summary>Milliseconds since the current animation's clock was (re)started — used by the shrug.</summary>
    double ElapsedMs { get; }

    /// <summary>Per-frame delta time in seconds, clamped; false when no time has elapsed yet.</summary>
    bool TryGetDt(out float dt);

    void RaiseViewChanged();

    /// <summary>Fires ZoomChanged unless the current animation suppresses it (launch/exit zoom).</summary>
    void RaiseZoomChanged();

    /// <summary>Sets the fitted / 1:1 flags from the settled scale for the given canvas (pan+zoom settle).</summary>
    void ReportSettledFit(Size canvasSize);

    /// <summary>Completes a spring: re-enables snapping, rebuilds the resting frame, fires AnimationCompleted.</summary>
    void FinishSpring();

    /// <summary>Completes a shrug: re-enables snapping, rebuilds the resting frame, fires ViewChanged.</summary>
    void FinishShrug();
}
