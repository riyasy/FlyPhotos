using System;
using System.Collections.Generic;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// A path in the command stream's layout: verbs (0 Move, 1 Line, 2 Quad, 3 Cubic, 4 Close) and x,y pairs, in double
/// precision so chopping and flattening stay exact at extreme zoom.
/// </summary>
internal readonly record struct SvgPathData(byte[] Verbs, double[] Points);

/// <summary>What the chopped path is drawn as, which decides what may change outside the cull rectangle.</summary>
internal enum SvgChopMode
{
    /// <summary>Outside curves become lines, lines are clamped into the box; the winding inside it is kept.</summary>
    Fill,
    /// <summary>A solid stroke: outside segments are dropped, lines are clamped into the box.</summary>
    Stroke,
    /// <summary>
    /// A dashed stroke: nothing is dropped or clamped, as either would shift the dash phase of everything after it;
    /// only outside curves become lines (which shortens them, so the phase is only approximately kept).
    /// </summary>
    DashedStroke
}

/// <summary>
/// Pre-chops a huge path against a cull rectangle, as Skia does before tessellating (skgpu::tess::PreChopPathCurves):
/// whatever lies completely outside the rectangle is simplified, whatever touches it is kept exactly, so the result
/// draws identically inside the rectangle but is far cheaper to fill or to widen into a stroke. Pure geometry with no
/// Win2D dependency, so it can be unit-tested; all coordinates are in the path's own space.
/// </summary>
/// <remarks>
/// A line or curve counts as "outside" when the bounding box of its points (the curve's hull lies inside the box
/// of its control points) misses the cull rectangle. A curve that overlaps it is split at t=0.5 until each piece
/// is no larger than the rectangle, so whatever reaches the renderer is about view-sized however far the view is
/// zoomed in (a fixed split depth left pieces many times the view's size at extreme zoom, and widening those into
/// a stroke froze Direct2D). The pieces kept stay exact curves. Only the pieces near the view are split further,
/// so the work grows with the logarithm of the zoom, not with the zoom. The caller inflates the rectangle by the
/// stroke's reach for strokes.
/// </remarks>
internal static class SvgPathChopper
{
    /// <summary>
    /// Safety limit on t=0.5 splits of one curve; reaching it would need a size ratio of 2^64 between the curve and
    /// the view, beyond what double coordinates resolve.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Chops the path for drawing as <paramref name="mode"/> (see <see cref="SvgChopMode"/>). Returns null when no
    /// segment is left, i.e. nothing is to be drawn.
    /// </summary>
    public static SvgPathData? Chop(ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points,
        SvgVec cullMin, SvgVec cullMax, SvgChopMode mode)
    {
        var chopper = new Chopper(cullMin, cullMax, mode);
        var pi = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case 0: // Move
                    chopper.MoveTo(At(points, pi++));
                    break;
                case 1: // Line
                    chopper.LineTo(At(points, pi++));
                    break;
                case 2: // Quad
                {
                    var c = At(points, pi++);
                    var e = At(points, pi++);
                    chopper.QuadTo(c, e);
                    break;
                }
                case 3: // Cubic
                {
                    var c1 = At(points, pi++);
                    var c2 = At(points, pi++);
                    var e = At(points, pi++);
                    chopper.CubicTo(c1, c2, e);
                    break;
                }
                case 4: // Close
                    chopper.Close();
                    break;
                default:
                    throw new FormatException("Bad path verb");
            }
        }
        return chopper.Result();
    }

    private static SvgVec At(ReadOnlySpan<float> points, int index) => new(points[2 * index], points[2 * index + 1]);

    private sealed class Chopper(SvgVec cullMin, SvgVec cullMax, SvgChopMode mode)
    {
        private readonly bool drop = mode == SvgChopMode.Stroke;
        private readonly bool _clamp = mode != SvgChopMode.DashedStroke;

        private readonly List<byte> _verbs = [];
        private readonly List<double> _points = [];
        private int _segments;

        /// <summary>The cull rectangle's larger side: overlapping curves are split until they are no larger.</summary>
        private readonly double _viewExtent = Extent(cullMin, cullMax);

        /// <summary>
        /// The box every output coordinate is kept in: the cull rectangle grown by its larger side on each side.
        /// The curve pieces kept already lie in it (they touch the rectangle and are no larger than that side);
        /// lines are cut at it and their outside parts clamped onto its border, as Skia's edge clipper does. A
        /// far-away end point (around 1e8 px off screen at extreme zoom) lands up to ~16 px off in Direct2D's float
        /// transform, which tilts the visible part of its line differently every frame; clamped points are never far.
        /// Clamping keeps the fill's winding everywhere inside the box: a piece left or right of it becomes a
        /// vertical edge on the border with the same crossings, a piece above or below a horizontal one with none.
        /// For a stroke the border pieces are at least one rectangle side beyond the stroke's reach, so invisible.
        /// </summary>
        private readonly SvgVec _boxMin = cullMin - new SvgVec(Extent(cullMin, cullMax), Extent(cullMin, cullMax));
        private readonly SvgVec _boxMax = cullMax + new SvgVec(Extent(cullMin, cullMax), Extent(cullMin, cullMax));

        private static double Extent(SvgVec min, SvgVec max) => Math.Max(max.X - min.X, max.Y - min.Y);

        /// <summary>Where the source path is now, whether or not that part was kept.</summary>
        private SvgVec _cur;
        /// <summary>The source subpath's start, where a Close returns to and a segment after it continues from.</summary>
        private SvgVec _start;
        /// <summary>The output has no open figure at <see cref="_cur"/>; the next kept segment begins one with a Move.</summary>
        private bool _needMove = true;
        /// <summary>Nothing of the current source figure was dropped so far.</summary>
        private bool _intact = true;

        public SvgPathData? Result()
        {
            FinishOpenFill();
            return _segments == 0 ? null : new SvgPathData(_verbs.ToArray(), _points.ToArray());
        }

        public void MoveTo(SvgVec p)
        {
            FinishOpenFill();
            _cur = _start = p;
            _needMove = true;
            _intact = true;
        }

        public void LineTo(SvgVec to)
        {
            if (drop && !Overlaps(SvgVec.Min(_cur, to), SvgVec.Max(_cur, to))) Outside(to);
            else EmitLine(to);
        }

        public void QuadTo(SvgVec c, SvgVec to) => Quad(_cur, c, to, 0);

        public void CubicTo(SvgVec c1, SvgVec c2, SvgVec to) => Cubic(_cur, c1, c2, to, 0);

        public void Close()
        {
            if (drop)
            {
                // The figure stays closed only if all of it was kept, closing line included. Otherwise the closing
                // line, if it reaches the view, is emitted as a plain line and the figure ends open.
                var keep = Overlaps(SvgVec.Min(_cur, _start), SvgVec.Max(_cur, _start));
                if (keep && _intact && !_needMove) CloseFigure();
                else if (keep && _cur != _start) EmitLine(_start);
            }
            else if (!_needMove)
            {
                CloseFigure();
            }
            _cur = _start;
            _needMove = true;
            _intact = true;
        }

        private void Quad(SvgVec p0, SvgVec p1, SvgVec p2, int depth)
        {
            var min = SvgVec.Min(SvgVec.Min(p0, p1), p2);
            var max = SvgVec.Max(SvgVec.Max(p0, p1), p2);
            if (!Overlaps(min, max))
            {
                Outside(p2);
                return;
            }
            if (depth >= MaxDepth || SmallEnough(min, max))
            {
                BeginFigure();
                AddVerb(2);
                AddPoint(p1);
                AddPoint(p2);
                _cur = p2;
                _segments++;
                return;
            }

            var p01 = (p0 + p1) * 0.5;
            var p12 = (p1 + p2) * 0.5;
            var mid = (p01 + p12) * 0.5;
            Quad(p0, p01, mid, depth + 1);
            Quad(mid, p12, p2, depth + 1);
        }

        private void Cubic(SvgVec p0, SvgVec p1, SvgVec p2, SvgVec p3, int depth)
        {
            var min = SvgVec.Min(SvgVec.Min(p0, p1), SvgVec.Min(p2, p3));
            var max = SvgVec.Max(SvgVec.Max(p0, p1), SvgVec.Max(p2, p3));
            if (!Overlaps(min, max))
            {
                Outside(p3);
                return;
            }
            if (depth >= MaxDepth || SmallEnough(min, max))
            {
                BeginFigure();
                AddVerb(3);
                AddPoint(p1);
                AddPoint(p2);
                AddPoint(p3);
                _cur = p3;
                _segments++;
                return;
            }

            var p01 = (p0 + p1) * 0.5;
            var p12 = (p1 + p2) * 0.5;
            var p23 = (p2 + p3) * 0.5;
            var p012 = (p01 + p12) * 0.5;
            var p123 = (p12 + p23) * 0.5;
            var mid = (p012 + p123) * 0.5;
            Cubic(p0, p01, p012, mid, depth + 1);
            Cubic(mid, p123, p23, p3, depth + 1);
        }

        /// <summary>A line or curve ending at <paramref name="to"/> that misses the cull rectangle.</summary>
        private void Outside(SvgVec to)
        {
            if (!drop)
            {
                EmitLine(to);
                return;
            }
            // Dropped: the next kept segment starts a new figure where this one ended.
            _cur = to;
            _needMove = true;
            _intact = false;
        }

        /// <summary>
        /// A line from <see cref="_cur"/>, cut where it crosses the box's border lines so that each piece lies in one
        /// region around the box, and each piece clamped into the box.
        /// </summary>
        private void EmitLine(SvgVec to)
        {
            BeginFigure();
            if (!_clamp)
            {
                AddVerb(1);
                AddPoint(to);
                _cur = to;
                _segments++;
                return;
            }
            var from = _cur;
            Span<double> cuts = stackalloc double[4];
            var n = 0;
            AddCut(ref n, cuts, from.X, to.X, _boxMin.X);
            AddCut(ref n, cuts, from.X, to.X, _boxMax.X);
            AddCut(ref n, cuts, from.Y, to.Y, _boxMin.Y);
            AddCut(ref n, cuts, from.Y, to.Y, _boxMax.Y);
            cuts[..n].Sort();
            foreach (var t in cuts[..n])
            {
                AddVerb(1);
                AddPoint(Clamp(from + (to - from) * t));
            }
            AddVerb(1);
            AddPoint(Clamp(to));
            _cur = to;
            _segments++;
        }

        /// <summary>The parameter where the segment from <paramref name="a"/> to <paramref name="b"/> crosses <paramref name="c"/>, if strictly inside.</summary>
        private static void AddCut(ref int n, Span<double> cuts, double a, double b, double c)
        {
            if (a == b) return;
            var t = (c - a) / (b - a);
            if (t > 0 && t < 1) cuts[n++] = t;
        }

        private SvgVec Clamp(SvgVec p) =>
            new(Math.Clamp(p.X, _boxMin.X, _boxMax.X), Math.Clamp(p.Y, _boxMin.Y, _boxMax.Y));

        private bool InBox(SvgVec p) =>
            p.X >= _boxMin.X && p.X <= _boxMax.X && p.Y >= _boxMin.Y && p.Y <= _boxMax.Y;

        /// <summary>
        /// Closes the output figure. Its implicit closing line runs between the clamped end points, which is only the
        /// same line when both are in the box; otherwise the closing line is emitted (and so cut) explicitly first.
        /// </summary>
        private void CloseFigure()
        {
            if (NeedsExplicitClosingLine()) EmitLine(_start);
            AddVerb(4);
        }

        /// <summary>
        /// A fill closes an open figure implicitly too, with the same problem as <see cref="CloseFigure"/>; strokes
        /// leave it open.
        /// </summary>
        private void FinishOpenFill()
        {
            if (mode == SvgChopMode.Fill && !_needMove && NeedsExplicitClosingLine()) EmitLine(_start);
        }

        private bool NeedsExplicitClosingLine() => _clamp && _cur != _start && (!InBox(_cur) || !InBox(_start));

        private void BeginFigure()
        {
            if (!_needMove) return;
            AddVerb(0);
            AddPoint(_clamp ? Clamp(_cur) : _cur);
            _needMove = false;
        }

        private void AddVerb(byte verb) => _verbs.Add(verb);

        private void AddPoint(SvgVec p)
        {
            _points.Add(p.X);
            _points.Add(p.Y);
        }

        private bool Overlaps(SvgVec min, SvgVec max) =>
            min.X <= cullMax.X && max.X >= cullMin.X && min.Y <= cullMax.Y && max.Y >= cullMin.Y;

        private bool SmallEnough(SvgVec min, SvgVec max) =>
            max.X - min.X <= _viewExtent && max.Y - min.Y <= _viewExtent;
    }
}
