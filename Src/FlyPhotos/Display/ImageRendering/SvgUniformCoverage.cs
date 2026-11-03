using System;
using System.Collections.Generic;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>What a shape does to a window (the view): all of it, none of it, or something in between.</summary>
internal enum SvgCoverage
{
    /// <summary>The shape's boundary may cross the window: draw it normally.</summary>
    Partial,
    /// <summary>The whole window is painted: a filled rectangle does the same.</summary>
    Inside,
    /// <summary>Nothing of the window is painted: draw nothing.</summary>
    Outside
}

/// <summary>A flattened figure. <see cref="Closed"/> adds the segment from the last point back to the first.</summary>
internal readonly record struct SvgPolyline(SvgVec[] Points, bool Closed);

/// <summary>
/// Decides whether a window of an already chopped path is uniform (fully painted or not painted at all), which lets
/// the renderer replace a fill or stroke whose widening or tessellation Direct2D can't cope with at extreme zoom by
/// one rectangle or nothing. Works on flattened polylines in double precision; curves are flattened with an error
/// of at most the given tolerance, and every test keeps that tolerance as a margin so a curve can't reach into a
/// window the polyline is said to miss. Pure geometry with no Win2D dependency, so it can be unit-tested.
/// </summary>
internal static class SvgUniformCoverage
{
    /// <summary>Most pieces one curve is flattened into, whatever the tolerance asks for.</summary>
    private const int MaxCurveSegments = 1024;

    /// <summary>
    /// Flattens the path into polylines with an error of at most <paramref name="tolerance"/>. A Close ends a
    /// figure as closed; a segment after it continues from the figure's start in a new one. Single-point figures
    /// paint nothing and are dropped.
    /// </summary>
    public static List<SvgPolyline> Flatten(ReadOnlySpan<byte> verbs, ReadOnlySpan<double> points, double tolerance)
    {
        var figures = new List<SvgPolyline>();
        var current = new List<SvgVec>();
        var start = new SvgVec(0, 0);
        var pi = 0;

        void Finish(bool closed)
        {
            if (current.Count >= 2) figures.Add(new SvgPolyline(current.ToArray(), closed));
            current.Clear();
        }

        // A segment after a Close (or the first one) begins a figure at the last start point.
        void Ensure()
        {
            if (current.Count == 0) current.Add(start);
        }

        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case 0: // Move
                    Finish(false);
                    start = At(points, pi++);
                    current.Add(start);
                    break;
                case 1: // Line
                    Ensure();
                    current.Add(At(points, pi++));
                    break;
                case 2: // Quad
                {
                    Ensure();
                    var p0 = current[^1];
                    var p1 = At(points, pi++);
                    var p2 = At(points, pi++);
                    var d = p0 - p1 * 2 + p2;
                    var n = SegmentCount(Math.Sqrt(d.X * d.X + d.Y * d.Y) / 4, tolerance);
                    for (var i = 1; i <= n; i++)
                    {
                        var t = (double)i / n;
                        var u = 1 - t;
                        current.Add(i == n ? p2 : p0 * (u * u) + p1 * (2 * u * t) + p2 * (t * t));
                    }
                    break;
                }
                case 3: // Cubic
                {
                    Ensure();
                    var p0 = current[^1];
                    var p1 = At(points, pi++);
                    var p2 = At(points, pi++);
                    var p3 = At(points, pi++);
                    var d1 = p0 - p1 * 2 + p2;
                    var d2 = p1 - p2 * 2 + p3;
                    var m = Math.Max(Math.Sqrt(d1.X * d1.X + d1.Y * d1.Y), Math.Sqrt(d2.X * d2.X + d2.Y * d2.Y));
                    var n = SegmentCount(m * 0.75, tolerance);
                    for (var i = 1; i <= n; i++)
                    {
                        var t = (double)i / n;
                        var u = 1 - t;
                        current.Add(i == n
                            ? p3
                            : p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t));
                    }
                    break;
                }
                case 4: // Close
                    Finish(true);
                    break;
                default:
                    throw new FormatException("Bad path verb");
            }
        }
        Finish(false);
        return figures;
    }

    private static SvgVec At(ReadOnlySpan<double> points, int index) => new(points[2 * index], points[2 * index + 1]);

    /// <summary>
    /// Pieces for a uniform split whose chord error stays within the tolerance: the error of n equal parameter steps
    /// is <c>k * maxSecondDifference / n^2</c>, and <paramref name="errorAtOnePiece"/> is that numerator.
    /// </summary>
    private static int SegmentCount(double errorAtOnePiece, double tolerance)
    {
        if (!(tolerance > 0)) return MaxCurveSegments;
        var n = Math.Ceiling(Math.Sqrt(errorAtOnePiece / tolerance));
        return double.IsNaN(n) ? 1 : (int)Math.Clamp(n, 1, MaxCurveSegments);
    }

    /// <summary>
    /// Fill: <see cref="SvgCoverage.Partial"/> when any segment (every figure counts as closed) comes within
    /// <paramref name="tolerance"/> of <paramref name="view"/>; otherwise the whole window is uniform and the winding
    /// number of its centre decides between <see cref="SvgCoverage.Inside"/> and <see cref="SvgCoverage.Outside"/>.
    /// </summary>
    public static SvgCoverage ClassifyFill(IReadOnlyList<SvgPolyline> figures, SvgRectD view, double tolerance,
        bool evenOdd)
    {
        var probe = view.Inflate(tolerance);
        foreach (var figure in figures)
        {
            var p = figure.Points;
            for (var i = 0; i < p.Length; i++)
                if (SegmentIntersectsRect(p[i], p[(i + 1) % p.Length], probe))
                    return SvgCoverage.Partial;
        }

        var winding = WindingNumber(figures, view.Center);
        var inside = evenOdd ? (winding & 1) != 0 : winding != 0;
        return inside ? SvgCoverage.Inside : SvgCoverage.Outside;
    }

    /// <summary>
    /// Solid stroke: <see cref="SvgCoverage.Inside"/> when one segment's body (the strip of half-width
    /// <paramref name="halfWidth"/> across its length) covers all four corners of the window with
    /// <paramref name="tolerance"/> to spare; <see cref="SvgCoverage.Outside"/> when every segment stays farther
    /// than <paramref name="outset"/> (how far the stroke reaches beyond its path, caps and joins included) plus
    /// the tolerance; otherwise <see cref="SvgCoverage.Partial"/>. Open figures are used as they are, closed ones
    /// include their closing segment.
    /// </summary>
    public static SvgCoverage ClassifyStroke(IReadOnlyList<SvgPolyline> figures, SvgRectD view, double tolerance,
        double halfWidth, double outset)
    {
        var reach = outset + tolerance;
        var near = false;
        foreach (var figure in figures)
        {
            var p = figure.Points;
            var count = figure.Closed ? p.Length : p.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Length];
                if (SegmentCoversRect(a, b, view, halfWidth - tolerance)) return SvgCoverage.Inside;
                if (!near && DistanceSegmentToRect(a, b, view) <= reach) near = true;
            }
        }
        return near ? SvgCoverage.Partial : SvgCoverage.Outside;
    }

    /// <summary>
    /// The winding number of <paramref name="point"/> against the figures, each implicitly closed. The point must not
    /// lie on an edge.
    /// </summary>
    public static int WindingNumber(IReadOnlyList<SvgPolyline> figures, SvgVec point)
    {
        var winding = 0;
        foreach (var figure in figures)
        {
            var p = figure.Points;
            for (var i = 0; i < p.Length; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Length];
                var side = (b.X - a.X) * (point.Y - a.Y) - (point.X - a.X) * (b.Y - a.Y);
                if (a.Y <= point.Y)
                {
                    if (b.Y > point.Y && side > 0) winding++;
                }
                else if (b.Y <= point.Y && side < 0)
                {
                    winding--;
                }
            }
        }
        return winding;
    }

    /// <summary>Whether the segment touches the closed rectangle (Liang-Barsky clipping).</summary>
    public static bool SegmentIntersectsRect(SvgVec a, SvgVec b, SvgRectD r)
    {
        double t0 = 0, t1 = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Clip(-dx, a.X - r.MinX, ref t0, ref t1) && Clip(dx, r.MaxX - a.X, ref t0, ref t1)
               && Clip(-dy, a.Y - r.MinY, ref t0, ref t1) && Clip(dy, r.MaxY - a.Y, ref t0, ref t1);
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0) return q >= 0;
        var t = q / p;
        if (p < 0)
        {
            if (t > t1) return false;
            if (t > t0) t0 = t;
        }
        else
        {
            if (t < t0) return false;
            if (t < t1) t1 = t;
        }
        return true;
    }

    /// <summary>
    /// Whether the strip around the segment (its perpendicular feet strictly within the segment, at most
    /// <paramref name="maxDistance"/> away from its line) contains all four corners of the rectangle, and so all of it.
    /// </summary>
    public static bool SegmentCoversRect(SvgVec a, SvgVec b, SvgRectD r, double maxDistance)
    {
        if (!(maxDistance >= 0)) return false;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (!(lengthSquared > 0)) return false;
        var length = Math.Sqrt(lengthSquared);

        for (var i = 0; i < 4; i++)
        {
            var rx = ((i & 1) == 0 ? r.MinX : r.MaxX) - a.X;
            var ry = ((i & 2) == 0 ? r.MinY : r.MaxY) - a.Y;
            var t = (rx * dx + ry * dy) / lengthSquared;
            if (!(t > 0 && t < 1)) return false;
            if (Math.Abs(dx * ry - dy * rx) / length > maxDistance) return false;
        }
        return true;
    }

    /// <summary>The distance between the segment and the rectangle; 0 when they touch.</summary>
    public static double DistanceSegmentToRect(SvgVec a, SvgVec b, SvgRectD r)
    {
        if (SegmentIntersectsRect(a, b, r)) return 0;
        // Disjoint convex shapes are closest at a vertex of one of them.
        var best = Math.Min(DistancePointToRect(a, r), DistancePointToRect(b, r));
        for (var i = 0; i < 4; i++)
        {
            var corner = new SvgVec((i & 1) == 0 ? r.MinX : r.MaxX, (i & 2) == 0 ? r.MinY : r.MaxY);
            best = Math.Min(best, DistancePointToSegment(corner, a, b));
        }
        return best;
    }

    public static double DistancePointToRect(SvgVec p, SvgRectD r)
    {
        var dx = Math.Max(Math.Max(r.MinX - p.X, p.X - r.MaxX), 0);
        var dy = Math.Max(Math.Max(r.MinY - p.Y, p.Y - r.MaxY), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double DistancePointToSegment(SvgVec p, SvgVec a, SvgVec b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
        var ex = p.X - (a.X + t * dx);
        var ey = p.Y - (a.Y + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
