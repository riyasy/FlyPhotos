using System.Collections.Generic;

namespace FlyPhotos.Core;

internal static class Constants
{
    public const string AppVersion = "2.6.1";

    // Pan Zoom Animation Related
    // Cubic-tween durations are used only by the launch open-zoom and exit zoom-out paths.
    public const int PanZoomAnimationDurationForExit = 200;
    public const int PanZoomAnimationDurationNormal = 600;
    public const int OffScreenDrawDelayMs = 400;

    // Spring (damped harmonic oscillator) for all user-triggered zoom / pan+zoom.
    // Damping ~ 2*sqrt(Stiffness) is critical; slightly above => overdamped => no overshoot.
    public const float SpringStiffness = 500f; // higher = snappier
    public const float SpringDamping = 50f;    // 2*sqrt(500) ~= 44.7 -> zeta ~= 1.12 (overdamped)
    public const float SpringMaxDtSeconds = 0.05f; // clamp per-frame dt (e.g. after the control un-pauses)
    // Settle thresholds: snap to target once both displacement and velocity fall below these.
    public const float SpringScaleSettleEpsilon = 0.0002f; // in log-scale units
    public const float SpringScaleVelocitySettle = 0.05f;
    public const double SpringPanSettleEpsilon = 0.1;       // pixels
    public const float SpringPanVelocitySettle = 2f;        // pixels/sec

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
        "ru-RU", // Russian
        "uk-UA", // Ukrainian
        "ja-JP", // Japanese
        "ko-KR", // Korean
        "zh-CN", // Chinese (Simplified, China)
        "zh-TW", // Chinese (Traditional, Taiwan)
        "ml-IN"  // Malayalam
    ];
}