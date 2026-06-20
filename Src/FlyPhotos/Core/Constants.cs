using System.Collections.Generic;

namespace FlyPhotos.Core;

internal static class Constants
{
    public const string AppVersion = "2.6.4";

    // ───────────────────────── Pan / Zoom animation ─────────────────────────
    // The full model is documented on CanvasViewManager. In short: every user-triggered zoom/pan is a
    // damped-spring animation (scale springs in LOG space, pan in pixels); launch open-zoom and exit
    // zoom-out reuse that spring; only a now-dormant cubic tween remains as a fallback.

    // Close/launch WAIT budgets, in ms (the caller waits this x2 for AnimationCompleted, then proceeds).
    // NOT animation durations any more — the spring sets its own pace. For the exit, the spring shrinks the
    // image to a sub-pixel dot well before this elapses, so it is effectively how long the (already-blank)
    // window lingers before closing.
    //   ↑ exit value  → window lingers longer after the image has vanished (feels sluggish to close).
    //   ↓ exit value  → snappier close; too low (< ~80) risks closing while the image is still visibly
    //                    shrinking on a slow/first-frame, looking like an abrupt cut.
    public const int PanZoomAnimationDurationForExit = 110;
    // Launch-zoom wait ceiling (x2 = 1.2 s). Pure safety net — the open spring settles (~0.65 s) and fires
    // AnimationCompleted well before this, so the value rarely matters; keep it comfortably above settle.
    public const int PanZoomAnimationDurationNormal = 600;
    public const int OffScreenDrawDelayMs = 400;

    // ── Spring physics (damped harmonic oscillator): accel = -Stiffness*displacement - Damping*velocity ──
    // The two that govern *feel* are the derived quantities, not the raw numbers:
    //   natural frequency  ω0 = sqrt(Stiffness)         → how fast it wants to move   (=22.4 rad/s here)
    //   damping ratio      ζ  = Damping / (2*sqrt(Stiffness))  → the SHAPE of the motion (=1.12 here)
    //     ζ<1 underdamped → overshoots & rings;  ζ=1 critical → fastest with no overshoot;  ζ>1 overdamped.
    // Rule of thumb: to change speed without changing feel, scale BOTH so Damping ≈ 2*sqrt(Stiffness).

    // Restoring-force strength — "snappiness".
    //   ↑ Stiffness → faster/snappier zoom; BUT ζ drops, so raise Damping too or it starts to overshoot.
    //                 (Very high also needs a smaller SpringMaxSubStepSeconds to stay accurate.)
    //   ↓ Stiffness → slower, floatier, "drifting"; ζ rises so it also gets more sluggish/laggy.
    public const float SpringStiffness = 500f;
    // Friction — controls overshoot vs sluggishness. Critical damping here is 2*sqrt(500) ≈ 44.7.
    //   ↑ Damping → more overdamped: no overshoot but a slower, lazier approach into the target.
    //   ↓ Damping → livelier; once below ~44.7 (ζ<1) it overshoots and bounces (reads as a glitch on zoom).
    public const float SpringDamping = 50f; // ζ ≈ 1.12 → just overdamped, so zoom never overshoots.

    // Upper clamp on a single frame's dt (e.g. the first frame after the Win2D control un-pauses). Caps how
    // much SIMULATION time one frame may advance, so after a stall the animation resumes rather than
    // teleporting forward. (Accuracy is handled by sub-stepping; this is purely "don't skip ahead".)
    //   ↑ value → after a stall the animation can lurch further forward in one frame.
    //   ↓ value → less lurch, but below a real frame interval the spring runs slow on low-fps machines.
    public const float SpringMaxDtSeconds = 0.05f;
    // Max integration sub-step (~240 Hz). Each frame's dt is integrated in steps no larger than this so
    // every step stays accurate (needs stepSize*sqrt(Stiffness) << 1); this is what stops the large
    // first-frame-after-launch dt from overshooting. Pure math — rendering still draws once per frame.
    //   ↑ value → coarser integration; risks overshoot/jitter on big frames (defeats the fix).
    //   ↓ value → more sub-steps per frame (slightly more CPU, still negligible); needed if Stiffness ↑ a lot.
    public const float SpringMaxSubStepSeconds = 1f / 240f;

    // Settle thresholds — the animation ends (snaps to target, fires AnimationCompleted) once BOTH the
    // remaining displacement AND the velocity are under these. Lower = settles more precisely but the loop
    // runs longer doing invisible micro-motion; higher = ends sooner but can visibly snap the last bit.
    //public const float SpringScaleSettleEpsilon = 0.0002f; // scale closeness, in LOG units (~0.02% of scale)
    public const float SpringScaleSettleEpsilon = 0.0008f; // modified from 0.0002f to 0.0008f to make it settle
                                                           // faster with less CPU usage, at the cost of a slightly
                                                           // less precise settle (still very close, within ~0.08% of scale).
    public const float SpringScaleVelocitySettle = 0.05f;  // scale speed, log-units/sec
    public const double SpringPanSettleEpsilon = 0.1;      // pan closeness, pixels
    public const float SpringPanVelocitySettle = 2f;       // pan speed, pixels/sec

    // Anchored zoom (wheel / keyboard / side-button) lands its resting frame on the device-pixel grid by
    // blending a constant ≤0.5 px grid-alignment offset into the pan over the settle tail. This is the
    // log-scale window (distance to target) over which that offset ramps 0→1: OUTSIDE it the cursor anchor
    // is pinned exactly (zero drift); INSIDE it the offset glides in so the landing is grid-clean with no
    // end-snap. Larger = gentler/longer glide; smaller = the ≤0.5 px correction comes in more abruptly.
    public const double ZoomGridAlignBlendRangeLog = 0.08;

    // Related to Shrug Animation for Delete Failure
    public const double ShrugAnimationDurationMs = 350;
    public const double ShrugAmplitude = 20; // How many pixels to shake
    public const double ShrugFrequency = 4;  // How many "wiggles"       
        
    // Thumbnail Related
    public const int ThumbnailPixelBufferSize = 128; // intermediate square pixel buffer stored on Photo
    public const int ThumbnailPadding = 2;
    public const float ThumbnailSelectionBorderThickness = 3.0f;
    public const float ThumbnailCornerRadius = 4.0f;

    // Others
    public const int CheckerSize = 10;

    public static readonly List<string> SupportedLanguages =
    [
        "en-US", // English
        "de-DE", // German
        "fr-FR", // French
        "es-ES", // Spanish
        "it-IT", // Italian
        "pl-PL", // Polish
        "nl-NL", // Dutch
        "sv-SE", // Swedish
        "fi-FI", // Finnish
        "pt-PT", // Portuguese
        "pt-BR", // Portuguese (Brazil)
        "hu-HU", // Hungarian
        "ru-RU", // Russian
        "uk-UA", // Ukrainian
        "ja-JP", // Japanese
        "ko-KR", // Korean
        "zh-CN", // Chinese (Simplified, China)
        "zh-TW", // Chinese (Traditional, Taiwan)
        "ml-IN"  // Malayalam
    ];
}