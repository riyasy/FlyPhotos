#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// An SVG drawn on the GPU. Built from the draw stream fly_rust_bridge's svg_scene.rs writes (usvg has already
/// resolved CSS, &lt;use&gt;, markers and text into plain groups and paths). Geometries, brushes and bitmaps are
/// created once; a <see cref="Painter"/> draws them into a bitmap at any scale, so the image stays sharp at
/// every zoom. Clip paths, masks, blend modes and filters map onto Direct2D effects, rendered at that scale.
/// </summary>
/// <remarks>
/// Split across partial files: this one is the public surface (ownership, rasterize); SvgScene.Model.cs the wire
/// tags and scene model, SvgScene.Reader.cs the stream parser, SvgScene.Painter.cs the stepped drawing into a
/// bitmap, SvgScene.NodeDrawer.cs drawing one node, SvgScene.Filters.cs filters.
/// The scene is owned through <see cref="Lease"/>s only; the device resources go with the last one.
/// </remarks>
internal sealed partial class SvgScene
{
    /// <summary>The SVG's width in user units (usvg's resolved canvas size).</summary>
    public float Width { get; }
    /// <summary>The SVG's height in user units.</summary>
    public float Height { get; }

    /// <summary>The root's children: the drawing, in document order.</summary>
    private readonly Node[] _root;
    /// <summary>The root's children as painter items (see <see cref="FlattenItems"/>); null = drawn whole.</summary>
    private readonly Item[]? _rootItems;
    /// <summary>Device resources built from the stream (geometries, brushes, stroke styles, bitmaps).</summary>
    private readonly List<IDisposable> _resources;

    /// <summary>The device the scene's resources were created on; it is drawn only on this device.</summary>
    public CanvasDevice Device { get; }

    /// <summary>Use <see cref="LoadAsync"/>.</summary>
    private SvgScene(float width, float height, Node[] root, List<IDisposable> resources, CanvasDevice device)
    {
        Width = width;
        Height = height;
        _root = root;
        _rootItems = FlattenItems(root);
        _resources = resources;
        Device = device;
    }

    /// <summary>
    /// The live <see cref="Lease"/>s: the SVG reader's (handed on to the display item), plus each renderer or
    /// rasterize call using the scene (see <see cref="TryAcquire"/>). The display item's own leases cover the item;
    /// these cover the GPU resources, so a renderer still drawing keeps them even after the item is freed.
    /// </summary>
    private LeaseCount _owners = new();

    /// <summary>
    /// One owner's hold on the scene. Disposing it drops that ownership, once however often it is called; the last
    /// one releases the device resources. No <see cref="Painter"/> may be stepped after its owner lets go: it draws
    /// with those geometries, brushes and bitmaps.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        // The scene while this lease still holds it; swapped to null by the first Dispose, so a second one is a no-op.
        private SvgScene? _held;

        /// <summary>The scene this lease holds (still set after Dispose; don't draw it then).</summary>
        public SvgScene Scene { get; }

        /// <summary>Takes an ownership already counted in <see cref="_owners"/>.</summary>
        internal Lease(SvgScene scene) => _held = Scene = scene;

        /// <summary>Drops this ownership. Any thread; safe to call more than once.</summary>
        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }

    /// <summary>Adds an owner, so the scene outlives the display item's Dispose; null if it is already released.</summary>
    public Lease? TryAcquire() => _owners.TryAcquire() ? new Lease(this) : null;

    /// <summary>Drops one owner; the last one releases the device resources.</summary>
    private void Release()
    {
        if (!_owners.Release()) return;
        foreach (var r in _resources) r.Dispose();
        _resources.Clear();
    }

    /// <summary>
    /// The pixel size of the scene fitted to <paramref name="maxDimension"/> on its longer side, and the scale
    /// (pixels per user unit) that fits it.
    /// </summary>
    public (int Width, int Height, float Scale) FitTo(int maxDimension)
    {
        var scale = maxDimension / Math.Max(Width, Height);
        return (Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)), scale);
    }

    /// <summary>
    /// Draws the whole scene, in one go, into a new bitmap fitted to <paramref name="maxDimension"/> (owned by the
    /// caller), resampling embedded images with <paramref name="imageQuality"/>. The caller must hold a lease.
    /// </summary>
    public CanvasRenderTarget Rasterize(int maxDimension, CanvasImageInterpolation imageQuality)
    {
        var (w, h, scale) = FitTo(maxDimension);
        var rt = new CanvasRenderTarget(Device, w, h, 96f);
        try
        {
            using var painter = new Painter(this, rt, Matrix3x2.CreateScale(scale), imageQuality);
            painter.Finish();
            return rt;
        }
        catch
        {
            rt.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The path's fill and stroke bounds in its parent's user space, for the painter's culling. Widening for
    /// exact stroke bounds costs about as much as drawing; the stroke pads by the furthest a miter join or
    /// square cap can reach instead (Direct2D's default miter limit is 10).
    /// </summary>
    private static Rect UserBounds(PathNode p)
    {
        var r = p.Geometry.ComputeBounds();
        if (p.Stroke == null || p.StrokeWidth <= 0 || r.IsEmpty) return r;
        var pad = p.StrokeWidth / 2 * Math.Max(p.StrokeStyle?.MiterLimit ?? 10f, 1.5f);
        return new Rect(r.X - pad, r.Y - pad, r.Width + 2 * pad, r.Height + 2 * pad);
    }

    /// <summary>
    /// The clip path as one geometry in the clipped group's user space, when it is a single filled, anti-aliased
    /// path, alone or inside groups that only transform it; null otherwise (it is drawn as a mask then). Paths
    /// inside a clip path are only ever filled, so their outline is the clip.
    /// </summary>
    private static CanvasGeometry? ClipGeometry(ClipDef clip)
    {
        if (clip.Children is not [var node]) return null;
        var m = Matrix3x2.Identity;
        while (node is GroupNode { Clip: null, ClipRect: null, Mask: null, Filters.Length: 0, Opacity: >= 1f,
                   Blend: SvgBlendMode.Normal, Children: [var only] } g)
        {
            m = g.Transform * m;
            node = only;
        }
        return node is PathNode { Fill: not null, Antialias: true } p ? p.Geometry.Transform(m * clip.Transform) : null;
    }

    /// <summary>
    /// Sets the <see cref="Node.Bounds"/> of the images and groups in <paramref name="nodes"/> and under them
    /// (paths have theirs) and returns their union, in the nodes' parent's user space; Rect.Empty for none.
    /// </summary>
    private static Rect UnionBounds(Node[] nodes)
    {
        var union = Rect.Empty;
        foreach (var n in nodes)
        {
            if (n is GroupNode g) g.Bounds = GroupBounds(g);
            else if (n is ImageNode img)
                img.Bounds = img.Data.Bitmap is { } b ? new Rect(0, 0, b.SizeInPixels.Width, b.SizeInPixels.Height) : Rect.Empty;
            if (!n.Bounds.IsEmpty) union.Union(n.Bounds);
        }
        return union;
    }

    /// <summary>
    /// What a group can paint, in its parent's user space: its children, or for a filtered group its filter
    /// regions, cut to its clips and mask region.
    /// </summary>
    private static Rect GroupBounds(GroupNode g)
    {
        var r = UnionBounds(g.Children);
        if (g.Filters.Length > 0)
        {
            // A filter can paint anywhere in its region (a flood), and only there.
            r = Rect.Empty;
            foreach (var f in g.Filters) r.Union(f.Region);
        }
        if (g.ClipRect is { } clipRect) r.Intersect(clipRect);
        for (var c = g.Clip; c?.Geometry != null; c = c.Nested) r.Intersect(c.GeometryBounds);
        if (g.Mask != null) r.Intersect(g.Mask.Rect);
        g.LayerBounds = r;
        return r.IsEmpty ? Rect.Empty : TransformRect(r, g.Transform);
    }

    /// <summary>SVG's 4×5 row-major colour matrix (out = M·[r g b a 1]) as Direct2D's row-vector 5×4.</summary>
    private static Matrix5x4 ToMatrix5x4(float[] m) => new()
    {
        M11 = m[0], M21 = m[1], M31 = m[2], M41 = m[3], M51 = m[4],
        M12 = m[5], M22 = m[6], M32 = m[7], M42 = m[8], M52 = m[9],
        M13 = m[10], M23 = m[11], M33 = m[12], M43 = m[13], M53 = m[14],
        M14 = m[15], M24 = m[16], M34 = m[17], M44 = m[18], M54 = m[19],
    };

    /// <summary>The axis-aligned bounds of <paramref name="r"/> after <paramref name="t"/>.</summary>
    internal static Rect TransformRect(Rect r, Matrix3x2 t)
    {
        var p0 = Vector2.Transform(new Vector2((float)r.Left, (float)r.Top), t);
        var p1 = Vector2.Transform(new Vector2((float)r.Right, (float)r.Top), t);
        var p2 = Vector2.Transform(new Vector2((float)r.Left, (float)r.Bottom), t);
        var p3 = Vector2.Transform(new Vector2((float)r.Right, (float)r.Bottom), t);
        var min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
        var max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
        return new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }
}
