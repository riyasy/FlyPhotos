using FlyPhotos.Display.ImageRendering;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class SvgUniformCoverageTests
{
    private const byte Move = 0, Line = 1, Cubic = 3, Close = 4;

    /// <summary>The flattening tolerance the renderer uses for a window: its diagonal over 64.</summary>
    private static double Tolerance(SvgRectD view) => view.Diagonal / 64;

    private static List<SvgPolyline> Flatten(byte[] verbs, double[] points, double tolerance = 1) =>
        SvgUniformCoverage.Flatten(verbs, points, tolerance);

    private static SvgCoverage Fill(List<SvgPolyline> figures, SvgRectD view, bool evenOdd = false) =>
        SvgUniformCoverage.ClassifyFill(figures, view, Tolerance(view), evenOdd);

    private static SvgCoverage Stroke(List<SvgPolyline> figures, SvgRectD view, double halfWidth) =>
        SvgUniformCoverage.ClassifyStroke(figures, view, Tolerance(view), halfWidth, outset: halfWidth);

    private static SvgRectD Window(double x, double y, double size = 20) => new(x, y, x + size, y + size);

    private static List<SvgPolyline> Square(double min, double max, bool reversed = false)
    {
        double[] points = reversed
            ? [min, min, min, max, max, max, max, min]
            : [min, min, max, min, max, max, min, max];
        return Flatten([Move, Line, Line, Line, Close], points);
    }

    // ---- fill ----

    [TestMethod]
    public void Fill_window_deep_inside_a_figure_is_inside_under_both_rules()
    {
        var figures = Square(0, 1000);
        var view = Window(490, 490);

        Assert.AreEqual(SvgCoverage.Inside, Fill(figures, view));
        Assert.AreEqual(SvgCoverage.Inside, Fill(figures, view, evenOdd: true));
    }

    [TestMethod]
    public void Fill_window_in_a_hole_depends_on_the_rule_and_the_direction_of_the_hole()
    {
        var view = Window(490, 490);

        var sameDirection = Square(0, 1000).Concat(Square(400, 600)).ToList();
        Assert.AreEqual(SvgCoverage.Inside, Fill(sameDirection, view), "winding 2 is filled under nonzero");
        Assert.AreEqual(SvgCoverage.Outside, Fill(sameDirection, view, evenOdd: true), "winding 2 is a hole under evenodd");

        var opposite = Square(0, 1000).Concat(Square(400, 600, reversed: true)).ToList();
        Assert.AreEqual(SvgCoverage.Outside, Fill(opposite, view), "winding 0 is a hole under nonzero");
        Assert.AreEqual(SvgCoverage.Outside, Fill(opposite, view, evenOdd: true));
    }

    [TestMethod]
    public void Fill_window_beyond_the_figure_is_outside()
    {
        var figures = Square(0, 1000);

        Assert.AreEqual(SvgCoverage.Outside, Fill(figures, Window(2000, 490)));
        Assert.AreEqual(SvgCoverage.Outside, Fill(figures, Window(490, -500), evenOdd: true));
    }

    [TestMethod]
    public void Fill_window_crossed_by_an_edge_is_partial()
    {
        var figures = Square(0, 1000);

        Assert.AreEqual(SvgCoverage.Partial, Fill(figures, Window(990, 490)));
        Assert.AreEqual(SvgCoverage.Partial, Fill(figures, Window(-10, -10), evenOdd: true), "a corner of the figure");
    }

    [TestMethod]
    public void Fill_edge_closer_than_the_tolerance_is_partial_and_farther_is_not()
    {
        var figures = Square(0, 1000);
        var tolerance = Tolerance(Window(0, 0)); // 0.44

        // The window's right edge ends 0.2 short of the figure's edge at x=1000, then 2.0 short of it.
        Assert.IsTrue(0.2 < tolerance && 2.0 > tolerance);
        Assert.AreEqual(SvgCoverage.Partial, Fill(figures, new SvgRectD(980, 490, 999.8, 510)));
        Assert.AreEqual(SvgCoverage.Inside, Fill(figures, new SvgRectD(978, 490, 998, 510)));
    }

    [TestMethod]
    public void Fill_treats_an_open_figure_as_closed()
    {
        var figures = Flatten([Move, Line, Line], [0, 0, 1000, 0, 0, 1000]);

        Assert.IsFalse(figures[0].Closed);
        Assert.AreEqual(SvgCoverage.Inside, Fill(figures, Window(100, 100)));
        Assert.AreEqual(SvgCoverage.Outside, Fill(figures, Window(800, 800)), "beyond the implicit closing line");
    }

    [TestMethod]
    public void Fill_of_a_curved_figure_uses_the_flattened_outline()
    {
        var figures = Circle(1000);

        Assert.AreEqual(SvgCoverage.Inside, Fill(figures, Window(-10, -10)));
        Assert.AreEqual(SvgCoverage.Partial, Fill(figures, Window(990, -10)));
        Assert.AreEqual(SvgCoverage.Outside, Fill(figures, Window(1100, -10)));
        Assert.AreEqual(SvgCoverage.Outside, Fill(figures, Window(800, 800)), "outside the circle but inside its bounding box");
    }

    /// <summary>A circle of the given radius around the origin from four cubics.</summary>
    private static List<SvgPolyline> Circle(double r, double tolerance = 1)
    {
        var k = 0.5522847498307936 * r;
        return Flatten([Move, Cubic, Cubic, Cubic, Cubic, Close],
        [
            r, 0,
            r, k, k, r, 0, r,
            -k, r, -r, k, -r, 0,
            -r, -k, -k, -r, 0, -r,
            k, -r, r, -k, r, 0
        ], tolerance);
    }

    // ---- flattening ----

    [TestMethod]
    public void Flatten_keeps_curves_within_the_tolerance()
    {
        const double tolerance = 0.05;
        var figures = Circle(1000, tolerance);

        var points = figures.Single().Points;
        Assert.IsTrue(figures.Single().Closed);
        Assert.IsTrue(points.Length > 8, "the curves are subdivided");
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            var mid = (a + b) * 0.5;
            // The cubic circle approximation itself is off by up to 0.03% of the radius; allow for it.
            Assert.IsTrue(Math.Abs(Math.Sqrt(mid.X * mid.X + mid.Y * mid.Y) - 1000) < tolerance + 0.3,
                $"chord {i} strays from the curve");
        }
    }

    [TestMethod]
    public void Flatten_splits_figures_at_moves_and_after_closes()
    {
        var figures = Flatten([Move, Line, Line, Close, Line, Move, Line],
            [0, 0, 10, 0, 10, 10, 20, 20, 50, 50, 60, 60]);

        Assert.AreEqual(3, figures.Count);
        Assert.IsTrue(figures[0].Closed);
        // A segment after a Close continues from the start of the closed figure.
        CollectionAssert.AreEqual(new[] { new SvgVec(0, 0), new SvgVec(20, 20) }, figures[1].Points);
        Assert.IsFalse(figures[1].Closed);
        Assert.IsFalse(figures[2].Closed);
    }

    // ---- stroke ----

    private static List<SvgPolyline> Wide() => Flatten([Move, Line], [-1000, 0, 1000, 0]);

    [TestMethod]
    public void Stroke_window_inside_the_body_of_a_wide_segment_is_inside()
    {
        Assert.AreEqual(SvgCoverage.Inside, Stroke(Wide(), Window(-10, -10), halfWidth: 50));
        // Corners at most 49 from the line, within halfWidth minus the tolerance (about 49.17).
        Assert.AreEqual(SvgCoverage.Inside, Stroke(Wide(), new SvgRectD(0, 0, 20, 49), halfWidth: 50));
    }

    [TestMethod]
    public void Stroke_window_beyond_the_reach_is_outside()
    {
        Assert.AreEqual(SvgCoverage.Outside, Stroke(Wide(), Window(490, 490), halfWidth: 50));
        // 51 from the line, past the reach plus tolerance (50.44).
        Assert.AreEqual(SvgCoverage.Outside, Stroke(Wide(), Window(0, 51), halfWidth: 50));
    }

    [TestMethod]
    public void Stroke_window_within_the_reach_or_straddling_an_edge_is_partial()
    {
        Assert.AreEqual(SvgCoverage.Partial, Stroke(Wide(), Window(0, 40), halfWidth: 50), "straddles the edge");
        Assert.AreEqual(SvgCoverage.Partial, Stroke(Wide(), Window(0, 50.2), halfWidth: 50), "inside the tolerance margin of the reach");
        Assert.AreEqual(SvgCoverage.Partial, Stroke(Wide(), new SvgRectD(0, 0, 20, 49.8), halfWidth: 50), "corners too close to the edge");
    }

    [TestMethod]
    public void Stroke_window_with_a_corner_past_the_end_of_the_segment_is_not_inside()
    {
        var figures = Flatten([Move, Line], [0, 0, 100, 0]);

        Assert.AreEqual(SvgCoverage.Inside, Stroke(figures, new SvgRectD(40, -5, 60, 5), halfWidth: 50));
        Assert.AreEqual(SvgCoverage.Partial, Stroke(figures, new SvgRectD(90, -5, 110, 5), halfWidth: 50));
    }

    [TestMethod]
    public void Stroke_outset_for_caps_and_miters_widens_the_reach()
    {
        var figures = Wide();
        var view = Window(0, 60);

        // Nothing reaches 60 away with a plain half width, but a miter-widened reach of 75 does.
        Assert.AreEqual(SvgCoverage.Outside, SvgUniformCoverage.ClassifyStroke(figures, view, Tolerance(view), 50, outset: 50));
        Assert.AreEqual(SvgCoverage.Partial, SvgUniformCoverage.ClassifyStroke(figures, view, Tolerance(view), 50, outset: 75));
    }

    [TestMethod]
    public void Stroke_includes_the_closing_segment_of_a_closed_figure_only()
    {
        // Two sides of a triangle; the diagonal back to the start exists only when the figure is closed.
        double[] points = [0, 0, 1000, 0, 1000, 1000];
        var open = Flatten([Move, Line, Line], points);
        var closed = Flatten([Move, Line, Line, Close], points);
        var view = Window(490, 490);

        Assert.AreEqual(SvgCoverage.Outside, Stroke(open, view, halfWidth: 50));
        Assert.AreEqual(SvgCoverage.Inside, Stroke(closed, view, halfWidth: 50));
    }

    [TestMethod]
    public void Stroke_of_nothing_is_outside()
    {
        Assert.AreEqual(SvgCoverage.Outside, Stroke([], Window(0, 0), halfWidth: 50));
    }

    // ---- primitives ----

    [TestMethod]
    public void Segment_rect_intersection_covers_crossing_inside_touching_and_missing()
    {
        var r = new SvgRectD(0, 0, 10, 10);

        Assert.IsTrue(SvgUniformCoverage.SegmentIntersectsRect(new(-5, 5), new(15, 5), r), "crosses");
        Assert.IsTrue(SvgUniformCoverage.SegmentIntersectsRect(new(2, 2), new(3, 3), r), "inside");
        Assert.IsTrue(SvgUniformCoverage.SegmentIntersectsRect(new(10, 5), new(20, 5), r), "touches the edge");
        Assert.IsTrue(SvgUniformCoverage.SegmentIntersectsRect(new(-5, 5), new(5, 15), r), "diagonal across a corner");
        Assert.IsFalse(SvgUniformCoverage.SegmentIntersectsRect(new(-5, 12), new(12, 15), r), "passes above");
        Assert.IsFalse(SvgUniformCoverage.SegmentIntersectsRect(new(-5, 6), new(6, 17), r), "cuts the corner without touching");
        Assert.IsFalse(SvgUniformCoverage.SegmentIntersectsRect(new(11, 0), new(11, 10), r), "parallel to an edge");
    }

    [TestMethod]
    public void Distances_to_a_rect()
    {
        var r = new SvgRectD(0, 0, 10, 10);

        Assert.AreEqual(0, SvgUniformCoverage.DistanceSegmentToRect(new(-5, 5), new(15, 5), r), 1e-12);
        Assert.AreEqual(5, SvgUniformCoverage.DistanceSegmentToRect(new(-5, 15), new(20, 15), r), 1e-12);
        Assert.AreEqual(5, SvgUniformCoverage.DistanceSegmentToRect(new(13, 14), new(20, 20), r), 1e-12, "corner to endpoint");
        // Nearest to the rect's corner (10, 10) at the segment's interior point (11, 11).
        Assert.AreEqual(Math.Sqrt(2), SvgUniformCoverage.DistanceSegmentToRect(new(20, 2), new(2, 20), r), 1e-12);
        Assert.AreEqual(5, SvgUniformCoverage.DistancePointToSegment(new(5, 5), new(0, 10), new(10, 10)), 1e-12);
        Assert.AreEqual(3, SvgUniformCoverage.DistancePointToSegment(new(-3, 0), new(0, 0), new(10, 0)), 1e-12, "beyond the end");
    }

    [TestMethod]
    public void Winding_number_counts_direction()
    {
        var ccw = Square(0, 100);
        var cw = Square(0, 100, reversed: true);

        Assert.AreEqual(-WindingAt(cw, 50, 50), WindingAt(ccw, 50, 50));
        Assert.AreEqual(1, Math.Abs(WindingAt(ccw, 50, 50)));
        Assert.AreEqual(0, WindingAt(ccw, 150, 50));
        Assert.AreEqual(2, Math.Abs(WindingAt(ccw.Concat(ccw).ToList(), 50, 50)));
    }

    private static int WindingAt(List<SvgPolyline> figures, double x, double y) =>
        SvgUniformCoverage.WindingNumber(figures, new SvgVec(x, y));

    // ---- double-precision view transform ----

    [TestMethod]
    public void Compose_view_maps_the_image_centre_to_the_pan_position()
    {
        foreach (var rotation in new[] { 0, 90, 180, 270, 30 })
        {
            var m = SvgAffine.ComposeView(100, 50, 2e5, rotation, 640.25, 360.75, snapTranslation: false);

            var centre = m.Transform(new SvgVec(50, 25));
            Assert.AreEqual(640.25, centre.X, 1e-6, $"rotation {rotation}");
            Assert.AreEqual(360.75, centre.Y, 1e-6, $"rotation {rotation}");
        }
    }

    [TestMethod]
    public void Compose_view_rotates_by_quarter_turns_exactly_and_scales_about_the_centre()
    {
        var upright = SvgAffine.ComposeView(100, 50, 2e5, 0, 0, 0, snapTranslation: false);
        var quarter = SvgAffine.ComposeView(100, 50, 2e5, 90, 0, 0, snapTranslation: false);

        // 50 image pixels right of the centre is 1e7 screen pixels right, or (rotated by 90 degrees) 1e7 down.
        Assert.AreEqual(new SvgVec(1e7, 0), upright.Transform(new SvgVec(100, 25)));
        Assert.AreEqual(new SvgVec(0, 1e7), quarter.Transform(new SvgVec(100, 25)));
    }

    [TestMethod]
    public void Compose_view_snaps_only_the_final_translation_when_asked()
    {
        var snapped = SvgAffine.ComposeView(100, 50, 2e5, 0, 500.25, 300.75, snapTranslation: true);
        var smooth = SvgAffine.ComposeView(100, 50, 2e5, 0, 500.25, 300.75, snapTranslation: false);

        // -50 * 2e5 + 500.25 and -25 * 2e5 + 300.75.
        Assert.AreEqual(-9999499.75, smooth.M31);
        Assert.AreEqual(-4999699.25, smooth.M32);
        Assert.AreEqual(-9999500, snapped.M31);
        Assert.AreEqual(-4999699, snapped.M32);
        Assert.AreEqual(smooth.M11, snapped.M11);
    }

    [TestMethod]
    public void Affine_inverse_round_trips_at_extreme_zoom()
    {
        var m = SvgAffine.Scale(2e5, 2e5) * SvgAffine.Rotation(90) * SvgAffine.Translation(-1.2e8, -5.5e7);

        Assert.IsTrue(m.TryInvert(out var inverse));
        var back = inverse.Transform(m.Transform(new SvgVec(123.456, 78.9)));
        Assert.AreEqual(123.456, back.X, 1e-9);
        Assert.AreEqual(78.9, back.Y, 1e-9);
        Assert.IsFalse(SvgAffine.Scale(0, 1).TryInvert(out _));
    }
}
