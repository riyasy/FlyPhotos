using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Renderer for SVGs kept as a retained vector display list, replayed at every scale so edges stay sharp and
/// there is no bitmap/vector seam while zooming. Only items that intersect the view are drawn. While a heavy SVG
/// (or any SVG whose animation frames turn out slow) is being panned/zoomed, it draws the 2000 px bitmap through a
/// <see cref="StaticImageRenderer"/> (mip chain included) instead, to keep animation smooth.
/// The display list and the bitmap belong to the <see cref="SvgVectorHqDisplayItem"/>, not to this renderer.
/// </summary>
internal partial class SvgVectorRenderer : IRenderer
{
    /// <summary>A gap between two animating draws longer than this means the vector frames are too slow.</summary>
    private const double SlowAnimationIntervalMs = 40;

    /// <summary>Antialiasing spill beyond an item's geometry, in device pixels; the culling view is padded by it.</summary>
    private const float CullPaddingPixels = 2;

    /// <summary>
    /// An item on screen larger than this many canvas widths or heights is drawn from its path pre-chopped to the
    /// view instead of its display list: Direct2D widens a whole huge stroke (or tessellates a whole huge fill)
    /// every frame, even though only a sliver of it is visible, which can take seconds.
    /// </summary>
    private const float HugeItemFactor = 4;

    /// <summary>
    /// When the whole SVG is larger than this many pixels in width or height (2^20), a float transform can't place
    /// anything on screen exactly any more, so every item with a raw shape is drawn rebased near the view, huge or not.
    /// </summary>
    private const double RebaseExtentPixels = 1 << 20;

    /// <summary>
    /// A huge item's path is flattened with an error of the view's diagonal over this, and the same length is the
    /// margin of the uniform-window tests.
    /// </summary>
    private const double UniformToleranceDivisor = 64;

    private readonly CanvasAnimatedControl _canvas;
    /// <summary>The image being drawn: its display list and its fallback bitmap.</summary>
    private readonly SvgVectorHqDisplayItem _item;
    /// <summary>Whether the image may have alpha, so the checkerboard is drawn under it.</summary>
    private readonly bool _supportsTransparency;
    /// <summary>Draws the bitmap when the vector path isn't used; owns the mip chain.</summary>
    private readonly StaticImageRenderer _bitmapRenderer;

    /// <summary>When the previous animating Draw started (Stopwatch ticks); 0 when the last Draw was at rest.</summary>
    private long _lastAnimatingDraw;
    /// <summary>Set once an animating frame came late; the bitmap is drawn until the animation ends.</summary>
    private bool _slowAnimationDetected;
    /// <summary>Rebased geometry and its brush per raw-shape item (by index in the item array), made on first use.</summary>
    private readonly Dictionary<int, RawShape> _rawShapes = [];

    /// <summary>Checkerboard brush drawn under transparent images.</summary>
    private CanvasImageBrush _checkeredBrush;
    /// <summary>Set by CanvasController on the W2D thread when the renderer is installed.</summary>
    public CanvasImageBrush CheckeredBrush
    {
        set
        {
            _checkeredBrush = value;
            _bitmapRenderer.CheckeredBrush = value;
        }
    }

    public SvgVectorRenderer(CanvasAnimatedControl canvas, SvgVectorHqDisplayItem item,
        bool supportsTransparency, Action invalidateCanvas)
    {
        _canvas = canvas;
        _item = item;
        _supportsTransparency = supportsTransparency;
        // The mip chain serves the animation fallback, which any SVG can end up using if its frames are slow.
        _bitmapRenderer = new StaticImageRenderer(canvas, item.Bitmap, supportsTransparency, invalidateCanvas,
            generateMipChain: true);
    }

    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        // Frames are drawn back to back while animating, so a long gap between two means the vectors are too slow.
        if (isAnimating)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastAnimatingDraw != 0 && Stopwatch.GetElapsedTime(_lastAnimatingDraw, now).TotalMilliseconds > SlowAnimationIntervalMs)
                _slowAnimationDetected = true;
            _lastAnimatingDraw = now;
        }
        else
        {
            _lastAnimatingDraw = 0;
            _slowAnimationDetected = false;
        }

        // Replaying a heavy or slow display list every animation frame drops frames; the bitmap stands in until rest.
        if ((_item.IsHeavy || _slowAnimationDetected) && isAnimating)
        {
            _bitmapRenderer.Draw(session, viewState, quality, isAnimating);
            return;
        }

        session.Units = CanvasUnits.Pixels;
        if (AppConfig.Settings.CheckeredBackground && _supportsTransparency)
        {
            session.Antialiasing = CanvasAntialiasing.Aliased;
            _checkeredBrush.Transform = Matrix3x2.CreateScale(1f / viewState.Scale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }
        session.Antialiasing = CanvasAntialiasing.Antialiased;

        // The display list is in SVG user units; the view maps image (bitmap) pixels to the screen. The view's
        // float Mat can't be used: at extreme zoom the screen coordinates reach 1e8 px, where a float is off by
        // tens of pixels, so the same transform is composed again in double.
        var imageRect = viewState.ImageRect;
        var toImage = SvgAffine.Scale(imageRect.Width / _item.SvgWidth, imageRect.Height / _item.SvgHeight);
        var svgToScreen = toImage * SvgAffine.ComposeView(imageRect.Width, imageRect.Height, viewState.Scale,
            viewState.Rotation, viewState.ImagePos.X, viewState.ImagePos.Y, viewState.SnapTranslation);
        var svgToScreenFloat = svgToScreen.ToMatrix3x2();
        session.Transform = svgToScreenFloat;

        // CanvasAnimatedControl.Size is safe to read from the game-loop thread; read it once.
        var size = _canvas.Size;
        var dpiScale = session.Dpi / 96f;
        var canvasWidth = (float)size.Width * dpiScale;
        var canvasHeight = (float)size.Height * dpiScale;

        // Beyond this zoom even small items are drawn from their raw shape, rebased near the view (see DrawShape).
        // Items without a shape (layers) still replay their display list, which float transforms can't place exactly.
        var svgOnScreen = svgToScreen.TransformBounds(new SvgRectD(0, 0, _item.SvgWidth, _item.SvgHeight));
        var rebase = Math.Max(svgOnScreen.MaxX - svgOnScreen.MinX, svgOnScreen.MaxY - svgOnScreen.MinY) > RebaseExtentPixels;

        // Linear scan; a spatial index could replace it if the item count ever makes this show up in a profile.
        var visible = VisibleSvgRect(svgToScreen, canvasWidth, canvasHeight);
        for (var i = 0; i < _item.Items.Length; i++)
        {
            var item = _item.Items[i];
            if (!Intersects(item.Bounds, visible)) continue;
            var drawnFromShape = false;
            if (item.Shape != null)
            {
                var huge = IsHuge(item.Bounds, svgToScreen, canvasWidth, canvasHeight);
                if (huge || rebase)
                {
                    drawnFromShape = DrawShape(i, item.Shape, session, svgToScreen, canvasWidth, canvasHeight, huge);
                    session.Transform = svgToScreenFloat;
                }
            }
            if (!drawnFromShape)
                session.DrawImage(item.Commands);
        }
        session.Transform = viewState.Mat;
    }

    /// <summary>Whether the item's bounding box on screen exceeds <see cref="HugeItemFactor"/> canvases in width or height.</summary>
    private static bool IsHuge(Rect bounds, SvgAffine svgToScreen, float canvasWidth, float canvasHeight)
    {
        var box = svgToScreen.TransformBounds(new SvgRectD(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        return box.MaxX - box.MinX > HugeItemFactor * canvasWidth || box.MaxY - box.MinY > HugeItemFactor * canvasHeight;
    }

    /// <summary>
    /// Draws a fill or stroke from its raw path instead of its display list, in double precision and relative to
    /// the view. The path is in the shape's local space; it is rebased onto an anchor (the centre of the visible
    /// area, in local coordinates) in double and only then cast to float, and the session transform places that
    /// anchor on the screen, so every float involved is small however far the view is from the SVG's origin.
    /// With <paramref name="chop"/> (the item is huge on screen) the path is first pre-chopped to the view (see
    /// <see cref="SvgPathChopper"/>), which looks the same inside the view but is far cheaper for Direct2D, and
    /// a window that turns out to be uniformly painted or empty is drawn as one rectangle or not at all (see
    /// <see cref="SvgUniformCoverage"/>). The geometry is cached until the view or the canvas size changes.
    /// Returns false, drawing nothing, if the shape's transform can't be inverted; the caller then draws the
    /// display list.
    /// </summary>
    private bool DrawShape(int index, SvgShape shape, CanvasDrawingSession session, SvgAffine svgToScreen,
        float canvasWidth, float canvasHeight, bool chop)
    {
        // The shape's path is in its own local space, mapped to the screen by the op transform, then the view.
        var localToScreen = SvgAffine.From(shape.Transform) * svgToScreen;
        if (!localToScreen.TryInvert(out var screenToLocal))
            return false;

        if (!_rawShapes.TryGetValue(index, out var entry))
        {
            entry = new RawShape
            {
                Paint = SvgCommandListBuilder.CreatePaint(_canvas, shape.Paint),
                StrokeStyle = shape.IsStroke ? SvgCommandListBuilder.CreateStrokeStyle(shape) : null
            };
            _rawShapes[index] = entry;
        }

        if (!entry.IsCurrent(svgToScreen, canvasWidth, canvasHeight))
        {
            // The visible area in local coordinates (the canvas padded by CullPaddingPixels, which covers the
            // antialiasing spill), also for rotated, skewed or non-uniformly scaled transforms.
            var view = screenToLocal.TransformBounds(new SvgRectD(-CullPaddingPixels, -CullPaddingPixels,
                canvasWidth + CullPaddingPixels, canvasHeight + CullPaddingPixels));
            var anchor = view.Center;

            SvgPathData? path;
            var coverage = SvgCoverage.Partial;
            if (chop)
            {
                // Dropping or clamping parts of a dashed stroke would shift the dash phase of the rest.
                var solidStroke = shape.IsStroke && shape.Dashes.Length == 0;
                var mode = !shape.IsStroke ? SvgChopMode.Fill : solidStroke ? SvgChopMode.Stroke : SvgChopMode.DashedStroke;
                // A stroke also reaches beyond its path by its width, joins and caps.
                var cull = shape.IsStroke ? view.Inflate(shape.StrokeOutset) : view;
                path = SvgPathChopper.Chop(shape.Verbs, shape.Points, cull.Min, cull.Max, mode);
                var tolerance = view.Diagonal / UniformToleranceDivisor;
                if (path is { } chopped && (!shape.IsStroke || solidStroke) && tolerance > 0)
                {
                    var figures = SvgUniformCoverage.Flatten(chopped.Verbs, chopped.Points, tolerance);
                    coverage = shape.IsStroke
                        ? SvgUniformCoverage.ClassifyStroke(figures, view, tolerance, shape.Width / 2.0, shape.StrokeOutset)
                        : SvgUniformCoverage.ClassifyFill(figures, view, tolerance, evenOdd: shape.FillRule != 0);
                }
            }
            else
            {
                path = new SvgPathData(shape.Verbs, Array.ConvertAll(shape.Points, p => (double)p));
            }

            // Nothing is built for a uniform window (it is one rectangle or nothing) or when no path is left.
            var geometry = path is { } built && coverage == SvgCoverage.Partial
                ? SvgCommandListBuilder.BuildPath(_canvas, built.Verbs, Rebase(built.Points, anchor), shape.FillRule)
                : null;
            entry.Update(geometry, coverage, anchor, view, svgToScreen, canvasWidth, canvasHeight);
        }

        // Nothing of the item is in the view.
        if (entry.Coverage == SvgCoverage.Outside) return true;
        if (entry.Coverage == SvgCoverage.Partial && entry.Geometry == null) return true;

        // The geometry is relative to the anchor: place the anchor on the screen, computed in double.
        var anchorX = entry.Anchor.X;
        var anchorY = entry.Anchor.Y;
        session.Transform = (SvgAffine.Translation(anchorX, anchorY) * localToScreen).ToMatrix3x2();
        // The brush lives in the shape's local space, which is now shifted by the anchor.
        if (entry.Paint.Brush is { } brush)
            brush.Transform = shape.Paint.Transform * Matrix3x2.CreateTranslation((float)-anchorX, (float)-anchorY);
        session.Antialiasing = shape.Aliased ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (entry.Coverage == SvgCoverage.Inside)
        {
            var window = entry.View;
            entry.Paint.FillRectangle(session, new Rect(window.MinX - anchorX, window.MinY - anchorY,
                window.MaxX - window.MinX, window.MaxY - window.MinY));
        }
        else if (shape.IsStroke)
        {
            entry.Paint.Stroke(session, entry.Geometry, shape.Width, entry.StrokeStyle);
        }
        else
        {
            entry.Paint.Fill(session, entry.Geometry);
        }
        session.Antialiasing = CanvasAntialiasing.Antialiased;
        return true;
    }

    /// <summary>The path's points relative to <paramref name="anchor"/>: subtracted in double, then cast to float.</summary>
    private static float[] Rebase(double[] points, SvgVec anchor)
    {
        var rebased = new float[points.Length];
        for (var i = 0; i < points.Length; i += 2)
        {
            rebased[i] = (float)(points[i] - anchor.X);
            rebased[i + 1] = (float)(points[i + 1] - anchor.Y);
        }
        return rebased;
    }

    /// <summary>The part of the SVG (in user units) the canvas shows, padded by <see cref="CullPaddingPixels"/>.</summary>
    private static Rect VisibleSvgRect(SvgAffine svgToScreen, float canvasWidth, float canvasHeight)
    {
        // Can't cull without an inverse (a degenerate transform draws nothing anyway); draw everything.
        if (!svgToScreen.TryInvert(out var screenToSvg))
            return new Rect(-1e30f, -1e30f, 2e30f, 2e30f);

        // Take the bounding box of the transformed corners, so a rotated view is covered.
        var box = screenToSvg.TransformBounds(new SvgRectD(-CullPaddingPixels, -CullPaddingPixels,
            canvasWidth + CullPaddingPixels, canvasHeight + CullPaddingPixels));
        return new Rect(new Point(box.MinX, box.MinY), new Point(box.MaxX, box.MaxY));
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.X <= b.X + b.Width && b.X <= a.X + a.Width && a.Y <= b.Y + b.Height && b.Y <= a.Y + a.Height;

    public void HandleScalingMethodChange() => _bitmapRenderer.HandleScalingMethodChange();

    public void Dispose()
    {
        foreach (var entry in _rawShapes.Values) entry.Dispose();
        _rawShapes.Clear();
        _bitmapRenderer.Dispose();
    }

    /// <summary>
    /// A raw-shape item's brush and stroke style (kept for the renderer's lifetime) and what it was last built for:
    /// its geometry relative to <see cref="Anchor"/>, or a uniform <see cref="Coverage"/> of the view. Valid for one
    /// view transform and canvas size, which also fix the anchor.
    /// </summary>
    private sealed class RawShape : IDisposable
    {
        public SvgCommandListBuilder.ResolvedPaint Paint { get; init; }
        public CanvasStrokeStyle StrokeStyle { get; init; }
        /// <summary>
        /// Null when nothing of the item is left in the view or the view is uniform (<see cref="Coverage"/> is not
        /// <see cref="SvgCoverage.Partial"/>).
        /// </summary>
        public CanvasGeometry Geometry { get; private set; }
        public SvgCoverage Coverage { get; private set; }
        /// <summary>The geometry's origin and the view's centre, in the shape's local coordinates.</summary>
        public SvgVec Anchor { get; private set; }
        /// <summary>The visible area in the shape's local coordinates.</summary>
        public SvgRectD View { get; private set; }

        private bool _hasKey;
        private SvgAffine _svgToScreen;
        private float _canvasWidth;
        private float _canvasHeight;

        public bool IsCurrent(SvgAffine svgToScreen, float canvasWidth, float canvasHeight) =>
            _hasKey && _svgToScreen == svgToScreen && _canvasWidth == canvasWidth && _canvasHeight == canvasHeight;

        /// <summary>Replaces the geometry (disposing the old one) and records the view it was built for.</summary>
        public void Update(CanvasGeometry geometry, SvgCoverage coverage, SvgVec anchor, SvgRectD view,
            SvgAffine svgToScreen, float canvasWidth, float canvasHeight)
        {
            Geometry?.Dispose();
            Geometry = geometry;
            Coverage = coverage;
            Anchor = anchor;
            View = view;
            _hasKey = true;
            _svgToScreen = svgToScreen;
            _canvasWidth = canvasWidth;
            _canvasHeight = canvasHeight;
        }

        public void Dispose()
        {
            Geometry?.Dispose();
            StrokeStyle?.Dispose();
            Paint.Dispose();
        }
    }
}
