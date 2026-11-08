#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Windows.Foundation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// An SVG drawn on the GPU. Built from the draw stream fly_rust_bridge's svg_scene.rs writes (usvg has already
/// resolved CSS, &lt;use&gt;, markers and text into plain groups and paths). Geometries, brushes and bitmaps are
/// created once; a <see cref="Painter"/> draws them into a bitmap at any scale, so the image stays sharp at
/// every zoom. Clip paths, masks, blend modes and filters map onto Direct2D effects, rendered at that scale.
/// </summary>
/// <remarks>
/// Split across partial files: this one is the public surface; SvgScene.Model.cs the wire tags and scene
/// model, SvgScene.Reader.cs the stream parser, SvgScene.NodeDrawer.cs drawing, SvgScene.Filters.cs filters.
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
        _resources = resources;
        Device = device;
    }

    /// <summary>
    /// The number of live <see cref="Lease"/>s: the loader's (handed on to the display item), plus each renderer
    /// or rasterize call using the scene (see <see cref="TryAcquire"/>). The HQ cache disposes its item on the UI
    /// thread when the photo leaves the window, while the renderer may still be drawing it on the W2D thread until
    /// the next photo is installed.
    /// </summary>
    private int _owners = 1;

    /// <summary>
    /// One owner's hold on the scene. Disposing it drops that ownership, once however often it is called; the last
    /// one releases the device resources. No <see cref="Painter"/> may be stepped after its owner lets go: it draws
    /// with those geometries, brushes and bitmaps.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private SvgScene? _held;

        /// <summary>The scene this lease holds (still set after Dispose; don't draw it then).</summary>
        public SvgScene Scene { get; }

        /// <summary>Takes an ownership already counted in <see cref="_owners"/>.</summary>
        internal Lease(SvgScene scene) => _held = Scene = scene;

        public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
    }

    /// <summary>Adds an owner, so the scene outlives the display item's Dispose; null if it is already released.</summary>
    public Lease? TryAcquire()
    {
        for (var n = Volatile.Read(ref _owners); n > 0; n = Volatile.Read(ref _owners))
            if (Interlocked.CompareExchange(ref _owners, n + 1, n) == n) return new Lease(this);
        return null;
    }

    /// <summary>Drops one owner; the last one releases the device resources.</summary>
    private void Release()
    {
        if (Interlocked.Decrement(ref _owners) != 0) return;
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
    /// Draws a scene into a bitmap, the only way the scene is drawn: <see cref="Finish"/> in one go (previews,
    /// clipboard), or <see cref="Step"/> a batch of items at a time so a slow scene (a big map can have thousands
    /// of ~1,000-point paths, seconds of tessellation) can be spread over frames. SVG user units map to the
    /// bitmap's pixels by userToPx; vectors and effects (clips, masks, filters, blends) are rasterised at that
    /// scale, and the root viewport clips like Chrome's overflow:hidden. Items are the root's nodes with
    /// pass-through groups opened up, so they keep their order and transforms; paths wholly outside the bitmap
    /// are skipped. A big group with opacity, a clip, a mask or isolation is stepped through too, into a bitmap
    /// of its own that is composited with those effects when done (filters need the whole source at once, so a
    /// filtered group is one item). A node list with mix-blend-mode is drawn in one step, as blending needs
    /// everything before it in the same drawing session. The caller owns the target; dispose the painter if it
    /// is dropped before it is done.
    /// </summary>
    public sealed class Painter : IDisposable
    {
        /// <summary>A group with at least this many nodes under it is stepped through rather than drawn in one item.</summary>
        private const int StepGroupMinNodes = 256;

        private readonly SvgScene _scene;
        private readonly ICanvasResourceCreator _rc;
        private readonly CanvasRenderTarget _target;
        private readonly CanvasImageInterpolation _quality;
        private readonly Matrix3x2 _userToPx;
        /// <summary>The nodes to draw: the root's, or a big group's children.</summary>
        private readonly Node[] _nodes;
        /// <summary>The root viewport, in user units; null for a group (its parent painter clips).</summary>
        private readonly Rect? _viewport;
        private readonly List<(Node Node, Matrix3x2 Parent)> _items = [];
        /// <summary>Draw all of <see cref="_nodes"/> in one step (they have a blend).</summary>
        private readonly bool _whole;
        private int _next;
        private bool _started;
        /// <summary>Items drawn per step; adapted so a step takes about the budget.</summary>
        private int _batch = 32;
        /// <summary>The big group at <c>_items[_next]</c>, being drawn into its own bitmap (its target).</summary>
        private Painter? _group;

        private bool Done => _started && _next >= _items.Count;

        /// <param name="target">A bitmap on the scene's <see cref="SvgScene.Device"/>.</param>
        internal Painter(SvgScene scene, CanvasRenderTarget target, Matrix3x2 userToPx, CanvasImageInterpolation quality)
            // usvg has already folded the viewBox into the root, so the viewport is 0,0 → Width×Height.
            : this(scene, scene.Device, target, userToPx, quality, scene._root, new Rect(0, 0, scene.Width, scene.Height)) { }

        private Painter(SvgScene scene, ICanvasResourceCreator rc, CanvasRenderTarget target, Matrix3x2 userToPx,
            CanvasImageInterpolation quality, Node[] nodes, Rect? viewport)
        {
            _scene = scene;
            _rc = rc;
            _target = target;
            _userToPx = userToPx;
            _quality = quality;
            _nodes = nodes;
            _viewport = viewport;
            _whole = ComputeHasBlend(nodes);
            if (!_whole) Flatten(nodes, userToPx);
        }

        private void Flatten(Node[] nodes, Matrix3x2 parent)
        {
            foreach (var n in nodes)
                if (n is GroupNode { IsPassThrough: true } g) Flatten(g.Children, g.Transform * parent);
                else _items.Add((n, parent));
        }

        /// <summary>Draws everything left in one go.</summary>
        public void Finish()
        {
            while (!Step(double.MaxValue)) { }
        }

        /// <summary>Draws the next batch, aiming at <paramref name="budgetMs"/>; returns whether everything is drawn.</summary>
        public bool Step(double budgetMs)
        {
            if (_group != null) return StepGroup(budgetMs);
            var sw = Stopwatch.StartNew();
            var size = _target.SizeInPixels;
            var drawer = new NodeDrawer(_rc, _quality);
            var drawn = 0;
            try
            {
                using var ds = _target.CreateDrawingSession();
                if (!_started) ds.Clear(Microsoft.UI.Colors.Transparent);
                _started = true;
                ds.Transform = _userToPx;
                using (_viewport is { } vp ? ds.CreateLayer(1f, vp) : null)
                {
                    if (_whole) drawer.DrawNodes(ds, _nodes);
                    else
                        // The time check also bounds a run of culled paths, which the batch doesn't count. It
                        // applies only after the first item, so every step makes progress however slow it is
                        // (starting a big group counts: its own steps then make progress).
                        for (var start = _next; _next < _items.Count && drawn < _batch
                             && (_next == start || sw.Elapsed.TotalMilliseconds < budgetMs);)
                        {
                            var (n, parent) = _items[_next];
                            if (n is PathNode p && !Visible(p, parent, size.Width, size.Height)) { _next++; continue; }
                            if (n is GroupNode g && SteppedThrough(g))
                            {
                                var bmp = new CanvasRenderTarget(_rc, size.Width, size.Height, 96f);
                                _group = new Painter(_scene, _rc, bmp, g.Transform * parent, _quality, g.Children, null);
                                break;
                            }
                            _next++;
                            ds.Transform = parent;
                            drawer.DrawNode(ds, n);
                            drawn++;
                        }
                }
            }
            finally
            {
                foreach (var t in drawer.Transients) t.Dispose(); // the session has ended, so its effects are done
            }
            if (drawn > 0)
                _batch = (int)Math.Clamp(_batch * budgetMs / Math.Max(1.0, sw.Elapsed.TotalMilliseconds), 4, 8192);
            return Done;
        }

        /// <summary>
        /// Steps the big group's own painter; once it is done, composites its bitmap with the group's clip, mask
        /// and opacity, and moves past the group.
        /// </summary>
        private bool StepGroup(double budgetMs)
        {
            if (!_group!.Step(budgetMs)) return false;
            var (n, parent) = _items[_next];
            var g = (GroupNode)n;
            var drawer = new NodeDrawer(_rc, _quality);
            try
            {
                using var ds = _target.CreateDrawingSession();
                ds.Transform = _userToPx;
                using (_viewport is { } vp ? ds.CreateLayer(1f, vp) : null)
                {
                    // The group's bitmap is in this target's pixels, and its effects map into them by t.
                    ds.Transform = Matrix3x2.Identity;
                    ds.DrawImage(drawer.GroupEffects(g, _group._target, g.Transform * parent));
                }
            }
            finally
            {
                foreach (var t in drawer.Transients) t.Dispose();
                EndGroup();
                _next++;
            }
            return Done;
        }

        /// <summary>
        /// Whether <paramref name="g"/> (not pass-through: it has opacity, a clip, a mask or isolation) is big
        /// enough to step through, and has no filter or blend, which need it whole.
        /// </summary>
        private static bool SteppedThrough(GroupNode g) =>
            g.Filters.Length == 0 && g.Blend == SvgBlendMode.Normal && CountNodes(g.Children, StepGroupMinNodes) >= StepGroupMinNodes;

        /// <summary>The nodes in <paramref name="nodes"/> and under them, counting no further than about <paramref name="limit"/>.</summary>
        private static int CountNodes(Node[] nodes, int limit)
        {
            var count = 0;
            foreach (var n in nodes)
            {
                count++;
                if (n is GroupNode g) count += CountNodes(g.Children, limit - count);
                if (count >= limit) break;
            }
            return count;
        }

        /// <summary>Drops the big group's painter and its bitmap.</summary>
        private void EndGroup()
        {
            if (_group == null) return;
            _group.Dispose();
            _group._target.Dispose();
            _group = null;
        }

        /// <summary>Releases a big group's bitmap if the painter is dropped mid-group.</summary>
        public void Dispose() => EndGroup();

        /// <summary>Whether the path's fill or stroke reaches into the w×h bitmap under <paramref name="parent"/>.</summary>
        private static bool Visible(PathNode p, Matrix3x2 parent, uint w, uint h)
        {
            var r = TransformRect(p.Bounds, parent);
            return r.Right >= 0 && r.Bottom >= 0 && r.Left <= w && r.Top <= h;
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
