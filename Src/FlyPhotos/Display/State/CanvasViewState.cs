using System;
using System.Numerics;
using Windows.Foundation;

namespace FlyPhotos.Display.State;

internal class CanvasViewState
{
    public Rect ImageRect;
    public Point ImagePos = new(0, 0);
    public Matrix3x2 Mat;
    public Matrix3x2 MatInv;

    public float Scale = 1.0f;
    public float LastScaleTo = 1.0f;

    public int Rotation = 0;

    /// <summary>
    /// When true (the default, i.e. at rest), the translations are rounded to whole pixels so the image lands
    /// on the device-pixel grid — this keeps 1:1 rendering crisp and avoids the NVIDIA nearest-neighbour glitch
    /// (#55). <see cref="Controllers.CanvasViewManager"/> clears it for the duration of a pan/zoom/shrug
    /// animation: rounding a continuously-moving translation every frame quantizes the smooth spring-settle
    /// into a 1px staircase, producing a visible "shiver" in the settling tail (worst on high-refresh
    /// displays). Rounding buys nothing mid-animation anyway — the fractional Scale already sub-pixel-samples
    /// the image — so we round only when settled. This mirrors how the XAML Image element behaves (layout
    /// rounding at rest, smooth sub-pixel resampling during a composition scale animation).
    /// </summary>
    public bool SnapTranslation = true;

    public void UpdateTransform()
    {
        Mat = Matrix3x2.Identity;

        // Centering translation. Round to whole pixels when snapping (at rest) to avoid subpixel rendering.
        // This term is constant for a given image, so it never contributes to settle-shiver; gating it keeps
        // the matrix fully unsnapped during animation for consistency.
        float translateX1 = SnapTranslation ? MathF.Round((float)(-ImageRect.Width * 0.5f)) : (float)(-ImageRect.Width * 0.5f);
        float translateY1 = SnapTranslation ? MathF.Round((float)(-ImageRect.Height * 0.5f)) : (float)(-ImageRect.Height * 0.5f);
        Mat *= Matrix3x2.CreateTranslation(translateX1, translateY1);

        // Scale operation remains unchanged
        Mat *= Matrix3x2.CreateScale(Scale, Scale);

        // Rotation remains unchanged
        Mat *= Matrix3x2.CreateRotation((float)(Math.PI * Rotation / 180f));

        // Pan translation. Per-frame rounding of this term during an animation is what causes the
        // settle-shiver, so round only when settled (SnapTranslation == true); see SnapTranslation.
        float translateX2 = SnapTranslation ? MathF.Round((float)ImagePos.X) : (float)ImagePos.X;
        float translateY2 = SnapTranslation ? MathF.Round((float)ImagePos.Y) : (float)ImagePos.Y;
        Mat *= Matrix3x2.CreateTranslation(translateX2, translateY2);

        // Calculate inverse transform
        Matrix3x2.Invert(Mat, out MatInv);
    }
}