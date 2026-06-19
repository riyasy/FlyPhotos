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
    /// <para>
    /// Animation settle targets are pre-quantized via <see cref="SnapImagePosToPixelGrid"/> so the settled
    /// frame already lands on the grid; the round below is then a no-op for those paths (a pure guard) and
    /// the image no longer visibly "snaps" by ±0.5 px when an animation completes. The guard still fires for
    /// direct-set paths (drag-pan, touchpad precision zoom, restored views).
    /// </para>
    /// </summary>
    public bool SnapTranslation = true;

    public void UpdateTransform()
    {
        Mat = ComposeUnsnapped(Scale, ImagePos);

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

    /// <summary>
    /// Builds the full image→screen transform (centre origin → scale → rotate → pan) WITHOUT the pixel-grid
    /// snap. Shared by <see cref="UpdateTransform"/> and <see cref="SnapImagePosToPixelGrid"/> so the two can
    /// never disagree about how translation is composed.
    /// </summary>
    private Matrix3x2 ComposeUnsnapped(float scale, Point imagePos)
    {
        var m = Matrix3x2.Identity;
        m *= Matrix3x2.CreateTranslation((float)(-ImageRect.Width * 0.5f), (float)(-ImageRect.Height * 0.5f));
        m *= Matrix3x2.CreateScale(scale, scale);
        m *= Matrix3x2.CreateRotation((float)(Math.PI * Rotation / 180f));
        m *= Matrix3x2.CreateTranslation((float)imagePos.X, (float)imagePos.Y);
        return m;
    }

    /// <summary>
    /// Returns <paramref name="imagePos"/> nudged (≤0.5 px) so that the fully-composed translation
    /// (Mat.M31/M32) at <paramref name="scale"/> lands on whole device pixels. ImagePos enters the composed
    /// translation with coefficient 1, so subtracting the fractional residual of M31/M32 from it is exact.
    /// Feeding an animation this as its settle target makes the at-rest <see cref="SnapTranslation"/> round a
    /// no-op, so there is no end-of-zoom shift.
    /// </summary>
    public Point SnapImagePosToPixelGrid(float scale, Point imagePos)
    {
        var m = ComposeUnsnapped(scale, imagePos);
        return new Point(imagePos.X - (m.M31 - MathF.Round(m.M31)),
                         imagePos.Y - (m.M32 - MathF.Round(m.M32)));
    }
}