using System;
using System.Numerics;
using Windows.Foundation;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>Bitmap-snapshot math shared by the vector renderers (SVG, PDF).</summary>
internal static class SnapshotMath
{
    /// <summary>
    /// <paramref name="m"/> with its offset on whole pixels when it maps bitmap pixels 1:1 to the screen (90° steps):
    /// a pan at a fractional DPI scale would otherwise resample the bitmap soft.
    /// </summary>
    public static Matrix3x2 SnapToPixels(Matrix3x2 m)
    {
        static bool Whole(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f;
        if (!(Whole(m.M11) && Whole(m.M12) && Whole(m.M21) && Whole(m.M22))) return m;
        return new Matrix3x2(MathF.Round(m.M11), MathF.Round(m.M12), MathF.Round(m.M21), MathF.Round(m.M22),
            MathF.Round(m.M31), MathF.Round(m.M32));
    }

    /// <summary>Whether <paramref name="outer"/> contains <paramref name="inner"/>, within half a pixel.</summary>
    public static bool Contains(Rect outer, Rect inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5 &&
        inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    /// <summary>The inverse of <paramref name="m"/>; identity if it has none.</summary>
    public static Matrix3x2 Inverse(Matrix3x2 m) => Matrix3x2.Invert(m, out var inv) ? inv : Matrix3x2.Identity;
}
