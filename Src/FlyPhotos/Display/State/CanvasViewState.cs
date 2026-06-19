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
        Mat *= Matrix3x2.CreateTranslation((float)(-ImageRect.Width * 0.5f), (float)(-ImageRect.Height * 0.5f));
        Mat *= Matrix3x2.CreateScale(Scale, Scale);
        Mat *= Matrix3x2.CreateRotation((float)(Math.PI * Rotation / 180f));
        Mat *= Matrix3x2.CreateTranslation((float)ImagePos.X, (float)ImagePos.Y);

        // Snap the final screen-space translation to whole pixels to avoid the NVIDIA nearest-neighbour
        // glitch (#55). Snapping pre-scale terms amplifies the rounding by Scale (e.g. ±10 px at 2000%),
        // so we snap Mat.M31/M32 after full composition — always exactly ±0.5 screen px regardless of zoom.
        // SnapTranslation is cleared during animations to prevent a 1-px staircase shiver in the settle tail.
        if (SnapTranslation)
        {
            Mat.M31 = MathF.Round(Mat.M31);
            Mat.M32 = MathF.Round(Mat.M32);
        }

        Matrix3x2.Invert(Mat, out MatInv);
    }
}