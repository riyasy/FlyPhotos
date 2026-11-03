using System;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// An axis-aligned rectangle in double precision. Windows.Foundation.Rect looks like double but stores floats,
/// which can't hold the visible part of an image at extreme zoom (a small fraction of one pixel).
/// </summary>
internal readonly record struct RectD(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public bool IsEmpty => !(MaxX > MinX && MaxY > MinY);

    public RectD Intersect(RectD other) => new(Math.Max(MinX, other.MinX), Math.Max(MinY, other.MinY),
        Math.Min(MaxX, other.MaxX), Math.Min(MaxY, other.MaxY));
}

/// <summary>
/// The view's image-to-screen transform (centre origin, scale, rotation, pan) in double precision, as
/// CanvasViewState.UpdateTransform composes it in float, including its rounding of the translation to whole
/// pixels at rest. At extreme zoom screen coordinates reach 1e9 px, where a float is off by tens of pixels.
/// </summary>
internal readonly record struct PreciseView(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static PreciseView Compose(double imageWidth, double imageHeight, double scale, int rotationDegrees,
        double panX, double panY, bool snapTranslation)
    {
        // Matrix3x2.CreateRotation's layout (row vectors); multiples of 90 degrees are exact, as there.
        var (cos, sin) = (((rotationDegrees % 360) + 360) % 360) switch
        {
            0 => (1.0, 0.0),
            90 => (0.0, 1.0),
            180 => (-1.0, 0.0),
            270 => (0.0, -1.0),
            _ => (Math.Cos(Math.PI * rotationDegrees / 180), Math.Sin(Math.PI * rotationDegrees / 180))
        };
        double m11 = scale * cos, m12 = scale * sin, m21 = -scale * sin, m22 = scale * cos;
        // Where the image origin lands: the centre is moved to the pan position.
        var cx = imageWidth * 0.5;
        var cy = imageHeight * 0.5;
        var offsetX = panX - (cx * m11 + cy * m21);
        var offsetY = panY - (cx * m12 + cy * m22);
        if (snapTranslation)
        {
            offsetX = Math.Round(offsetX);
            offsetY = Math.Round(offsetY);
        }
        return new PreciseView(m11, m12, m21, m22, offsetX, offsetY);
    }

    public (double X, double Y) ToScreen(double x, double y) =>
        (x * M11 + y * M21 + OffsetX, x * M12 + y * M22 + OffsetY);

    public (double X, double Y) ToImage(double x, double y)
    {
        var det = M11 * M22 - M12 * M21;
        var dx = x - OffsetX;
        var dy = y - OffsetY;
        return ((dx * M22 - dy * M21) / det, (dy * M11 - dx * M12) / det);
    }

    /// <summary>
    /// The part of the image (in image pixels, clipped to it) that a canvas of the given size shows, padded by
    /// one screen pixel; the bounding box of the canvas corners, so rotated views are covered.
    /// </summary>
    public RectD VisibleArea(double canvasWidth, double canvasHeight, double imageWidth, double imageHeight)
    {
        var p0 = ToImage(-1, -1);
        var p1 = ToImage(canvasWidth + 1, -1);
        var p2 = ToImage(-1, canvasHeight + 1);
        var p3 = ToImage(canvasWidth + 1, canvasHeight + 1);
        var box = new RectD(Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
            Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
            Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
            Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
        return box.Intersect(new RectD(0, 0, imageWidth, imageHeight));
    }
}

/// <summary>Picking the part of a source image to draw at extreme zoom. Pure geometry, so it can be unit-tested.</summary>
internal static class PreciseRegion
{
    /// <summary>
    /// For a source rectangle <paramref name="src"/> drawn onto <paramref name="dest"/> (image pixels), the part
    /// that covers <paramref name="visible"/>: rounded out to whole source pixels plus <paramref name="margin"/>
    /// more, so its coordinates are exact in float and the interpolation filter has its neighbours, then clamped
    /// to <paramref name="src"/>. Returns false when it misses the visible area.
    /// </summary>
    public static bool TryClip(RectD dest, RectD src, RectD visible, int margin, out RectD srcRegion, out RectD destRegion)
    {
        srcRegion = destRegion = default;
        var shown = dest.Intersect(visible);
        if (shown.IsEmpty || dest.IsEmpty) return false;

        var kx = src.Width / dest.Width;
        var ky = src.Height / dest.Height;
        srcRegion = new RectD(
            Math.Max(src.MinX, Math.Floor(src.MinX + (shown.MinX - dest.MinX) * kx) - margin),
            Math.Max(src.MinY, Math.Floor(src.MinY + (shown.MinY - dest.MinY) * ky) - margin),
            Math.Min(src.MaxX, Math.Ceiling(src.MinX + (shown.MaxX - dest.MinX) * kx) + margin),
            Math.Min(src.MaxY, Math.Ceiling(src.MinY + (shown.MaxY - dest.MinY) * ky) + margin));
        destRegion = new RectD(
            dest.MinX + (srcRegion.MinX - src.MinX) / kx,
            dest.MinY + (srcRegion.MinY - src.MinY) / ky,
            dest.MinX + (srcRegion.MaxX - src.MinX) / kx,
            dest.MinY + (srcRegion.MaxY - src.MinY) / ky);
        return !srcRegion.IsEmpty;
    }

    /// <summary>
    /// <paramref name="offset"/> reduced into [0, <paramref name="period"/>): where a repeating pattern starts
    /// within one period, so a huge offset never reaches a float.
    /// </summary>
    public static double PatternPhase(double offset, double period) => ((offset % period) + period) % period;
}
