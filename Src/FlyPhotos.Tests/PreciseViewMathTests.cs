using System.Numerics;
using FlyPhotos.Display.ImageRendering;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class PreciseViewMathTests
{
    /// <summary>CanvasViewState.ComposeUnsnapped, in float as the app does it.</summary>
    private static Matrix3x2 FloatView(float w, float h, float scale, int rotation, float panX, float panY) =>
        Matrix3x2.CreateTranslation(-w * 0.5f, -h * 0.5f) * Matrix3x2.CreateScale(scale)
        * Matrix3x2.CreateRotation((float)(Math.PI * rotation / 180f)) * Matrix3x2.CreateTranslation(panX, panY);

    [TestMethod]
    [DataRow(0)]
    [DataRow(90)]
    [DataRow(180)]
    [DataRow(270)]
    [DataRow(30)]
    public void Compose_matches_the_float_view_at_normal_zoom(int rotation)
    {
        var view = PreciseView.Compose(4000, 3000, 0.25, rotation, 960, 540, snapTranslation: false);
        var reference = FloatView(4000, 3000, 0.25f, rotation, 960, 540);

        foreach (var (x, y) in new[] { (0.0, 0.0), (4000.0, 0.0), (1234.5, 2345.5), (4000.0, 3000.0) })
        {
            var (sx, sy) = view.ToScreen(x, y);
            var expected = Vector2.Transform(new Vector2((float)x, (float)y), reference);
            Assert.AreEqual(expected.X, sx, 0.01, $"x of ({x}, {y})");
            Assert.AreEqual(expected.Y, sy, 0.01, $"y of ({x}, {y})");
        }
    }

    [TestMethod]
    public void Snapping_rounds_the_translation_to_whole_pixels()
    {
        var view = PreciseView.Compose(4001, 3001, 1, 0, 960.3, 540.7, snapTranslation: true);

        Assert.AreEqual(Math.Round(view.OffsetX), view.OffsetX);
        Assert.AreEqual(Math.Round(view.OffsetY), view.OffsetY);
    }

    [TestMethod]
    public void ToImage_inverts_ToScreen_at_extreme_zoom()
    {
        var view = PreciseView.Compose(6000, 4000, 2e7, 90, -3.7e10, 2.1e10, snapTranslation: false);
        var (sx, sy) = view.ToScreen(1234.000123, 2345.000456);

        var (x, y) = view.ToImage(sx, sy);

        Assert.AreEqual(1234.000123, x, 1e-9);
        Assert.AreEqual(2345.000456, y, 1e-9);
    }

    [TestMethod]
    public void The_visible_area_at_extreme_zoom_is_a_sliver_of_one_pixel_around_the_point_on_screen()
    {
        // Put image point (1234.5, 2345.5) at the canvas centre, at 2,000,000,000%.
        const double scale = 2e7;
        var panX = 960 - (1234.5 - 3000) * scale;
        var panY = 540 - (2345.5 - 2000) * scale;
        var view = PreciseView.Compose(6000, 4000, scale, 0, panX, panY, snapTranslation: false);

        var visible = view.VisibleArea(1920, 1080, 6000, 4000);

        Assert.IsFalse(visible.IsEmpty);
        Assert.AreEqual(1922 / scale, visible.Width, 1e-12);
        Assert.AreEqual(1234.5, (visible.MinX + visible.MaxX) / 2, 1e-9);
        Assert.AreEqual(2345.5, (visible.MinY + visible.MaxY) / 2, 1e-9);
    }

    [TestMethod]
    public void The_drawn_region_lands_near_the_screen_however_far_the_image_origin_is()
    {
        // The point of rebasing: the translation handed to Direct2D is the region corner's screen position, which
        // stays small, while the image origin is ~1e10 px away.
        const double scale = 2e7;
        var panX = 960 - (1234.5 - 3000) * scale;
        var panY = 540 - (2345.5 - 2000) * scale;
        var view = PreciseView.Compose(6000, 4000, scale, 0, panX, panY, snapTranslation: false);
        var visible = view.VisibleArea(1920, 1080, 6000, 4000);

        Assert.IsTrue(PreciseRegion.TryClip(new RectD(0, 0, 6000, 4000), new RectD(0, 0, 6000, 4000), visible, 2,
            out var src, out var dest));

        Assert.AreEqual(new RectD(1232, 2343, 1237, 2348), src, "whole source pixels, two beyond the visible one");
        var (cornerX, cornerY) = view.ToScreen(dest.MinX, dest.MinY);
        Assert.IsTrue(Math.Abs(view.OffsetX) > 1e10, "the image origin is far off screen");
        Assert.AreEqual(960 - 2.5 * scale, cornerX, 1e-3);
        Assert.AreEqual(540 - 2.5 * scale, cornerY, 1e-3);
    }

    [TestMethod]
    public void Clip_rounds_out_to_whole_source_pixels_adds_the_margin_and_clamps_to_the_source()
    {
        // A tile: source pixels 2..514 of its bitmap (inside a gutter) drawn at 2x onto image pixels 1024..2048.
        var dest = new RectD(1024, 0, 2048, 1024);
        var src = new RectD(2, 2, 514, 514);
        var visible = new RectD(1030.4, 500.2, 1031.9, 501.1);

        Assert.IsTrue(PreciseRegion.TryClip(dest, src, visible, 2, out var srcRegion, out var destRegion));

        // Visible source x: 2 + 6.4/2 = 5.2 .. 2 + 7.9/2 = 5.95 -> 5..6, minus/plus 2 -> 3..8;
        // y: 2 + 500.2/2 = 252.1 .. 2 + 501.1/2 = 252.55 -> 252..253 -> 250..255.
        Assert.AreEqual(new RectD(3, 250, 8, 255), srcRegion);
        Assert.AreEqual(new RectD(1026, 496, 1036, 506), destRegion);
    }

    [TestMethod]
    public void Clip_clamps_at_the_source_edge()
    {
        var full = new RectD(0, 0, 100, 100);

        Assert.IsTrue(PreciseRegion.TryClip(full, full, new RectD(0.2, 99.5, 0.4, 100), 2, out var src, out var dest));

        Assert.AreEqual(new RectD(0, 97, 3, 100), src);
        Assert.AreEqual(src, dest);
    }

    [TestMethod]
    public void Clip_misses_when_the_part_is_not_visible()
    {
        Assert.IsFalse(PreciseRegion.TryClip(new RectD(0, 0, 512, 512), new RectD(0, 0, 512, 512),
            new RectD(600, 600, 700, 700), 2, out _, out _));
    }

    [TestMethod]
    [DataRow(1e9 + 7.25, 16, 7.25)]
    [DataRow(-3.5, 16, 12.5)]
    [DataRow(32, 16, 0)]
    public void PatternPhase_reduces_an_offset_into_one_period(double offset, double period, double expected)
    {
        Assert.AreEqual(expected, PreciseRegion.PatternPhase(offset, period), 1e-6);
    }
}
