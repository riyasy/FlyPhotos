using System;
using System.Numerics;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Double-precision 2D geometry for the SVG vector renderer, which at extreme zoom (screen coordinates in the
/// hundreds of millions of pixels) can't use float transforms. No Win2D dependency, so it can be unit-tested.
/// </summary>
internal readonly record struct SvgVec(double X, double Y)
{
    public static SvgVec operator +(SvgVec a, SvgVec b) => new(a.X + b.X, a.Y + b.Y);
    public static SvgVec operator -(SvgVec a, SvgVec b) => new(a.X - b.X, a.Y - b.Y);
    public static SvgVec operator *(SvgVec a, double s) => new(a.X * s, a.Y * s);
    public static SvgVec Min(SvgVec a, SvgVec b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
    public static SvgVec Max(SvgVec a, SvgVec b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}

/// <summary>An axis-aligned rectangle by its edges.</summary>
internal readonly record struct SvgRectD(double MinX, double MinY, double MaxX, double MaxY)
{
    public SvgVec Min => new(MinX, MinY);
    public SvgVec Max => new(MaxX, MaxY);
    public SvgVec Center => new((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);
    public double Diagonal => Math.Sqrt((MaxX - MinX) * (MaxX - MinX) + (MaxY - MinY) * (MaxY - MinY));

    public SvgRectD Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);
}

/// <summary>
/// A 2D affine transform in System.Numerics' row-vector convention (<c>p' = p * M</c>, so in a product the left
/// operand applies first), in double precision.
/// </summary>
internal readonly record struct SvgAffine(double M11, double M12, double M21, double M22, double M31, double M32)
{
    public static SvgAffine Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static SvgAffine Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static SvgAffine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary>Rotation by whole degrees; multiples of 90 are exact, as in Matrix3x2.CreateRotation.</summary>
    public static SvgAffine Rotation(int degrees)
    {
        var (cos, sin) = (((degrees % 360) + 360) % 360) switch
        {
            0 => (1.0, 0.0),
            90 => (0.0, 1.0),
            180 => (-1.0, 0.0),
            270 => (0.0, -1.0),
            _ => (Math.Cos(Math.PI * degrees / 180), Math.Sin(Math.PI * degrees / 180))
        };
        return new SvgAffine(cos, sin, -sin, cos, 0, 0);
    }

    public static SvgAffine From(Matrix3x2 m) => new(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);

    public Matrix3x2 ToMatrix3x2() => new((float)M11, (float)M12, (float)M21, (float)M22, (float)M31, (float)M32);

    /// <summary>Applies <paramref name="a"/> first, then <paramref name="b"/>.</summary>
    public static SvgAffine operator *(SvgAffine a, SvgAffine b) => new(
        a.M11 * b.M11 + a.M12 * b.M21,
        a.M11 * b.M12 + a.M12 * b.M22,
        a.M21 * b.M11 + a.M22 * b.M21,
        a.M21 * b.M12 + a.M22 * b.M22,
        a.M31 * b.M11 + a.M32 * b.M21 + b.M31,
        a.M31 * b.M12 + a.M32 * b.M22 + b.M32);

    public SvgVec Transform(SvgVec p) => new(p.X * M11 + p.Y * M21 + M31, p.X * M12 + p.Y * M22 + M32);

    /// <summary>The axis-aligned box of the four corners of <paramref name="r"/> mapped through this transform.</summary>
    public SvgRectD TransformBounds(SvgRectD r)
    {
        var p0 = Transform(new SvgVec(r.MinX, r.MinY));
        var p1 = Transform(new SvgVec(r.MaxX, r.MinY));
        var p2 = Transform(new SvgVec(r.MinX, r.MaxY));
        var p3 = Transform(new SvgVec(r.MaxX, r.MaxY));
        var min = SvgVec.Min(SvgVec.Min(p0, p1), SvgVec.Min(p2, p3));
        var max = SvgVec.Max(SvgVec.Max(p0, p1), SvgVec.Max(p2, p3));
        return new SvgRectD(min.X, min.Y, max.X, max.Y);
    }

    /// <summary>False when the transform is singular or not finite.</summary>
    public bool TryInvert(out SvgAffine inverse)
    {
        var det = M11 * M22 - M12 * M21;
        if (!(Math.Abs(det) > 0) || !double.IsFinite(det))
        {
            inverse = default;
            return false;
        }
        var inv = 1 / det;
        inverse = new SvgAffine(M22 * inv, -M12 * inv, -M21 * inv, M11 * inv,
            (M21 * M32 - M22 * M31) * inv, (M12 * M31 - M11 * M32) * inv);
        return true;
    }

    /// <summary>
    /// The image-to-screen transform of CanvasViewState (centre origin, scale, rotate, pan), in double precision.
    /// With <paramref name="snapTranslation"/> the final translation is rounded to whole pixels, as
    /// CanvasViewState.UpdateTransform does. <paramref name="rotationDegrees"/> is the view's whole-degree rotation.
    /// </summary>
    public static SvgAffine ComposeView(double imageWidth, double imageHeight, double scale, int rotationDegrees,
        double panX, double panY, bool snapTranslation)
    {
        var m = Translation(-imageWidth * 0.5, -imageHeight * 0.5) * Scale(scale, scale)
                * Rotation(rotationDegrees) * Translation(panX, panY);
        return snapTranslation ? m with { M31 = Math.Round(m.M31), M32 = Math.Round(m.M32) } : m;
    }
}
