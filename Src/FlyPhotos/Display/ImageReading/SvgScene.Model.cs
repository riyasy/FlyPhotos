#nullable enable
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;

namespace FlyPhotos.Display.ImageReading;

internal sealed partial class SvgScene
{
    // ── Wire tags ────────────────────────────────────────────────────────────
    // Byte values written by fly_rust_bridge/src/svg_scene.rs; keep every value in sync with its constants.

    /// <summary>Node tags in a child list; <see cref="End"/> closes the list.</summary>
    private enum NodeTag : byte { End, Group, Path, Image }

    /// <summary>A group's flags byte: which optional blocks follow, and <c>isolation: isolate</c>.</summary>
    [Flags]
    private enum GroupFlags : byte { None = 0, Clip = 1, Mask = 2, Isolate = 4, ClipRect = 8 }

    /// <summary>A path's flags byte: which paints follow, paint order, anti-aliasing and fill rule.</summary>
    [Flags]
    private enum PathFlags : byte { None = 0, Fill = 1, Stroke = 2, StrokeFirst = 4, Antialias = 8, EvenOdd = 16 }

    /// <summary>Path segment tags; Move/Line carry one point, Quad two, Cubic three, Close none.</summary>
    private enum PathVerb : byte { Move, Line, Quad, Cubic, Close }

    /// <summary>Paint tags: solid colour, linear gradient, radial gradient, pattern.</summary>
    private enum PaintTag : byte { Solid, Linear, Radial, Pattern }

    /// <summary>stroke-linecap.</summary>
    private enum LineCapTag : byte { Butt, Round, Square }

    /// <summary>stroke-linejoin (miter-clip arrives as Miter).</summary>
    private enum LineJoinTag : byte { Miter, Round, Bevel }

    /// <summary>Gradient spreadMethod.</summary>
    private enum SpreadTag : byte { Pad, Reflect, Repeat }

    /// <summary>usvg::BlendMode in declaration order.</summary>
    private enum SvgBlendMode : byte
    {
        Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight,
        Difference, Exclusion, Hue, Saturation, Color, Luminosity, PlusLighter,
    }

    /// <summary>Filter primitive tags (fe* elements).</summary>
    private enum FilterTag : byte
    {
        Blend = 1, ColorMatrix, ComponentTransfer, Composite, DropShadow, Flood, GaussianBlur, Merge, Morphology,
        Offset, Turbulence, DiffuseLighting, SpecularLighting,
    }

    /// <summary>Where a primitive input comes from; <see cref="Reference"/> names an earlier result.</summary>
    private enum InputKind : byte { SourceGraphic, SourceAlpha, Reference }

    /// <summary>feComponentTransfer function types (feFuncR/G/B/A <c>type</c>).</summary>
    private enum TransferKind : byte { Identity, Table, Discrete, Linear, Gamma }

    /// <summary>feComposite operators.</summary>
    private enum CompositeOp : byte { Over, In, Out, Atop, Xor, Lighter, Arithmetic }

    /// <summary>Light source types: feDistantLight, fePointLight, feSpotLight.</summary>
    private enum LightKind : byte { Distant, Point, Spot }

    // ── Scene model ──────────────────────────────────────────────────────────

    /// <summary>A drawable node: <see cref="GroupNode"/>, <see cref="PathNode"/> or <see cref="ImageNode"/>.</summary>
    private abstract class Node;

    /// <summary>A group (also a flattened text element or a nested SVG image).</summary>
    private sealed class GroupNode : Node
    {
        /// <summary>The group's own transform, relative to its parent.</summary>
        public Matrix3x2 Transform;
        /// <summary>Group opacity, 0..1.</summary>
        public float Opacity;
        /// <summary>mix-blend-mode.</summary>
        public SvgBlendMode Blend;
        /// <summary>clip-path, in the group's user space.</summary>
        public ClipDef? Clip;
        /// <summary>A clip-path that is one axis-aligned rectangle, in the group's user space (instead of <see cref="Clip"/>).</summary>
        public Rect? ClipRect;
        /// <summary>mask, in the group's user space.</summary>
        public MaskDef? Mask;
        /// <summary>The filter chain, applied in order.</summary>
        public FilterDef[] Filters = [];
        /// <summary>Child nodes in document order.</summary>
        public Node[] Children = [];
        /// <summary>CSS <c>isolation: isolate</c>: a backdrop boundary for mix-blend-mode on its own.</summary>
        public bool Isolate;
        /// <summary>Has a clip, mask or filter, so it is drawn as an effect image rather than straight into the list.</summary>
        public bool NeedsLayerImage => Clip != null || Mask != null || Filters.Length > 0;
        /// <summary>Draws straight into its parent's backdrop: no layer of its own.</summary>
        public bool IsPassThrough => !NeedsLayerImage && ClipRect == null && !Isolate && Opacity >= 1f && Blend == SvgBlendMode.Normal;
        /// <summary>A blended group sits in this subtree without an isolating group in between.</summary>
        public bool HasBlend;
    }

    /// <summary>Whether a blended group is reachable from <paramref name="nodes"/> through pass-through groups only.</summary>
    private static bool ComputeHasBlend(Node[] nodes)
    {
        foreach (var n in nodes)
            if (n is GroupNode g && (g.Blend != SvgBlendMode.Normal || g.IsPassThrough && g.HasBlend)) return true;
        return false;
    }

    /// <summary>A path with its fill and/or stroke. The geometry is in the parent's user space.</summary>
    private sealed class PathNode : Node
    {
        /// <summary>
        /// The outline, built once; its fill rule is the path's fill-rule. Set after the whole stream is read,
        /// when all paths' geometries are built in parallel.
        /// </summary>
        public CanvasGeometry Geometry = null!;
        /// <summary>Bounds of the fill and stroke in the parent's user space, for the painter's culling; set with <see cref="Geometry"/>.</summary>
        public Rect Bounds;
        /// <summary>Fill paint (opacity baked in), or null for no fill.</summary>
        public Paint? Fill;
        /// <summary>Stroke paint (opacity baked in), or null for no stroke.</summary>
        public Paint? Stroke;
        /// <summary>stroke-width in user units.</summary>
        public float StrokeWidth;
        /// <summary>Caps, join, miter limit and dashes (dashes in stroke widths, as Win2D measures them).</summary>
        public CanvasStrokeStyle? StrokeStyle;
        /// <summary><c>paint-order: stroke</c>: stroke before fill.</summary>
        public bool StrokeFirst;
        /// <summary>Anti-alias edges (false for shape-rendering crispEdges / optimizeSpeed).</summary>
        public bool Antialias;
    }

    /// <summary>An embedded raster image, drawn at (0, 0, pixel width, pixel height) in its user space.</summary>
    private sealed class ImageNode : Node
    {
        /// <summary>Where the encoded image starts in the stream; decoded from there into <see cref="Bitmap"/>.</summary>
        public int DataPos;
        /// <summary>The encoded image's length in bytes.</summary>
        public int DataLength;
        /// <summary>The decoded image (96 DPI, so DIPs equal pixels); null if decoding failed.</summary>
        public CanvasBitmap? Bitmap;
        /// <summary>image-rendering asks for nearest-neighbour (pixelated / crisp-edges / optimizeSpeed).</summary>
        public bool Nearest;
    }

    /// <summary>A brush built once, or a pattern whose tile is recorded at the drawing scale.</summary>
    private sealed class Paint
    {
        /// <summary>Solid colour or gradient brush (paint opacity baked in); null for a pattern.</summary>
        public ICanvasBrush? Brush;
        /// <summary>The pattern, when this paint is one.</summary>
        public PatternDef? Pattern;
        /// <summary>Paint opacity for a pattern (applied to the image brush).</summary>
        public float Opacity = 1f;
    }

    /// <summary>A pattern tile: its rectangle, patternTransform and content (content in tile space).</summary>
    private sealed class PatternDef
    {
        /// <summary>The tile rectangle in pattern space.</summary>
        public Rect Rect;
        /// <summary>patternTransform.</summary>
        public Matrix3x2 Transform;
        /// <summary>The tile's content, with (0, 0) at the tile's corner.</summary>
        public Node[] Children = [];
    }

    /// <summary>A clipPath. Its children are opaque black fills; the coverage is the clip.</summary>
    private sealed class ClipDef
    {
        /// <summary>The clipPath's own transform, applied on top of the clipped group's.</summary>
        public Matrix3x2 Transform;
        /// <summary>A clip-path on the clipPath element itself; intersected with this one.</summary>
        public ClipDef? Nested;
        /// <summary>The clip shapes.</summary>
        public Node[] Children = [];
    }

    /// <summary>A mask: its region, luminance or alpha type, and content.</summary>
    private sealed class MaskDef
    {
        /// <summary>The mask region in the masked group's user space; nothing outside it shows.</summary>
        public Rect Rect;
        /// <summary>mask-type: alpha (otherwise luminance).</summary>
        public bool Alpha;
        /// <summary>A mask on the mask element; applied to the content first, as resvg does.</summary>
        public MaskDef? Nested;
        /// <summary>The mask content.</summary>
        public Node[] Children = [];
    }

    /// <summary>One &lt;filter&gt;: its region and primitives, in order.</summary>
    private sealed class FilterDef
    {
        /// <summary>The filter region in the filtered group's user space (usvg resolves the units).</summary>
        public Rect Region;
        /// <summary>The primitives; each one's result can feed later ones.</summary>
        public Primitive[] Primitives = [];
    }

    // ── Filter primitives ────────────────────────────────────────────────────

    /// <summary>A primitive input; <paramref name="Name"/> is the referenced result (empty otherwise).</summary>
    private readonly record struct Input(InputKind Kind, string Name);

    /// <summary>One channel's transfer function: Table/Discrete = values, Linear = slope, intercept,
    /// Gamma = amplitude, exponent, offset.</summary>
    private readonly record struct Transfer(TransferKind Kind, float[] Values);

    /// <summary>A filter light source in user units; angles in degrees.</summary>
    private readonly record struct Light(LightKind Kind, float Azimuth, float Elevation, Vector3 Position,
        Vector3 Target, float Focus, float? ConeAngle);

    /// <summary>The fields every filter primitive has; one subtype per primitive kind.</summary>
    private abstract record Primitive
    {
        /// <summary>The primitive subregion in user space (usvg fills in the defaults).</summary>
        public Rect Subregion;
        /// <summary>color-interpolation-filters is linearRGB.</summary>
        public bool Linear;
        /// <summary>The result name later primitives can reference.</summary>
        public string Result = "";
    }

    /// <summary>feBlend: <paramref name="In"/> (top) over <paramref name="In2"/> (backdrop).</summary>
    private sealed record BlendPrimitive(Input In, Input In2, SvgBlendMode Mode) : Primitive;
    /// <summary>feColorMatrix as the 20-value SVG matrix.</summary>
    private sealed record ColorMatrixPrimitive(Input In, float[] Matrix) : Primitive;
    /// <summary>feComponentTransfer: R, G, B, A.</summary>
    private sealed record ComponentTransferPrimitive(Input In, Transfer[] Channels) : Primitive;
    /// <summary>feComposite; <paramref name="K"/> = k1..k4 for arithmetic.</summary>
    private sealed record CompositePrimitive(Input In, Input In2, CompositeOp Op, Vector4 K) : Primitive;
    /// <summary>feDropShadow: offset (dx, dy) and deviation in user units; colour with flood-opacity as alpha.</summary>
    private sealed record DropShadowPrimitive(Input In, Vector2 Offset, Vector2 StdDev, Color Color) : Primitive;
    /// <summary>feFlood: flood-color with flood-opacity as alpha.</summary>
    private sealed record FloodPrimitive(Color Color) : Primitive;
    /// <summary>feGaussianBlur: stdDeviation x, y in user units.</summary>
    private sealed record BlurPrimitive(Input In, Vector2 StdDev) : Primitive;
    /// <summary>feMerge: inputs drawn bottom to top.</summary>
    private sealed record MergePrimitive(Input[] Inputs) : Primitive;
    /// <summary>feMorphology: dilate or erode, radius x, y in user units.</summary>
    private sealed record MorphologyPrimitive(Input In, bool Dilate, Vector2 Radius) : Primitive;
    /// <summary>feOffset: dx, dy in user units.</summary>
    private sealed record OffsetPrimitive(Input In, Vector2 Offset) : Primitive;
    /// <summary>feTurbulence; <paramref name="FractalNoise"/> false = type="turbulence".</summary>
    private sealed record TurbulencePrimitive(Vector2 BaseFrequency, int Octaves, int Seed, bool Stitch,
        bool FractalNoise) : Primitive;
    /// <summary>feDiffuseLighting / feSpecularLighting; <paramref name="Constant"/> is kd or ks,
    /// <paramref name="Exponent"/> the specular exponent (unused for diffuse).</summary>
    private sealed record LightingPrimitive(Input In, bool Specular, float SurfaceScale, float Constant, float Exponent,
        Color Color, Light Light) : Primitive;
}
