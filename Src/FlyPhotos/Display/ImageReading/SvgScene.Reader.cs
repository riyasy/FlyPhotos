#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;

namespace FlyPhotos.Display.ImageReading;

internal sealed partial class SvgScene
{
    /// <summary>
    /// A read position in the stream svg_scene.rs writes (little-endian). On its own it reads one path's
    /// segments (geometry is built in parallel, a cursor per path); <see cref="Reader"/> adds the scene state.
    /// </summary>
    private class Cursor(byte[] buf, int pos)
    {
        /// <summary>Read position in <c>buf</c>.</summary>
        protected int _pos = pos;

        /// <summary>One byte.</summary>
        public byte U8() => buf[_pos++];
        /// <summary>Little-endian u32.</summary>
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(_pos)); _pos += 4; return v; }
        /// <summary>Little-endian f32.</summary>
        public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(_pos)); _pos += 4; return v; }
        /// <summary>A byte as a flag (non-zero = true).</summary>
        public bool Bool() => U8() != 0;
        /// <summary>A u32 byte length followed by UTF-8 text.</summary>
        public string Str() { var n = (int)U32(); var s = System.Text.Encoding.UTF8.GetString(buf, _pos, n); _pos += n; return s; }
        /// <summary>Where the next <paramref name="n"/> bytes start in the buffer; skips past them.</summary>
        public int Skip(int n) { var at = _pos; _pos += n; return at; }
        /// <summary>Two f32s as x, y.</summary>
        public Vector2 Vec2() => new(F32(), F32());
        /// <summary>Three f32s as x, y, z.</summary>
        public Vector3 Vec3() => new(F32(), F32(), F32());
        /// <summary>A transform: sx, ky, kx, sy, tx, ty as M11, M12, M21, M22, M31, M32.</summary>
        public Matrix3x2 Matrix() => new(F32(), F32(), F32(), F32(), F32(), F32());
        /// <summary>x, y, width, height.</summary>
        public Rect Rect() => new(F32(), F32(), F32(), F32());
        /// <summary>Three bytes red, green, blue; opaque.</summary>
        public Color Rgb() => Color.FromArgb(255, U8(), U8(), U8());
        /// <summary>RGB bytes followed by an f32 opacity, as the stream writes every colour that has one.</summary>
        public Color Rgba()
        {
            var c = Rgb();
            c.A = (byte)Math.Round(Math.Clamp(F32(), 0, 1) * 255);
            return c;
        }
        /// <summary><paramref name="n"/> f32s.</summary>
        public float[] Floats(int n) { var a = new float[n]; for (var i = 0; i < n; i++) a[i] = F32(); return a; }
    }

    /// <summary>Reads the whole stream and tracks every device resource it creates.</summary>
    private sealed class Reader(byte[] buf, ICanvasResourceCreator rc) : Cursor(buf, 0)
    {
        /// <summary>The device the scene's resources are created on.</summary>
        public readonly ICanvasResourceCreator Rc = rc;
        /// <summary>Every device resource created while reading; becomes the scene's resource list.</summary>
        public readonly List<IDisposable> Resources = [];
        /// <summary>Image nodes, decoded after the whole stream is read.</summary>
        public readonly List<ImageNode> Images = [];
        /// <summary>Paths whose geometry is still to be built, with the stream position of their segments.</summary>
        public readonly List<(PathNode Node, int Pos, bool EvenOdd)> PendingGeometry = [];
        /// <summary>One paint per solid colour: maps reuse a few colours across tens of thousands of paths.</summary>
        public readonly Dictionary<Color, Paint> SolidPaints = [];
        /// <summary>One stroke style per distinct cap, join, miter limit and dash pattern (in stroke widths).</summary>
        public readonly Dictionary<(CanvasCapStyle, CanvasLineJoin, float, string), CanvasStrokeStyle> StrokeStyles = [];

        /// <summary>
        /// Notes where <paramref name="node"/>'s segments start and skips past them; <see cref="LoadAsync"/>
        /// builds the geometry later, in parallel with the other paths'.
        /// </summary>
        public void DeferGeometry(PathNode node, bool evenOdd)
        {
            PendingGeometry.Add((node, _pos, evenOdd));
            var n = U32();
            for (var i = 0; i < n; i++)
            {
                var points = (PathVerb)U8() switch { PathVerb.Close => 0, PathVerb.Quad => 2, PathVerb.Cubic => 3, _ => 1 };
                _pos += points * 8;
            }
        }

        /// <summary>Adds a device resource to the scene's list (disposed with the scene) and returns it.</summary>
        public T Track<T>(T r) where T : IDisposable { Resources.Add(r); return r; }
    }

    /// <summary>
    /// Builds a scene from svg_build_scene's stream (status byte already stripped) and decodes its embedded
    /// raster images. An image that fails to decode is left out (as resvg does). If the build throws, every
    /// device resource created so far is disposed before the exception goes on.
    /// </summary>
    public static async Task<SvgScene> LoadAsync(ICanvasResourceCreator rc, byte[] stream)
    {
        var r = new Reader(stream, rc);
        Task<CanvasBitmap?[]>? decoding = null;
        var geometryTracked = false;
        try
        {
            float w = r.F32(), h = r.F32();
            var root = ReadChildren(r);

            // Decode the embedded images, straight from the stream, concurrently while the geometries are built.
            decoding = Task.WhenAll(r.Images.Select(img => DecodeImage(rc, stream, img.DataPos, img.DataLength)));

            // Building a geometry is one Win2D call per segment, the bulk of loading a big map; paths are
            // independent and Win2D's Direct2D factory is multithreaded, so they are built in parallel.
            var pending = r.PendingGeometry;
            Parallel.For(0, pending.Count, i =>
            {
                var (node, pos, evenOdd) = pending[i];
                node.Geometry = ReadGeometry(new Cursor(stream, pos), rc, evenOdd);
                // Here, not on first draw: painters on two threads (renderer, clipboard) may share the scene.
                node.Bounds = UserBounds(node);
            });
            foreach (var p in pending) r.Track(p.Node.Geometry);
            geometryTracked = true;

            var bitmaps = await decoding;
            decoding = null;
            for (var i = 0; i < bitmaps.Length; i++)
                if (bitmaps[i] is { } bmp) r.Images[i].Bitmap = r.Track(bmp);
            return new SvgScene(w, h, root, r.Resources, rc.Device);
        }
        catch
        {
            // Geometries are tracked only once all are built, so those built before a failure are loose.
            if (!geometryTracked)
                foreach (var p in r.PendingGeometry) p.Node.Geometry?.Dispose();
            foreach (var res in r.Resources) res.Dispose();
            // DecodeImage doesn't throw, so this only waits for the decodes still running.
            if (decoding != null)
                foreach (var bmp in await decoding) bmp?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The embedded image at <paramref name="pos"/> (<paramref name="length"/> encoded bytes) in the stream,
    /// decoded at 96 DPI (DIPs = pixels); null if it can't be decoded.
    /// </summary>
    private static async Task<CanvasBitmap?> DecodeImage(ICanvasResourceCreator rc, byte[] stream, int pos, int length)
    {
        try
        {
            using var ms = new MemoryStream(stream, pos, length, writable: false);
            return await CanvasBitmap.LoadAsync(rc, ms.AsRandomAccessStream(), 96f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads nodes until <see cref="NodeTag.End"/>.</summary>
    private static Node[] ReadChildren(Reader r)
    {
        var list = new List<Node>();
        while (true)
        {
            switch ((NodeTag)r.U8())
            {
                case NodeTag.End: return [.. list];
                case NodeTag.Group: list.Add(ReadGroup(r)); break;
                case NodeTag.Path: list.Add(ReadPath(r)); break;
                case NodeTag.Image:
                    var nearest = r.Bool();
                    var length = (int)r.U32();
                    var img = new ImageNode { Nearest = nearest, DataPos = r.Skip(length), DataLength = length };
                    r.Images.Add(img);
                    list.Add(img);
                    break;
                default: throw new InvalidDataException("Bad SVG scene node tag");
            }
        }
    }

    /// <summary>A group: transform, opacity, blend, flags, filter count, then clip, mask, filters and children.</summary>
    private static GroupNode ReadGroup(Reader r)
    {
        var g = new GroupNode { Transform = r.Matrix(), Opacity = r.F32(), Blend = (SvgBlendMode)r.U8() };
        var flags = (GroupFlags)r.U8();
        g.Isolate = flags.HasFlag(GroupFlags.Isolate);
        var filterCount = r.U8();
        if (flags.HasFlag(GroupFlags.Clip)) g.Clip = ReadClip(r);
        if (flags.HasFlag(GroupFlags.ClipRect)) g.ClipRect = r.Rect();
        if (flags.HasFlag(GroupFlags.Mask)) g.Mask = ReadMask(r);
        g.Filters = new FilterDef[filterCount];
        for (var i = 0; i < filterCount; i++) g.Filters[i] = ReadFilter(r);
        g.Children = ReadChildren(r);
        g.HasBlend = ComputeHasBlend(g.Children);
        return g;
    }

    /// <summary>A clip path: transform, optional nested clip, children.</summary>
    private static ClipDef ReadClip(Reader r)
    {
        var c = new ClipDef { Transform = r.Matrix() };
        if (r.Bool()) c.Nested = ReadClip(r);
        c.Children = ReadChildren(r);
        return c;
    }

    /// <summary>A mask: region, alpha flag, optional nested mask, children.</summary>
    private static MaskDef ReadMask(Reader r)
    {
        var m = new MaskDef { Rect = r.Rect(), Alpha = r.Bool() };
        if (r.Bool()) m.Nested = ReadMask(r);
        m.Children = ReadChildren(r);
        return m;
    }

    /// <summary>A path: flags, geometry, then the fill and stroke the flags announce.</summary>
    private static PathNode ReadPath(Reader r)
    {
        var flags = (PathFlags)r.U8();
        var node = new PathNode
        {
            StrokeFirst = flags.HasFlag(PathFlags.StrokeFirst), Antialias = flags.HasFlag(PathFlags.Antialias),
        };
        r.DeferGeometry(node, flags.HasFlag(PathFlags.EvenOdd));
        Paint? fill = null, stroke = null;
        float strokeWidth = 0;
        CanvasStrokeStyle? style = null;
        if (flags.HasFlag(PathFlags.Fill)) fill = ReadPaint(r);
        if (flags.HasFlag(PathFlags.Stroke))
        {
            stroke = ReadPaint(r);
            strokeWidth = r.F32();
            var cap = (LineCapTag)r.U8() switch
            {
                LineCapTag.Round => CanvasCapStyle.Round, LineCapTag.Square => CanvasCapStyle.Square, _ => CanvasCapStyle.Flat,
            };
            var join = (LineJoinTag)r.U8() switch
            {
                LineJoinTag.Round => CanvasLineJoin.Round, LineJoinTag.Bevel => CanvasLineJoin.Bevel, _ => CanvasLineJoin.MiterOrBevel,
            };
            var miter = r.F32();
            var dashes = r.Floats((int)r.U32());
            var dashOffset = r.F32();
            // Win2D measures dashes in stroke widths; SVG in user units.
            var dashed = dashes.Length > 0 && strokeWidth > 0;
            if (dashed)
            {
                for (var i = 0; i < dashes.Length; i++) dashes[i] /= strokeWidth;
                dashOffset /= strokeWidth;
            }
            var key = (cap, join, miter, dashed ? $"{string.Join(',', dashes)}@{dashOffset}" : "");
            if (!r.StrokeStyles.TryGetValue(key, out style))
            {
                style = r.Track(new CanvasStrokeStyle
                {
                    StartCap = cap, EndCap = cap, DashCap = cap, LineJoin = join, MiterLimit = miter,
                });
                if (dashed)
                {
                    style.CustomDashStyle = dashes;
                    style.DashOffset = dashOffset;
                }
                r.StrokeStyles[key] = style;
            }
        }
        node.Fill = fill;
        node.Stroke = stroke;
        node.StrokeWidth = strokeWidth;
        node.StrokeStyle = style;
        return node;
    }

    /// <summary>Reads a path's segments straight into a geometry.</summary>
    private static CanvasGeometry ReadGeometry(Cursor r, ICanvasResourceCreator rc, bool evenOdd)
    {
        using var pb = new CanvasPathBuilder(rc);
        pb.SetFilledRegionDetermination(evenOdd ? CanvasFilledRegionDetermination.Alternate : CanvasFilledRegionDetermination.Winding);
        var open = false;
        Vector2 start = default, last = default;
        var n = (int)r.U32();
        for (var i = 0; i < n; i++)
        {
            var verb = (PathVerb)r.U8();
            if (verb == PathVerb.Close)
            {
                if (open) pb.EndFigure(CanvasFigureLoop.Closed);
                open = false;
                last = start;
                continue;
            }
            var a = r.Vec2();
            if (verb == PathVerb.Move)
            {
                if (open) pb.EndFigure(CanvasFigureLoop.Open);
                pb.BeginFigure(a);
                open = true;
                start = last = a;
                continue;
            }
            if (!open) { pb.BeginFigure(last); open = true; start = last; } // drawing on after a close
            switch (verb)
            {
                case PathVerb.Line:
                    pb.AddLine(a); last = a;
                    break;
                case PathVerb.Quad:
                    var q = r.Vec2();
                    pb.AddQuadraticBezier(a, q); last = q;
                    break;
                case PathVerb.Cubic:
                    var c1 = r.Vec2();
                    var c2 = r.Vec2();
                    pb.AddCubicBezier(a, c1, c2); last = c2;
                    break;
            }
        }
        if (open) pb.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>A paint followed by its opacity.</summary>
    private static Paint ReadPaint(Reader r)
    {
        var tag = (PaintTag)r.U8();
        switch (tag)
        {
            case PaintTag.Solid:
                var color = r.Rgba();
                if (!r.SolidPaints.TryGetValue(color, out var solid))
                    r.SolidPaints[color] = solid = new Paint { Brush = r.Track(new CanvasSolidColorBrush(r.Rc, color)) };
                return solid;
            case PaintTag.Linear:
            case PaintTag.Radial:
            {
                // Linear: x1, y1, x2, y2. Radial: cx, cy, r, fx, fy, fr.
                var geo = r.Floats(tag == PaintTag.Linear ? 4 : 6);
                var ts = r.Matrix();
                var edge = (SpreadTag)r.U8() switch
                {
                    SpreadTag.Reflect => CanvasEdgeBehavior.Mirror, SpreadTag.Repeat => CanvasEdgeBehavior.Wrap, _ => CanvasEdgeBehavior.Clamp,
                };
                var stops = new CanvasGradientStop[r.U32()];
                for (var i = 0; i < stops.Length; i++) stops[i] = new CanvasGradientStop { Position = r.F32(), Color = r.Rgba() };
                var opacity = r.F32();
                if (opacity < 1f)
                    for (var i = 0; i < stops.Length; i++) stops[i].Color.A = (byte)Math.Round(stops[i].Color.A * opacity);

                ICanvasBrush brush = tag == PaintTag.Linear
                    ? new CanvasLinearGradientBrush(r.Rc, stops, edge, CanvasAlphaMode.Straight)
                    {
                        StartPoint = new Vector2(geo[0], geo[1]), EndPoint = new Vector2(geo[2], geo[3]),
                    }
                    // Focal radius (geo[5]) is ignored: Direct2D radial gradients have none.
                    : new CanvasRadialGradientBrush(r.Rc, stops, edge, CanvasAlphaMode.Straight)
                    {
                        Center = new Vector2(geo[0], geo[1]), RadiusX = geo[2], RadiusY = geo[2],
                        OriginOffset = new Vector2(geo[3] - geo[0], geo[4] - geo[1]),
                    };
                brush.Transform = ts;
                return new Paint { Brush = r.Track(brush) };
            }
            case PaintTag.Pattern:
            {
                var p = new PatternDef { Rect = r.Rect(), Transform = r.Matrix() };
                p.Children = ReadChildren(r);
                return new Paint { Pattern = p, Opacity = r.F32() };
            }
            default: throw new InvalidDataException("Bad SVG paint tag");
        }
    }

    /// <summary>A filter: region, then each primitive's subregion, linearRGB flag, result name, tag and parameters.</summary>
    private static FilterDef ReadFilter(Reader r)
    {
        var f = new FilterDef { Region = r.Rect() };
        f.Primitives = new Primitive[r.U32()];
        for (var i = 0; i < f.Primitives.Length; i++)
        {
            var subregion = r.Rect();
            var linear = r.Bool();
            var result = r.Str();
            Primitive p = (FilterTag)r.U8() switch
            {
                FilterTag.Blend => new BlendPrimitive(ReadInput(r), ReadInput(r), (SvgBlendMode)r.U8()),
                FilterTag.ColorMatrix => new ColorMatrixPrimitive(ReadInput(r), r.Floats(20)),
                FilterTag.ComponentTransfer => new ComponentTransferPrimitive(ReadInput(r),
                    [ReadTransfer(r), ReadTransfer(r), ReadTransfer(r), ReadTransfer(r)]),
                FilterTag.Composite => ReadComposite(r),
                FilterTag.DropShadow => new DropShadowPrimitive(ReadInput(r), r.Vec2(), r.Vec2(), r.Rgba()),
                FilterTag.Flood => new FloodPrimitive(r.Rgba()),
                FilterTag.GaussianBlur => new BlurPrimitive(ReadInput(r), r.Vec2()),
                FilterTag.Merge => new MergePrimitive(ReadInputs(r)),
                FilterTag.Morphology => new MorphologyPrimitive(ReadInput(r), r.Bool(), r.Vec2()),
                FilterTag.Offset => new OffsetPrimitive(ReadInput(r), r.Vec2()),
                FilterTag.Turbulence => new TurbulencePrimitive(r.Vec2(), (int)r.U32(), (int)r.U32(), r.Bool(), FractalNoise: !r.Bool()),
                FilterTag.DiffuseLighting => new LightingPrimitive(ReadInput(r), false, r.F32(), r.F32(), 0, r.Rgb(), ReadLight(r)),
                FilterTag.SpecularLighting => new LightingPrimitive(ReadInput(r), true, r.F32(), r.F32(), r.F32(), r.Rgb(), ReadLight(r)),
                _ => throw new InvalidDataException("Bad SVG filter primitive tag"),
            };
            p.Subregion = subregion;
            p.Linear = linear;
            p.Result = result;
            f.Primitives[i] = p;
        }
        return f;
    }

    /// <summary>feComposite: in, in2, operator, and k1..k4 when arithmetic.</summary>
    private static CompositePrimitive ReadComposite(Reader r)
    {
        var in1 = ReadInput(r);
        var in2 = ReadInput(r);
        var op = (CompositeOp)r.U8();
        var k = op == CompositeOp.Arithmetic ? new Vector4(r.F32(), r.F32(), r.F32(), r.F32()) : default;
        return new CompositePrimitive(in1, in2, op, k);
    }

    /// <summary>One feComponentTransfer channel function and its values.</summary>
    private static Transfer ReadTransfer(Reader r)
    {
        var kind = (TransferKind)r.U8();
        return new Transfer(kind, kind switch
        {
            TransferKind.Table or TransferKind.Discrete => r.Floats((int)r.U32()),
            TransferKind.Linear => r.Floats(2),
            TransferKind.Gamma => r.Floats(3),
            _ => [],
        });
    }

    /// <summary>A light source: distant (azimuth, elevation), point (x, y, z) or spot (position, target,
    /// specularExponent, optional limiting cone angle).</summary>
    private static Light ReadLight(Reader r)
    {
        switch ((LightKind)r.U8())
        {
            case LightKind.Distant: return new Light(LightKind.Distant, r.F32(), r.F32(), default, default, 0, null);
            case LightKind.Point: return new Light(LightKind.Point, 0, 0, r.Vec3(), default, 0, null);
            default:
                var pos = r.Vec3();
                var target = r.Vec3();
                var focus = r.F32();
                var hasCone = r.Bool();
                var cone = r.F32();
                return new Light(LightKind.Spot, 0, 0, pos, target, focus, hasCone ? cone : null);
        }
    }

    /// <summary>A primitive input; a reference carries the result name.</summary>
    private static Input ReadInput(Reader r)
    {
        var kind = (InputKind)r.U8();
        return new Input(kind, kind == InputKind.Reference ? r.Str() : "");
    }

    /// <summary>A u32 count followed by that many inputs (feMerge).</summary>
    private static Input[] ReadInputs(Reader r)
    {
        var inputs = new Input[r.U32()];
        for (var i = 0; i < inputs.Length; i++) inputs[i] = ReadInput(r);
        return inputs;
    }
}
