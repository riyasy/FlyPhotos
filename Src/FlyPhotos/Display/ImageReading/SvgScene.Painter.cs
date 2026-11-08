#nullable enable
using System;
using System.Diagnostics;
using System.Numerics;
using Windows.Foundation;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

internal sealed partial class SvgScene
{
    /// <summary>A group with at least this many nodes under it is stepped through rather than drawn in one item.</summary>
    private const int StepGroupMinNodes = 256;

    /// <summary>
    /// What a <see cref="Painter"/> steps through for <paramref name="nodes"/>: the nodes with pass-through groups
    /// opened up, each with its transform relative to <paramref name="nodes"/>' space, in document order. Null when
    /// the nodes have a mix-blend-mode: blending needs them all in one drawing session, so they are drawn whole.
    /// Built once at load, as the scene doesn't change.
    /// </summary>
    private static Item[]? FlattenItems(Node[] nodes)
    {
        if (ComputeHasBlend(nodes)) return null;
        var items = new Item[Count(nodes)];
        var next = 0;
        Add(nodes, Matrix3x2.Identity);
        return items;

        static int Count(Node[] list)
        {
            var count = 0;
            foreach (var n in list)
                count += n is GroupNode { IsPassThrough: true } g ? Count(g.Children) : 1;
            return count;
        }

        void Add(Node[] list, Matrix3x2 parent)
        {
            foreach (var n in list)
                if (n is GroupNode { IsPassThrough: true } g) Add(g.Children, g.Transform * parent);
                else items[next++] = new Item(n, parent);
        }
    }

    /// <summary>
    /// Whether a painter steps through <paramref name="g"/> into a bitmap of its own instead of drawing it as one
    /// item: it has a layer (opacity, a clip, a mask or isolation), no filter or blend (they need the whole source
    /// in one session), and at least <see cref="StepGroupMinNodes"/> nodes under it.
    /// </summary>
    private static bool IsStepThrough(GroupNode g) =>
        !g.IsPassThrough && g.Filters.Length == 0 && g.Blend == SvgBlendMode.Normal
        && CountNodes(g.Children, StepGroupMinNodes) >= StepGroupMinNodes;

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

    /// <summary>
    /// Draws a scene into a bitmap, the only way the scene is drawn: <see cref="Finish"/> in one go (previews,
    /// clipboard), or <see cref="Step"/> a batch of items at a time so a slow scene (a big map can have thousands
    /// of ~1,000-point paths, seconds of tessellation) can be spread over frames. SVG user units map to the
    /// bitmap's pixels by userToPx; vectors and effects (clips, masks, filters, blends) are rasterised at that
    /// scale, and the root viewport clips like Chrome's overflow:hidden. Items (built at load) are the root's
    /// nodes with pass-through groups opened up, so they keep their order and transforms; items wholly outside
    /// the bitmap are skipped. A big group with opacity, a clip, a mask or isolation is stepped through too, into
    /// a bitmap of its own (just big enough for the group's visible part) that is composited with those effects when done (filters need the whole source at
    /// once, so a filtered group is one item). A node list with mix-blend-mode is drawn in one step, as blending
    /// needs everything before it in the same drawing session. The caller owns the target; dispose the painter if
    /// it is dropped before it is done.
    /// </summary>
    public sealed class Painter : IDisposable
    {
        private readonly SvgScene _scene;
        private readonly ICanvasResourceCreator _rc;
        private readonly CanvasRenderTarget _target;
        private readonly CanvasImageInterpolation _quality;
        private readonly Matrix3x2 _userToPx;
        /// <summary>The nodes to draw: the root's, or a big group's children.</summary>
        private readonly Node[] _nodes;
        /// <summary>The root viewport, in user units; null for a group (its parent painter clips).</summary>
        private readonly Rect? _viewport;
        /// <summary><see cref="_nodes"/> as items (built at load); null = draw them whole, in one step (they have a blend).</summary>
        private readonly Item[]? _items;
        private int _next;
        private bool _started;
        /// <summary>Items drawn per step; adapted so a step takes about the budget.</summary>
        private int _batch = 32;
        /// <summary>The big group at <c>_items[_next]</c>, being drawn into its own bitmap (its target).</summary>
        private Painter? _group;
        /// <summary>Where <see cref="_group"/>'s bitmap sits in this target, in whole pixels.</summary>
        private Vector2 _groupOffset;

        private bool Done => _started && _next >= (_items?.Length ?? 0);

        /// <param name="target">A bitmap on the scene's <see cref="SvgScene.Device"/>.</param>
        internal Painter(SvgScene scene, CanvasRenderTarget target, Matrix3x2 userToPx, CanvasImageInterpolation quality)
            // usvg has already folded the viewBox into the root, so the viewport is 0,0 → Width×Height.
            : this(scene, scene.Device, target, userToPx, quality, scene._root, scene._rootItems,
                new Rect(0, 0, scene.Width, scene.Height)) { }

        private Painter(SvgScene scene, ICanvasResourceCreator rc, CanvasRenderTarget target, Matrix3x2 userToPx,
            CanvasImageInterpolation quality, Node[] nodes, Item[]? items, Rect? viewport)
        {
            _scene = scene;
            _rc = rc;
            _target = target;
            _userToPx = userToPx;
            _quality = quality;
            _nodes = nodes;
            _items = items;
            _viewport = viewport;
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
                    if (_items == null) drawer.DrawNodes(ds, _nodes);
                    else
                        // The time check also bounds a run of culled paths, which the batch doesn't count. It
                        // applies only after the first item, so every step makes progress however slow it is
                        // (starting a big group counts: its own steps then make progress).
                        for (var start = _next; _next < _items.Length && drawn < _batch
                             && (_next == start || sw.Elapsed.TotalMilliseconds < budgetMs);)
                        {
                            var (n, itemParent) = _items[_next];
                            var parent = itemParent * _userToPx;
                            if (!Visible(n, parent, size.Width, size.Height)) { _next++; continue; }
                            if (n is GroupNode { StepThrough: true } g)
                            {
                                // A bitmap just big enough for the visible part of the group.
                                var px = TransformRect(g.Bounds, parent);
                                double x0 = Math.Max(0, Math.Floor(px.Left)), y0 = Math.Max(0, Math.Floor(px.Top));
                                double x1 = Math.Min(size.Width, Math.Ceiling(px.Right)), y1 = Math.Min(size.Height, Math.Ceiling(px.Bottom));
                                var bmp = new CanvasRenderTarget(_rc, (float)Math.Max(1, x1 - x0), (float)Math.Max(1, y1 - y0), 96f);
                                _groupOffset = new Vector2((float)x0, (float)y0);
                                _group = new Painter(_scene, _rc, bmp, g.Transform * parent * Matrix3x2.CreateTranslation(-_groupOffset),
                                    _quality, g.Children, g.Items, null);
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
            var (n, itemParent) = _items![_next];
            var parent = itemParent * _userToPx;
            var g = (GroupNode)n;
            var drawer = new NodeDrawer(_rc, _quality);
            try
            {
                using var ds = _target.CreateDrawingSession();
                ds.Transform = _userToPx;
                using (_viewport is { } vp ? ds.CreateLayer(1f, vp) : null)
                {
                    // The group's bitmap is in this target's pixels from its offset, and its effects map into them
                    // by t; drawn at that whole-pixel offset, so nothing is resampled.
                    ds.Transform = Matrix3x2.CreateTranslation(_groupOffset);
                    ds.DrawImage(drawer.GroupEffects(g, _group._target,
                        g.Transform * parent * Matrix3x2.CreateTranslation(-_groupOffset)));
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

        /// <summary>Whether what the node paints reaches into the w×h bitmap under <paramref name="parent"/>.</summary>
        private static bool Visible(Node n, Matrix3x2 parent, uint w, uint h)
        {
            if (n.Bounds.IsEmpty) return false;
            var r = TransformRect(n.Bounds, parent);
            return r.Right >= 0 && r.Bottom >= 0 && r.Left <= w && r.Top <= h;
        }
    }
}
