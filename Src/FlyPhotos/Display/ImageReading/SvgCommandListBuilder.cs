#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using NLog;
using Windows.Foundation;
using Windows.UI;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Replays the binary draw-command stream produced by the Rust bridge (<c>resvg_render_svg_with_commands</c>)
/// into a scene of <see cref="SvgDisplayItem"/>s, each a retained Direct2D <see cref="CanvasCommandList"/> with its
/// bounds, which can be drawn at any zoom without re-rasterising and culled against the viewport. All coordinates
/// are in SVG user units; the stream is little-endian.
/// </summary>
/// <remarks>
/// Format: header (magic "FSV1", f32 width, f32 height, u32 totalPoints, u32 totalOps), then ops until END.
/// Any malformed input makes <see cref="Build"/> return null so the caller falls back to the bitmap.
/// </remarks>
internal static class SvgCommandListBuilder
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Above either threshold the vector replay is skipped while the view is animating, to keep zoom/pan smooth.
    private const uint HeavyPointThreshold = 50_000;
    private const uint HeavyOpThreshold = 5_000;

    private const float ClipFlatteningTolerance = 0.001f;

    // Each item's bounds are padded by this fraction of the SVG's larger side (0.1%), against float noise in the bounds.
    private const float BoundsEpsilonFraction = 0.001f;

    private const byte OpEnd = 0;
    private const byte OpPushLayer = 1;
    private const byte OpPopLayer = 2;
    private const byte OpFill = 3;
    private const byte OpStroke = 4;

    /// <summary>
    /// Builds the scene from <paramref name="data"/>. Returns null (after logging) if the stream is malformed
    /// or Win2D rejects it. <paramref name="width"/>/<paramref name="height"/> are the SVG's size in user units.
    /// </summary>
    public static SvgDisplayItem[]? Build(ICanvasResourceCreator creator, ReadOnlySpan<byte> data,
        out float width, out float height, out bool isHeavy)
    {
        width = 0;
        height = 0;
        isHeavy = false;

        var items = new List<SvgDisplayItem>();
        try
        {
            var r = new Reader(data);
            if (r.U32() != 0x31565346) // "FSV1" read as little-endian
                throw new FormatException("Bad magic");
            width = r.F32();
            height = r.F32();
            if (!(width > 0 && height > 0 && float.IsFinite(width) && float.IsFinite(height)))
                throw new FormatException("Bad SVG size");
            uint totalPoints = r.U32();
            uint totalOps = r.U32();
            isHeavy = totalPoints > HeavyPointThreshold || totalOps > HeavyOpThreshold;

            Replay(creator, ref r, items, BoundsEpsilonFraction * Math.Max(width, height));
            return items.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to build SVG scene");
            foreach (var item in items) item.Commands.Dispose();
            return null;
        }
    }

    /// <summary>Splits the stream into top-level items: one per fill/stroke, one per complete top-level layer.</summary>
    private static void Replay(ICanvasResourceCreator creator, ref Reader r, List<SvgDisplayItem> items, float epsilon)
    {
        while (true)
        {
            var op = r.U8();
            switch (op)
            {
                case OpEnd:
                    return;
                case OpPushLayer:
                case OpFill:
                case OpStroke:
                    var item = RecordItem(creator, ref r, op, epsilon);
                    if (item != null) items.Add(item.Value);
                    break;
                case OpPopLayer:
                    throw new FormatException("Pop without push");
                default:
                    throw new FormatException("Unknown opcode");
            }
        }
    }

    /// <summary>
    /// Records the op that follows (whose opcode <paramref name="op"/> was just read) into its own command list.
    /// Returns null when it draws nothing visible, so the caller never has to draw or cull it.
    /// </summary>
    private static SvgDisplayItem? RecordItem(ICanvasResourceCreator creator, ref Reader r, byte op, float epsilon)
    {
        var list = new CanvasCommandList(creator);
        try
        {
            Rect bounds;
            // Kept with a lone fill or stroke so the renderer can chop its path; a layer is always drawn from its list.
            SvgShape? shape = null;
            // The list only becomes drawable once the session that records it is disposed.
            using (var ds = list.CreateDrawingSession())
            {
                ds.Units = CanvasUnits.Pixels;
                switch (op)
                {
                    case OpFill:
                        shape = ReadFill(ref r);
                        bounds = Record(creator, ds, shape);
                        break;
                    case OpStroke:
                        shape = ReadStroke(ref r);
                        bounds = Record(creator, ds, shape);
                        break;
                    default:
                        bounds = ReplayLayer(creator, ds, ref r);
                        break;
                }
            }

            if (bounds.IsEmpty)
            {
                list.Dispose();
                return null;
            }
            // Pad against float noise; antialiasing spill is a device-pixel matter, so the renderer pads the view.
            return new SvgDisplayItem(list, new Rect(bounds.X - epsilon, bounds.Y - epsilon,
                bounds.Width + 2 * epsilon, bounds.Height + 2 * epsilon), shape);
        }
        catch
        {
            list.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Records a top-level layer (its push opcode already read) up to the matching pop, nested layers included.
    /// Returns the bounds of what it draws: the union of its children, cut down to each layer's clip.
    /// </summary>
    private static Rect ReplayLayer(ICanvasResourceCreator creator, CanvasDrawingSession ds, ref Reader r)
    {
        var layers = new Stack<LayerFrame>();
        try
        {
            layers.Push(PushLayer(creator, ds, ref r));
            while (true)
            {
                switch (r.U8())
                {
                    case OpEnd:
                        throw new FormatException("Unbalanced layers");
                    case OpPushLayer:
                        layers.Push(PushLayer(creator, ds, ref r));
                        break;
                    case OpPopLayer:
                    {
                        var frame = layers.Pop();
                        frame.Layer.Dispose();
                        var bounds = frame.Content;
                        if (frame.ClipBounds is { } clipBounds) bounds.Intersect(clipBounds);
                        if (layers.Count == 0) return bounds;
                        layers.Peek().Content.Union(bounds);
                        break;
                    }
                    case OpFill:
                        layers.Peek().Content.Union(Record(creator, ds, ReadFill(ref r)));
                        break;
                    case OpStroke:
                        layers.Peek().Content.Union(Record(creator, ds, ReadStroke(ref r)));
                        break;
                    default:
                        throw new FormatException("Unknown opcode");
                }
            }
        }
        finally
        {
            while (layers.Count > 0) layers.Pop().Layer.Dispose();
        }
    }

    private static LayerFrame PushLayer(ICanvasResourceCreator creator, CanvasDrawingSession ds, ref Reader r)
    {
        float opacity = r.F32();
        uint nClip = r.U32();

        CanvasGeometry? clip = null;
        for (uint i = 0; i < nClip; i++)
        {
            var mat = r.Mat();
            var fillRule = r.U8();
            using var geo = ReadPath(creator, ref r, fillRule);
            var transformed = geo.Transform(mat);
            if (clip == null)
            {
                clip = transformed;
            }
            else
            {
                var union = clip.CombineWith(transformed, Matrix3x2.Identity, CanvasGeometryCombine.Union,
                    ClipFlatteningTolerance);
                clip.Dispose();
                transformed.Dispose();
                clip = union;
            }
        }

        // The clip geometry already has its transform baked in, so the layer must be created with an identity transform.
        ds.Transform = Matrix3x2.Identity;
        var layer = clip == null ? ds.CreateLayer(opacity) : ds.CreateLayer(opacity, clip);
        Rect? clipBounds = clip == null ? null : Sane(clip.ComputeBounds());
        clip?.Dispose();
        return new LayerFrame(layer, clipBounds);
    }

    /// <summary>Reads a fill (its opcode already read) without recording it.</summary>
    private static SvgShape ReadFill(ref Reader r)
    {
        var mat = r.Mat();
        var fillRule = r.U8();
        var aliased = r.U8() != 0;
        var paint = ReadPaint(ref r);
        ReadPathData(ref r, out var verbs, out var points);
        return new SvgShape
        {
            IsStroke = false,
            Transform = mat,
            Verbs = verbs,
            Points = points,
            FillRule = fillRule,
            Aliased = aliased,
            Paint = paint
        };
    }

    /// <summary>Reads a stroke (its opcode already read) without recording it.</summary>
    private static SvgShape ReadStroke(ref Reader r)
    {
        var mat = r.Mat();
        var aliased = r.U8() != 0;
        var paint = ReadPaint(ref r);
        float strokeWidth = r.F32();
        var cap = r.U8() switch
        {
            0 => CanvasCapStyle.Flat,
            1 => CanvasCapStyle.Round,
            2 => CanvasCapStyle.Square,
            _ => throw new FormatException("Bad line cap")
        };
        // Direct2D's Miter clips at the miter limit (SVG 2 miter-clip); MiterOrBevel falls back to a bevel (SVG 1 miter).
        var join = r.U8() switch
        {
            0 => CanvasLineJoin.MiterOrBevel,
            1 => CanvasLineJoin.Miter,
            2 => CanvasLineJoin.Round,
            3 => CanvasLineJoin.Bevel,
            _ => throw new FormatException("Bad line join")
        };
        float miterLimit = r.F32();
        uint nDash = r.U32();
        r.EnsureAvailable(nDash, 4);
        var dash = new float[nDash];
        for (var i = 0; i < dash.Length; i++) dash[i] = r.F32();
        float dashOffset = r.F32();
        ReadPathData(ref r, out var verbs, out var points);
        return new SvgShape
        {
            IsStroke = true,
            Transform = mat,
            Verbs = verbs,
            Points = points,
            FillRule = 0,
            Aliased = aliased,
            Paint = paint,
            Width = strokeWidth,
            Cap = cap,
            Join = join,
            MiterLimit = miterLimit,
            Dashes = dash,
            DashOffset = dashOffset
        };
    }

    /// <summary>Records a fill or stroke; returns its bounds in SVG user space (empty if it draws nothing).</summary>
    private static Rect Record(ICanvasResourceCreator creator, CanvasDrawingSession ds, SvgShape shape)
    {
        using var geo = BuildPath(creator, shape.Verbs, shape.Points, shape.FillRule);

        // A zero or negative width draws nothing (and would divide by zero for dashes).
        if (shape.IsStroke && !(shape.Width > 0)) return Rect.Empty;

        using var paint = CreatePaint(creator, shape.Paint);
        ds.Transform = shape.Transform;
        ds.Antialiasing = shape.Aliased ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (!shape.IsStroke)
        {
            paint.Fill(ds, geo);
            return Sane(geo.ComputeBounds(shape.Transform));
        }

        using var style = CreateStrokeStyle(shape);
        paint.Stroke(ds, geo, shape.Width, style);
        return Sane(geo.ComputeStrokeBounds(shape.Width, style, shape.Transform, CanvasGeometry.DefaultFlatteningTolerance));
    }

    /// <summary>Creates the stroke style of a stroke shape: caps, join, miter limit and dashes.</summary>
    internal static CanvasStrokeStyle CreateStrokeStyle(SvgShape shape)
    {
        var style = new CanvasStrokeStyle
        {
            StartCap = shape.Cap,
            EndCap = shape.Cap,
            DashCap = shape.Cap,
            LineJoin = shape.Join,
            MiterLimit = shape.MiterLimit
        };
        if (shape.Dashes.Length > 0)
        {
            // Direct2D expresses dash lengths and offset in multiples of the stroke width.
            var dash = new float[shape.Dashes.Length];
            for (var i = 0; i < dash.Length; i++) dash[i] = shape.Dashes[i] / shape.Width;
            style.CustomDashStyle = dash;
            style.DashOffset = shape.DashOffset / shape.Width;
        }
        return style;
    }

    /// <summary>Maps the empty or non-finite rects Direct2D reports for degenerate geometry to <see cref="Rect.Empty"/>.</summary>
    private static Rect Sane(Rect rect) =>
        rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)
        || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)
            ? Rect.Empty
            : rect;

    private static SvgPaint ReadPaint(ref Reader r)
    {
        switch (r.U8())
        {
            case 0:
                return new SvgPaint { Kind = SvgPaintKind.Solid, Solid = r.Color() };
            case 1:
            {
                var start = r.Vec2();
                var end = r.Vec2();
                var transform = r.Mat();
                var edge = ReadSpread(ref r);
                float opacity = r.F32();
                var stops = ReadStops(ref r);
                return new SvgPaint
                {
                    Kind = SvgPaintKind.LinearGradient,
                    Start = start,
                    End = end,
                    Transform = transform,
                    Edge = edge,
                    Opacity = opacity,
                    Stops = stops
                };
            }
            case 2:
            {
                var center = r.Vec2();
                float radius = r.F32();
                var focal = r.Vec2();
                var transform = r.Mat();
                var edge = ReadSpread(ref r);
                float opacity = r.F32();
                var stops = ReadStops(ref r);
                return new SvgPaint
                {
                    Kind = SvgPaintKind.RadialGradient,
                    Start = center,
                    End = focal,
                    Radius = radius,
                    Transform = transform,
                    Edge = edge,
                    Opacity = opacity,
                    Stops = stops
                };
            }
            default:
                throw new FormatException("Bad paint kind");
        }
    }

    /// <summary>
    /// Creates the paint's brush (none for a solid color). Gradients are straight-alpha, as the stream's colors are.
    /// The caller disposes the result.
    /// </summary>
    internal static ResolvedPaint CreatePaint(ICanvasResourceCreator creator, SvgPaint paint) => paint.Kind switch
    {
        SvgPaintKind.Solid => new ResolvedPaint(paint.Solid, null),
        SvgPaintKind.LinearGradient => new ResolvedPaint(default,
            new CanvasLinearGradientBrush(creator, paint.Stops, paint.Edge, CanvasAlphaMode.Straight)
            {
                StartPoint = paint.Start,
                EndPoint = paint.End,
                Transform = paint.Transform,
                Opacity = paint.Opacity
            }),
        _ => new ResolvedPaint(default,
            new CanvasRadialGradientBrush(creator, paint.Stops, paint.Edge, CanvasAlphaMode.Straight)
            {
                Center = paint.Start,
                OriginOffset = paint.End - paint.Start,
                RadiusX = paint.Radius,
                RadiusY = paint.Radius,
                Transform = paint.Transform,
                Opacity = paint.Opacity
            })
    };

    private static CanvasEdgeBehavior ReadSpread(ref Reader r) => r.U8() switch
    {
        0 => CanvasEdgeBehavior.Clamp,
        1 => CanvasEdgeBehavior.Mirror,
        2 => CanvasEdgeBehavior.Wrap,
        _ => throw new FormatException("Bad gradient spread")
    };

    private static CanvasGradientStop[] ReadStops(ref Reader r)
    {
        uint n = r.U32();
        if (n == 0) throw new FormatException("Gradient without stops");
        r.EnsureAvailable(n, 20);
        var stops = new CanvasGradientStop[n];
        for (var i = 0; i < stops.Length; i++)
        {
            float offset = r.F32();
            stops[i] = new CanvasGradientStop { Position = offset, Color = r.Color() };
        }
        return stops;
    }

    /// <summary>Reads a PathData block into a geometry using the given fill rule (0 nonzero, 1 evenodd).</summary>
    private static CanvasGeometry ReadPath(ICanvasResourceCreator creator, ref Reader r, byte fillRule)
    {
        ReadPathData(ref r, out var verbs, out var points);
        return BuildPath(creator, verbs, points, fillRule);
    }

    /// <summary>Reads a PathData block as is: the verbs and the x,y point pairs.</summary>
    private static void ReadPathData(ref Reader r, out byte[] verbs, out float[] points)
    {
        uint nVerbs = r.U32();
        verbs = r.Bytes(nVerbs).ToArray();
        uint nPoints = r.U32();
        r.EnsureAvailable(nPoints, 8);
        points = new float[nPoints * 2];
        for (var i = 0; i < points.Length; i++) points[i] = r.F32();
    }

    /// <summary>
    /// Builds a geometry from path verbs and x,y point pairs using the given fill rule (0 nonzero, 1 evenodd).
    /// Throws <see cref="FormatException"/> if the verbs and the points disagree.
    /// </summary>
    internal static CanvasGeometry BuildPath(ICanvasResourceCreator creator, byte[] verbs, float[] points, byte fillRule)
    {
        using var pb = new CanvasPathBuilder(creator);
        // Must be set before the first figure begins.
        pb.SetFilledRegionDetermination(fillRule == 0
            ? CanvasFilledRegionDetermination.Winding
            : CanvasFilledRegionDetermination.Alternate);

        var figureOpen = false;
        var hasStart = false;
        var start = Vector2.Zero;
        var pointsUsed = 0;

        Vector2 Next()
        {
            if (2 * pointsUsed + 2 > points.Length) throw new FormatException("Path point count mismatch");
            var p = new Vector2(points[2 * pointsUsed], points[2 * pointsUsed + 1]);
            pointsUsed++;
            return p;
        }

        // A segment after a Close continues from the last Move point, as in tiny-skia.
        void EnsureFigure()
        {
            if (figureOpen) return;
            if (!hasStart) throw new FormatException("Segment before move");
            pb.BeginFigure(start, CanvasFigureFill.Default);
            figureOpen = true;
        }

        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case 0: // Move
                    if (figureOpen) pb.EndFigure(CanvasFigureLoop.Open);
                    start = Next();
                    pb.BeginFigure(start, CanvasFigureFill.Default);
                    hasStart = true;
                    figureOpen = true;
                    break;
                case 1: // Line
                    EnsureFigure();
                    pb.AddLine(Next());
                    break;
                case 2: // Quad
                    EnsureFigure();
                    pb.AddQuadraticBezier(Next(), Next());
                    break;
                case 3: // Cubic
                    EnsureFigure();
                    pb.AddCubicBezier(Next(), Next(), Next());
                    break;
                case 4: // Close
                    if (figureOpen)
                    {
                        pb.EndFigure(CanvasFigureLoop.Closed);
                        figureOpen = false;
                    }
                    break;
                default:
                    throw new FormatException("Bad path verb");
            }
        }
        if (figureOpen) pb.EndFigure(CanvasFigureLoop.Open);
        if (2 * pointsUsed != points.Length) throw new FormatException("Path point count mismatch");

        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>An open layer while recording: what it has drawn so far and the bounds of its clip, if any.</summary>
    private sealed class LayerFrame(CanvasActiveLayer layer, Rect? clipBounds)
    {
        public CanvasActiveLayer Layer { get; } = layer;
        public Rect? ClipBounds { get; } = clipBounds;
        public Rect Content = Rect.Empty;
    }

    /// <summary>A solid color or a gradient brush; the brush (if any) is owned and disposed with the struct.</summary>
    internal readonly struct ResolvedPaint(Color solid, ICanvasBrush? brush) : IDisposable
    {
        public Color Solid { get; } = solid;
        public ICanvasBrush? Brush { get; } = brush;

        public void Fill(CanvasDrawingSession ds, CanvasGeometry geo)
        {
            if (Brush != null) ds.FillGeometry(geo, Brush);
            else ds.FillGeometry(geo, Solid);
        }

        public void FillRectangle(CanvasDrawingSession ds, Rect rect)
        {
            if (Brush != null) ds.FillRectangle(rect, Brush);
            else ds.FillRectangle(rect, Solid);
        }

        public void Stroke(CanvasDrawingSession ds, CanvasGeometry geo, float width, CanvasStrokeStyle style)
        {
            if (Brush != null) ds.DrawGeometry(geo, Brush, width, style);
            else ds.DrawGeometry(geo, Solid, width, style);
        }

        public void Dispose() => (Brush as IDisposable)?.Dispose();
    }

    /// <summary>Forward-only little-endian cursor over the command stream; throws when it runs past the end.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public byte U8() => _data[_pos++];

        public uint U32()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
            _pos += 4;
            return v;
        }

        public float F32()
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(_data[_pos..]);
            _pos += 4;
            return v;
        }

        public Vector2 Vec2()
        {
            var x = F32();
            var y = F32();
            return new Vector2(x, y);
        }

        /// <summary>Six floats in tiny-skia order: sx ky kx sy tx ty.</summary>
        public Matrix3x2 Mat()
        {
            var sx = F32();
            var ky = F32();
            var kx = F32();
            var sy = F32();
            var tx = F32();
            var ty = F32();
            return new Matrix3x2(sx, ky, kx, sy, tx, ty);
        }

        /// <summary>Straight-alpha RGBA, 0..1 per channel.</summary>
        public Windows.UI.Color Color()
        {
            var cr = F32();
            var cg = F32();
            var cb = F32();
            var ca = F32();
            return Windows.UI.Color.FromArgb(ToByte(ca), ToByte(cr), ToByte(cg), ToByte(cb));
        }

        public ReadOnlySpan<byte> Bytes(uint count)
        {
            EnsureAvailable(count, 1);
            var s = _data.Slice(_pos, (int)count);
            _pos += (int)count;
            return s;
        }

        /// <summary>Guards allocations driven by a count read from the stream.</summary>
        public readonly void EnsureAvailable(uint count, int itemSize)
        {
            if ((ulong)count * (ulong)itemSize > (ulong)(_data.Length - _pos))
                throw new FormatException("Truncated stream");
        }

        private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
    }
}
