#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;

namespace FlyPhotos.Display.ImageReading;

internal sealed partial class SvgScene
{
    /// <summary>
    /// Draws nodes for one <see cref="Painter"/> step. Every effect, intermediate command list and per-step brush
    /// it creates is kept in <see cref="Transients"/>, which the painter disposes once the step's drawing session
    /// has ended. Filters are in SvgScene.Filters.cs.
    /// </summary>
    private sealed partial class NodeDrawer(ICanvasResourceCreator rc, CanvasImageInterpolation imageQuality)
    {
        /// <summary>Everything this drawer created; disposed after the drawing session that used it.</summary>
        public readonly List<IDisposable> Transients = [];

        /// <summary>Adds an effect, list or brush to <see cref="Transients"/> and returns it.</summary>
        private T Keep<T>(T t) where T : IDisposable { Transients.Add(t); return t; }

        /// <summary><paramref name="img"/> transformed by <paramref name="m"/> (a Transform2DEffect).</summary>
        private ICanvasImage Xform(ICanvasImage img, Matrix3x2 m) => Keep(new Transform2DEffect { Source = img, TransformMatrix = m });

        /// <summary>The image's alpha with black colour (SVG's SourceAlpha).</summary>
        private ICanvasImage AlphaOf(ICanvasImage img) => Keep(new ColorMatrixEffect { Source = img, ColorMatrix = new Matrix5x4 { M44 = 1 } });

        /// <summary>Records <paramref name="nodes"/> into a new command list with user→list transform <paramref name="t"/>.</summary>
        public CanvasCommandList Record(Node[] nodes, Matrix3x2 t)
        {
            var cl = Keep(new CanvasCommandList(rc));
            using var ds = cl.CreateDrawingSession();
            ds.Transform = t;
            DrawNodes(ds, nodes);
            return cl;
        }

        /// <summary>
        /// Draws <paramref name="nodes"/> into <paramref name="ds"/> under its current transform. Lists with no
        /// blended group (through pass-through groups) draw node by node; others go through the blend backdrop.
        /// </summary>
        public void DrawNodes(CanvasDrawingSession ds, Node[] nodes)
        {
            if (!ComputeHasBlend(nodes))
            {
                foreach (var n in nodes) DrawNode(ds, n);
                return;
            }

            // mix-blend-mode blends with everything drawn so far in the nearest isolating group, so pass-through
            // groups are flattened into one list. Runs of normal items are recorded together; each blended group
            // is blended onto what came before. Every piece is recorded in this session's space, then drawn
            // untransformed.
            var items = new List<(Node Node, Matrix3x2 Parent)>();
            Flatten(nodes, ds.Transform, items);
            var t = ds.Transform;
            ICanvasImage? backdrop = null;
            var run = new List<(Node, Matrix3x2)>();
            foreach (var (n, parent) in items)
            {
                if (n is GroupNode { Blend: not SvgBlendMode.Normal } g)
                {
                    backdrop = Over(backdrop, RecordRun(run));
                    run.Clear();
                    var img = GroupImage(g, g.Transform * parent);
                    if (img != null) backdrop = backdrop == null ? img : Blend(backdrop, img, g.Blend);
                }
                else run.Add((n, parent));
            }
            backdrop = Over(backdrop, RecordRun(run));
            if (backdrop == null) return;
            ds.Transform = Matrix3x2.Identity;
            ds.DrawImage(backdrop);
            ds.Transform = t;
        }

        /// <summary>
        /// Appends <paramref name="nodes"/> to <paramref name="items"/> with their parent transform, opening up
        /// pass-through groups that contain a blend so their children share the enclosing backdrop.
        /// </summary>
        private static void Flatten(Node[] nodes, Matrix3x2 parent, List<(Node, Matrix3x2)> items)
        {
            foreach (var n in nodes)
            {
                if (n is GroupNode { IsPassThrough: true, HasBlend: true } g) Flatten(g.Children, g.Transform * parent, items);
                else items.Add((n, parent));
            }
        }

        /// <summary>Records items that each carry their parent's transform; null for none.</summary>
        private CanvasCommandList? RecordRun(List<(Node Node, Matrix3x2 Parent)> run)
        {
            if (run.Count == 0) return null;
            var cl = Keep(new CanvasCommandList(rc));
            using var ds = cl.CreateDrawingSession();
            foreach (var (n, parent) in run)
            {
                ds.Transform = parent;
                DrawNode(ds, n);
            }
            return cl;
        }

        /// <summary>
        /// Draws one node under the session's current transform: a path (fill and stroke in paint order), an
        /// image, or a group (straight in, in an opacity layer, or as an effect image when it has a clip, mask
        /// or filter).
        /// </summary>
        public void DrawNode(CanvasDrawingSession ds, Node node)
        {
            switch (node)
            {
                case PathNode p:
                    ds.Antialiasing = p.Antialias ? CanvasAntialiasing.Antialiased : CanvasAntialiasing.Aliased;
                    if (p.StrokeFirst) { StrokePath(ds, p); FillPath(ds, p); }
                    else { FillPath(ds, p); StrokePath(ds, p); }
                    break;
                case ImageNode { Bitmap: { } bmp } img:
                    var size = bmp.SizeInPixels;
                    ds.DrawImage(bmp, new Rect(0, 0, size.Width, size.Height), bmp.Bounds, 1f,
                        img.Nearest ? CanvasImageInterpolation.NearestNeighbor : imageQuality);
                    break;
                case GroupNode g:
                    var saved = ds.Transform;
                    var t = g.Transform * saved;
                    if (g.NeedsLayerImage)
                    {
                        // The group's image is already in this session's space.
                        var layer = GroupImage(g, t);
                        if (layer != null)
                        {
                            ds.Transform = Matrix3x2.Identity;
                            ds.DrawImage(layer);
                        }
                    }
                    else
                    {
                        ds.Transform = t;
                        // A rectangle clip is a Direct2D layer (an axis-aligned clip when it can be), far cheaper
                        // than the mask effect a clip path needs.
                        if (g.ClipRect is { } clipRect)
                            using (ds.CreateLayer(g.Opacity, clipRect)) DrawNodes(ds, g.Children);
                        else if (g.Opacity < 1f)
                            using (ds.CreateLayer(g.Opacity)) DrawNodes(ds, g.Children);
                        else
                            DrawNodes(ds, g.Children);
                    }
                    ds.Transform = saved;
                    break;
            }
        }

        /// <summary>Fills the path's geometry with its fill paint, if it has one.</summary>
        private void FillPath(CanvasDrawingSession ds, PathNode p)
        {
            if (p.Fill == null) return;
            ds.FillGeometry(p.Geometry, BrushFor(p.Fill, ds.Transform));
        }

        /// <summary>Strokes the path's geometry with its stroke paint and style, if it has a visible stroke.</summary>
        private void StrokePath(CanvasDrawingSession ds, PathNode p)
        {
            if (p.Stroke == null || p.StrokeWidth <= 0) return;
            ds.DrawGeometry(p.Geometry, BrushFor(p.Stroke, ds.Transform), p.StrokeWidth, p.StrokeStyle);
        }

        /// <summary>
        /// The brush for <paramref name="paint"/>: its prebuilt brush, or for a pattern a new image brush whose
        /// tile is recorded at the scale of <paramref name="t"/> (the session's current transform).
        /// </summary>
        private ICanvasBrush BrushFor(Paint paint, Matrix3x2 t)
        {
            if (paint.Brush != null) return paint.Brush;
            // Pattern: record one tile at the current scale so it stays sharp, then tile it with an image brush.
            var pat = paint.Pattern!;
            var s = MathF.Max(MathF.Sqrt(MathF.Abs(t.GetDeterminant())), 1e-6f);
            var tile = Record(pat.Children, Matrix3x2.CreateScale(s));
            return Keep(new CanvasImageBrush(rc, tile)
            {
                SourceRectangle = new Rect(0, 0, pat.Rect.Width * s, pat.Rect.Height * s),
                ExtendX = CanvasEdgeBehavior.Wrap, ExtendY = CanvasEdgeBehavior.Wrap,
                Transform = Matrix3x2.CreateScale(1 / s) * Matrix3x2.CreateTranslation((float)pat.Rect.X, (float)pat.Rect.Y) * pat.Transform,
                Opacity = paint.Opacity,
            });
        }

        /// <summary>
        /// The group drawn as an image in the space <paramref name="t"/> maps to (t = the group's own
        /// user→target transform): content, then filters, clip path, mask and opacity, as resvg does. Null when
        /// nothing would show.
        /// </summary>
        private ICanvasImage? GroupImage(GroupNode g, Matrix3x2 t)
        {
            ICanvasImage content;
            if (g.Filters.Length > 0)
            {
                // Filters run in an axis-aligned pixel space (resvg's approach): split the scale out of t,
                // filter there, then apply the rest (rotation/skew) to the result.
                var sx = MathF.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
                var sy = MathF.Sqrt(t.M21 * t.M21 + t.M22 * t.M22);
                if (sx <= 0 || sy <= 0) return null;
                var rest = new Matrix3x2(t.M11 / sx, t.M12 / sx, t.M21 / sy, t.M22 / sy, 0, 0);
                Matrix3x2.Invert(rest, out var invRest);
                var filterSpace = t * invRest;
                content = Record(g.Children, filterSpace);
                for (var i = 0; i < g.Filters.Length; i++)
                {
                    // The first filter can re-record its source at any resolution; later ones shrink the previous output.
                    var prev = content;
                    Func<float, ICanvasImage> recordSourceAt = i == 0
                        ? scale => Record(g.Children, filterSpace * Matrix3x2.CreateScale(scale))
                        : scale => Xform(prev, Matrix3x2.CreateScale(scale));
                    content = ApplyFilter(g.Filters[i], content, recordSourceAt, filterSpace, sx, sy);
                }
                if (!rest.IsIdentity) content = Xform(content, rest);
            }
            else content = Record(g.Children, t);
            return GroupEffects(g, content, t);
        }

        /// <summary>
        /// <paramref name="content"/>, the group's (filtered) children in the space <paramref name="t"/> maps to,
        /// with the group's clip path, rectangle clip, mask and opacity applied.
        /// </summary>
        public ICanvasImage GroupEffects(GroupNode g, ICanvasImage content, Matrix3x2 t)
        {
            if (g.Clip != null) content = Keep(new AlphaMaskEffect { Source = content, AlphaMask = ClipImage(g.Clip, t) });
            if (g.ClipRect is { } clipRect) content = Keep(new AlphaMaskEffect { Source = content, AlphaMask = RectImage(clipRect, t) });
            if (g.Mask != null) content = ApplyMask(g.Mask, content, t);
            if (g.Opacity < 1f) content = Keep(new OpacityEffect { Source = content, Opacity = g.Opacity });
            return content;
        }

        /// <summary>The clip path's coverage: its children as opaque fills, intersected with any nested clip.</summary>
        private ICanvasImage ClipImage(ClipDef clip, Matrix3x2 t)
        {
            ICanvasImage img = Record(clip.Children, clip.Transform * t);
            if (clip.Nested != null) img = Keep(new AlphaMaskEffect { Source = img, AlphaMask = ClipImage(clip.Nested, t) });
            return img;
        }

        /// <summary>The rectangle <paramref name="r"/> filled opaque under <paramref name="t"/>: a rectangle clip's coverage.</summary>
        private ICanvasImage RectImage(Rect r, Matrix3x2 t)
        {
            var cl = Keep(new CanvasCommandList(rc));
            using var ds = cl.CreateDrawingSession();
            ds.Transform = t;
            ds.FillRectangle(r, Windows.UI.Color.FromArgb(255, 0, 0, 0));
            return cl;
        }

        /// <summary>
        /// <paramref name="content"/> masked by <paramref name="mask"/> (and any nested mask first). The mask
        /// content is clipped to the mask region; a luminance mask turns luminance into coverage.
        /// </summary>
        private ICanvasImage ApplyMask(MaskDef mask, ICanvasImage content, Matrix3x2 t)
        {
            var cl = Keep(new CanvasCommandList(rc));
            using (var ds = cl.CreateDrawingSession())
            {
                ds.Transform = t;
                // The mask only covers its region.
                using var region = CanvasGeometry.CreateRectangle(rc, mask.Rect);
                using (ds.CreateLayer(1f, region)) DrawNodes(ds, mask.Children);
            }
            if (mask.Nested != null) content = ApplyMask(mask.Nested, content, t);

            ICanvasImage coverage = cl;
            if (!mask.Alpha)
            {
                // Luminance mask: alpha = luminance of the premultiplied colour (= luminance × alpha).
                // Straight mode applies the matrix to the stored (premultiplied) values as they are.
                coverage = Keep(new ColorMatrixEffect
                {
                    Source = cl,
                    AlphaMode = CanvasAlphaMode.Straight,
                    ColorMatrix = new Matrix5x4 { M14 = 0.2125f, M24 = 0.7154f, M34 = 0.0721f },
                });
            }
            return Keep(new AlphaMaskEffect { Source = content, AlphaMask = coverage });
        }

        /// <summary><paramref name="above"/> drawn over <paramref name="below"/> (source-over); either may be null.</summary>
        private ICanvasImage? Over(ICanvasImage? below, ICanvasImage? above) =>
            below == null ? above : above == null ? below
            : Keep(new CompositeEffect { Mode = CanvasComposite.SourceOver, Sources = { below, above } });

        /// <summary>
        /// <paramref name="foreground"/> blended onto <paramref name="background"/> with an SVG blend mode (used for
        /// mix-blend-mode and feBlend): normal = source-over, plus-lighter = add, the rest via BlendEffect.
        /// </summary>
        private ICanvasImage Blend(ICanvasImage background, ICanvasImage foreground, SvgBlendMode mode) => mode switch
        {
            SvgBlendMode.Normal => Over(background, foreground)!,
            SvgBlendMode.PlusLighter => Keep(new CompositeEffect { Mode = CanvasComposite.Add, Sources = { background, foreground } }),
            _ => Keep(new BlendEffect { Background = background, Foreground = foreground, Mode = ToBlendEffectMode(mode) }),
        };

        /// <summary>The Direct2D blend mode for a separable/non-separable SVG blend mode (not Normal/PlusLighter).</summary>
        private static BlendEffectMode ToBlendEffectMode(SvgBlendMode mode) => mode switch
        {
            SvgBlendMode.Multiply => BlendEffectMode.Multiply, SvgBlendMode.Screen => BlendEffectMode.Screen,
            SvgBlendMode.Overlay => BlendEffectMode.Overlay, SvgBlendMode.Darken => BlendEffectMode.Darken,
            SvgBlendMode.Lighten => BlendEffectMode.Lighten, SvgBlendMode.ColorDodge => BlendEffectMode.ColorDodge,
            SvgBlendMode.ColorBurn => BlendEffectMode.ColorBurn, SvgBlendMode.HardLight => BlendEffectMode.HardLight,
            SvgBlendMode.SoftLight => BlendEffectMode.SoftLight, SvgBlendMode.Difference => BlendEffectMode.Difference,
            SvgBlendMode.Exclusion => BlendEffectMode.Exclusion, SvgBlendMode.Hue => BlendEffectMode.Hue,
            SvgBlendMode.Saturation => BlendEffectMode.Saturation, SvgBlendMode.Color => BlendEffectMode.Color,
            SvgBlendMode.Luminosity => BlendEffectMode.Luminosity,
            _ => BlendEffectMode.Multiply,
        };
    }
}
