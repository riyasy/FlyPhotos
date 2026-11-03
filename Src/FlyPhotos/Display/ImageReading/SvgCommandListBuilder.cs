#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using NLog;
using Windows.UI;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Replays the binary draw-command stream produced by the Rust bridge (<c>resvg_render_svg_with_commands</c>)
/// into a <see cref="CanvasCommandList"/>, a retained Direct2D display list that can be drawn at any zoom
/// without re-rasterising. All coordinates are in SVG user units; the stream is little-endian.
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

    private const byte OpEnd = 0;
    private const byte OpPushLayer = 1;
    private const byte OpPopLayer = 2;
    private const byte OpFill = 3;
    private const byte OpStroke = 4;

    /// <summary>
    /// Builds a command list from <paramref name="data"/>. Returns null (after logging) if the stream is malformed
    /// or Win2D rejects it. <paramref name="width"/>/<paramref name="height"/> are the SVG's size in user units.
    /// </summary>
    public static CanvasCommandList? Build(ICanvasResourceCreator creator, ReadOnlySpan<byte> data,
        out float width, out float height, out bool isHeavy)
    {
        width = 0;
        height = 0;
        isHeavy = false;

        CanvasCommandList? list = null;
        var layers = new Stack<CanvasActiveLayer>();
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

            list = new CanvasCommandList(creator);
            // The list only becomes drawable once the session that records it is disposed.
            using (var ds = list.CreateDrawingSession())
            {
                ds.Units = CanvasUnits.Pixels;
                try
                {
                    Replay(creator, ds, ref r, layers);
                }
                finally
                {
                    while (layers.Count > 0) layers.Pop().Dispose();
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to build SVG command list");
            list?.Dispose();
            return null;
        }
    }

    private static void Replay(ICanvasResourceCreator creator, CanvasDrawingSession ds, ref Reader r,
        Stack<CanvasActiveLayer> layers)
    {
        while (true)
        {
            switch (r.U8())
            {
                case OpEnd:
                    if (layers.Count != 0) throw new FormatException("Unbalanced layers");
                    return;
                case OpPushLayer:
                    PushLayer(creator, ds, ref r, layers);
                    break;
                case OpPopLayer:
                    if (layers.Count == 0) throw new FormatException("Pop without push");
                    layers.Pop().Dispose();
                    break;
                case OpFill:
                    Fill(creator, ds, ref r);
                    break;
                case OpStroke:
                    Stroke(creator, ds, ref r);
                    break;
                default:
                    throw new FormatException("Unknown opcode");
            }
        }
    }

    private static void PushLayer(ICanvasResourceCreator creator, CanvasDrawingSession ds, ref Reader r,
        Stack<CanvasActiveLayer> layers)
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
        layers.Push(clip == null ? ds.CreateLayer(opacity) : ds.CreateLayer(opacity, clip));
        clip?.Dispose();
    }

    private static void Fill(ICanvasResourceCreator creator, CanvasDrawingSession ds, ref Reader r)
    {
        var mat = r.Mat();
        var fillRule = r.U8();
        var aliased = r.U8() != 0;
        using var paint = ReadPaint(creator, ref r);
        using var geo = ReadPath(creator, ref r, fillRule);

        ds.Transform = mat;
        ds.Antialiasing = aliased ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (paint.Brush != null) ds.FillGeometry(geo, paint.Brush);
        else ds.FillGeometry(geo, paint.Solid);
    }

    private static void Stroke(ICanvasResourceCreator creator, CanvasDrawingSession ds, ref Reader r)
    {
        var mat = r.Mat();
        var aliased = r.U8() != 0;
        using var paint = ReadPaint(creator, ref r);
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
        using var geo = ReadPath(creator, ref r, 0);

        // A zero or negative width draws nothing (and would divide by zero below).
        if (!(strokeWidth > 0)) return;

        using var style = new CanvasStrokeStyle
        {
            StartCap = cap,
            EndCap = cap,
            DashCap = cap,
            LineJoin = join,
            MiterLimit = miterLimit
        };
        if (nDash > 0)
        {
            // Direct2D expresses dash lengths and offset in multiples of the stroke width.
            for (var i = 0; i < dash.Length; i++) dash[i] /= strokeWidth;
            style.CustomDashStyle = dash;
            style.DashOffset = dashOffset / strokeWidth;
        }

        ds.Transform = mat;
        ds.Antialiasing = aliased ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (paint.Brush != null) ds.DrawGeometry(geo, paint.Brush, strokeWidth, style);
        else ds.DrawGeometry(geo, paint.Solid, strokeWidth, style);
    }

    private static Paint ReadPaint(ICanvasResourceCreator creator, ref Reader r)
    {
        switch (r.U8())
        {
            case 0:
                return new Paint(r.Color(), null);
            case 1:
            {
                var start = r.Vec2();
                var end = r.Vec2();
                var transform = r.Mat();
                var edge = ReadSpread(ref r);
                float opacity = r.F32();
                var stops = ReadStops(ref r);
                return new Paint(default, new CanvasLinearGradientBrush(creator, stops, edge, CanvasAlphaMode.Straight)
                {
                    StartPoint = start,
                    EndPoint = end,
                    Transform = transform,
                    Opacity = opacity
                });
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
                return new Paint(default, new CanvasRadialGradientBrush(creator, stops, edge, CanvasAlphaMode.Straight)
                {
                    Center = center,
                    OriginOffset = focal - center,
                    RadiusX = radius,
                    RadiusY = radius,
                    Transform = transform,
                    Opacity = opacity
                });
            }
            default:
                throw new FormatException("Bad paint kind");
        }
    }

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
        uint nVerbs = r.U32();
        var verbs = r.Bytes(nVerbs);
        uint nPoints = r.U32();
        r.EnsureAvailable(nPoints, 8);

        using var pb = new CanvasPathBuilder(creator);
        // Must be set before the first figure begins.
        pb.SetFilledRegionDetermination(fillRule == 0
            ? CanvasFilledRegionDetermination.Winding
            : CanvasFilledRegionDetermination.Alternate);

        var figureOpen = false;
        var hasStart = false;
        var start = Vector2.Zero;
        uint pointsUsed = 0;

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
                    start = r.Vec2();
                    pointsUsed++;
                    pb.BeginFigure(start, CanvasFigureFill.Default);
                    hasStart = true;
                    figureOpen = true;
                    break;
                case 1: // Line
                    EnsureFigure();
                    pb.AddLine(r.Vec2());
                    pointsUsed++;
                    break;
                case 2: // Quad
                    EnsureFigure();
                    pb.AddQuadraticBezier(r.Vec2(), r.Vec2());
                    pointsUsed += 2;
                    break;
                case 3: // Cubic
                    EnsureFigure();
                    pb.AddCubicBezier(r.Vec2(), r.Vec2(), r.Vec2());
                    pointsUsed += 3;
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
        if (pointsUsed != nPoints) throw new FormatException("Path point count mismatch");

        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>A solid color or a gradient brush; the brush (if any) is owned and disposed with the struct.</summary>
    private readonly struct Paint(Color solid, ICanvasBrush? brush) : IDisposable
    {
        public Color Solid { get; } = solid;
        public ICanvasBrush? Brush { get; } = brush;

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
