using FlyPhotos.Display.ImageRendering;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class SvgPathChopperTests
{
    private const byte Move = 0, Line = 1, Quad = 2, Cubic = 3, Close = 4;

    // Output coordinates are clamped into this square grown by its side, -100..200; tests about which segments are
    // kept use points inside that box.
    private static readonly SvgVec CullMin = new(0, 0);
    private static readonly SvgVec CullMax = new(100, 100);

    /// <summary>Chops against the 0..100 square; <paramref name="drop"/> is the solid stroke mode, otherwise a fill.</summary>
    private static SvgPathData? Chop(byte[] verbs, float[] points, bool drop) =>
        SvgPathChopper.Chop(verbs, points, CullMin, CullMax, drop ? SvgChopMode.Stroke : SvgChopMode.Fill);

    [TestMethod]
    public void Fill_replaces_a_cubic_outside_the_view_with_a_line_to_its_end()
    {
        byte[] verbs = [Move, Cubic, Line];
        float[] points = [120, 120, 130, 150, 160, 150, 190, 190, 50, 50];

        var result = Chop(verbs, points, drop: false)!.Value;

        CollectionAssert.AreEqual(new[] { Move, Line, Line }, result.Verbs);
        CollectionAssert.AreEqual(new double[] { 120, 120, 190, 190, 50, 50 }, result.Points);
    }

    [TestMethod]
    public void Fill_keeps_lines_and_closes_the_figure()
    {
        byte[] verbs = [Move, Line, Line, Close];
        float[] points = [-50, -50, 150, -50, 150, 150];

        var result = Chop(verbs, points, drop: false)!.Value;

        CollectionAssert.AreEqual(verbs, result.Verbs);
        CollectionAssert.AreEqual(Doubles(points), result.Points);
    }

    [TestMethod]
    public void Fill_continues_after_a_close_from_the_figure_start()
    {
        byte[] verbs = [Move, Line, Line, Close, Line];
        float[] points = [10, 10, 20, 10, 20, 20, 30, 30];

        var result = Chop(verbs, points, drop: false)!.Value;

        CollectionAssert.AreEqual(new[] { Move, Line, Line, Close, Move, Line }, result.Verbs);
        CollectionAssert.AreEqual(new double[] { 10, 10, 20, 10, 20, 20, 10, 10, 30, 30 }, result.Points);
    }

    [TestMethod]
    public void Stroke_drops_segments_outside_and_restarts_the_figure_after_them()
    {
        byte[] verbs = [Move, Line, Line, Line, Line, Line];
        float[] points = [10, 10, 20, 20, 150, 10, 150, 150, 180, 150, 50, 50];

        var result = Chop(verbs, points, drop: true)!.Value;

        // The two segments past (150, 10) miss the view; the last one touches it again and starts a new figure.
        CollectionAssert.AreEqual(new[] { Move, Line, Line, Move, Line }, result.Verbs);
        CollectionAssert.AreEqual(new double[] { 10, 10, 20, 20, 150, 10, 180, 150, 50, 50 }, result.Points);
    }

    [TestMethod]
    public void Stroke_entirely_outside_the_view_leaves_nothing()
    {
        byte[] verbs = [Move, Cubic, Line];
        float[] points = [200, 200, 250, 300, 400, 300, 500, 500, 600, 700];

        Assert.IsNull(Chop(verbs, points, drop: true));
    }

    [TestMethod]
    public void A_lone_move_leaves_nothing()
    {
        Assert.IsNull(Chop([Move, Move], [10, 10, 20, 20], drop: false));
    }

    [TestMethod]
    public void Dashed_stroke_mode_keeps_outside_lines_and_shortens_outside_curves()
    {
        // The outside lines stay unclamped, even far away; the outside cubic becomes a line.
        byte[] verbs = [Move, Line, Line, Cubic];
        float[] points = [10, 10, 500, 10, 500, 500, 600, 600, 700, 600, 800, 800];

        var result = SvgPathChopper.Chop(verbs, points, CullMin, CullMax, SvgChopMode.DashedStroke)!.Value;

        CollectionAssert.AreEqual(new[] { Move, Line, Line, Line }, result.Verbs);
        CollectionAssert.AreEqual(new double[] { 10, 10, 500, 10, 500, 500, 800, 800 }, result.Points);
    }

    [TestMethod]
    public void A_cubic_partly_in_view_is_split_and_the_outside_pieces_become_lines_in_a_fill()
    {
        byte[] verbs = [Move, Cubic];
        float[] points = [50, 50, 50, 200, 200, 200, 200, 50];

        var result = Chop(verbs, points, drop: false)!.Value;

        Assert.AreEqual(Move, result.Verbs[0]);
        Assert.IsTrue(result.Verbs.Contains(Cubic), "pieces in view stay curves");
        Assert.IsTrue(result.Verbs.Contains(Line), "pieces outside the view become lines");
        // The chain is continuous and ends where the source curve ends.
        Assert.AreEqual(1, result.Verbs.Count(v => v == Move));
        CollectionAssert.AreEqual(new double[] { 50, 50 }, result.Points[..2]);
        CollectionAssert.AreEqual(new double[] { 200, 50 }, result.Points[^2..]);
    }

    [TestMethod]
    public void A_cubic_partly_in_view_keeps_only_the_pieces_touching_the_view_in_a_stroke()
    {
        byte[] verbs = [Move, Cubic];
        float[] points = [50, 50, 50, 2000, 2000, 2000, 2000, 50];

        var result = Chop(verbs, points, drop: true)!.Value;

        Assert.AreEqual(Move, result.Verbs[0]);
        CollectionAssert.AreNotEqual(new double[] { 2000, 50 }, result.Points[^2..], "the curve is split, its far end dropped");
        Assert.IsTrue(result.Verbs.Skip(1).All(v => v == Cubic), "only one run of curve pieces remains, no lines");
        // Chain the pieces: each starts where the previous one ended, and its control box touches the view.
        var start = new SvgVec(result.Points[0], result.Points[1]);
        for (var i = 1; i < result.Verbs.Length; i++)
        {
            var p = 2 + 6 * (i - 1);
            SvgVec[] piece = [start, At(result.Points, p), At(result.Points, p + 2), At(result.Points, p + 4)];
            Assert.IsTrue(piece.Min(v => v.X) <= 100 && piece.Max(v => v.X) >= 0
                          && piece.Min(v => v.Y) <= 100 && piece.Max(v => v.Y) >= 0, "a kept piece must touch the view");
            start = piece[3];
        }
    }

    private static SvgVec At(double[] points, int index) => new(points[index], points[index + 1]);

    private static double[] Doubles(float[] values) => values.Select(v => (double)v).ToArray();

    [TestMethod]
    public void A_quad_completely_in_view_is_kept_as_is()
    {
        byte[] verbs = [Move, Quad];
        float[] points = [10, 10, 50, 90, 90, 10];

        var result = Chop(verbs, points, drop: true)!.Value;

        CollectionAssert.AreEqual(verbs, result.Verbs);
        CollectionAssert.AreEqual(Doubles(points), result.Points);
    }

    [TestMethod]
    public void A_curve_covering_the_view_gives_a_bounded_number_of_pieces()
    {
        // The hull covers the view, so the pieces through it are split down to view size, and no further.
        byte[] verbs = [Move, Cubic];
        float[] points = [-1000, -1000, -1000, 1000, 1000, -1000, 1000, 1000];

        var result = Chop(verbs, points, drop: false)!.Value;

        Assert.IsTrue(result.Verbs.Length < 5000);
    }

    [TestMethod]
    public void A_huge_curve_crossing_the_view_is_split_into_view_sized_pieces_only_near_the_view()
    {
        // 2e7 wide against a 100-wide view: the extreme-zoom case. A fixed split depth of 10 left a kept piece
        // ~20000 wide here, which Direct2D froze widening into a stroke.
        byte[] verbs = [Move, Cubic];
        float[] points = [-1e7f, 50, -3e6f, 60, 3e6f, 40, 1e7f, 50];

        var result = Chop(verbs, points, drop: true)!.Value;

        Assert.IsTrue(result.Verbs.Skip(1).All(v => v == Cubic), "the kept pieces stay curves");
        Assert.IsTrue(result.Verbs.Length < 20, $"only pieces near the view are kept, got {result.Verbs.Length - 1}");
        var start = new SvgVec(result.Points[0], result.Points[1]);
        for (var i = 1; i < result.Verbs.Length; i++)
        {
            var p = 2 + 6 * (i - 1);
            SvgVec[] piece = [start, At(result.Points, p), At(result.Points, p + 2), At(result.Points, p + 4)];
            Assert.IsTrue(piece.Max(v => v.X) - piece.Min(v => v.X) <= 100, "a kept piece is no wider than the view");
            start = piece[3];
        }
    }

    [TestMethod]
    public void A_closed_figure_fully_in_view_stays_closed_in_a_stroke()
    {
        byte[] verbs = [Move, Line, Line, Line, Close];
        float[] points = [10, 10, 90, 10, 90, 90, 10, 90];

        var result = Chop(verbs, points, drop: true)!.Value;

        CollectionAssert.AreEqual(verbs, result.Verbs);
        CollectionAssert.AreEqual(Doubles(points), result.Points);
    }

    [TestMethod]
    public void A_figure_broken_by_a_dropped_segment_ends_open_but_keeps_its_closing_line_in_view()
    {
        // The edge (150,150)-(190,190) misses the view; the closing line (190,190)-(10,10) crosses it.
        byte[] verbs = [Move, Line, Line, Line, Close];
        float[] points = [10, 10, 90, 10, 150, 150, 190, 190];

        var result = Chop(verbs, points, drop: true)!.Value;

        CollectionAssert.AreEqual(new[] { Move, Line, Line, Move, Line }, result.Verbs);
        CollectionAssert.AreEqual(new double[] { 10, 10, 90, 10, 150, 150, 190, 190, 10, 10 }, result.Points);
    }

    [TestMethod]
    public void A_figure_whose_closing_line_is_outside_the_view_ends_open()
    {
        // All edges touch the view, but the closing line (200,60)-(200,50) does not.
        byte[] verbs = [Move, Line, Line, Close];
        float[] points = [200, 50, 50, 50, 200, 60];

        var result = Chop(verbs, points, drop: true)!.Value;

        CollectionAssert.AreEqual(new[] { Move, Line, Line }, result.Verbs);
        CollectionAssert.AreEqual(Doubles(points), result.Points);
    }

    [TestMethod]
    public void A_long_stroke_line_through_the_view_is_cut_at_the_box()
    {
        // The extreme-zoom case: end points ~1e8 away, which Direct2D's float transform can't place exactly.
        byte[] verbs = [Move, Line];
        float[] points = [-1e8f, 50, 1e8f, 50];

        var result = Chop(verbs, points, drop: true)!.Value;

        CollectionAssert.AreEqual(new[] { Move, Line, Line, Line }, result.Verbs);
        CollectionAssert.AreEqual(new double[] { -100, 50, -100, 50, 200, 50, 200, 50 }, result.Points);
    }

    [TestMethod]
    public void A_fill_with_far_away_points_is_clamped_into_the_box_with_the_same_winding_inside_it()
    {
        // Two figures, one closed explicitly and one left open (implicitly closed by the fill), with edges crossing
        // the box diagonally, passing its corners and lying wholly outside it.
        byte[] verbs = [Move, Line, Line, Line, Close, Move, Line, Line, Line];
        float[] points =
        [
            -1e7f, -3e6f, 2e7f, 40, 50, 1e7f, -5e6f, 80,
            150, -1e6f, 3e6f, 3e6f, -2e6f, 160, 60, 30
        ];

        var result = Chop(verbs, points, drop: false)!.Value;

        Assert.IsTrue(result.Points.All(v => v >= -100 && v <= 200), "every output coordinate is in the box");
        var original = SvgUniformCoverage.Flatten(verbs, Doubles(points), 1);
        var chopped = SvgUniformCoverage.Flatten(result.Verbs, result.Points, 1);
        // Sample the box on a grid offset so no sample lies on an edge.
        for (var x = -99.3; x < 200; x += 7.1)
        for (var y = -98.7; y < 200; y += 6.9)
        {
            var p = new SvgVec(x, y);
            Assert.AreEqual(SvgUniformCoverage.WindingNumber(original, p), SvgUniformCoverage.WindingNumber(chopped, p),
                $"winding at ({x:F1}, {y:F1})");
        }
    }
}
