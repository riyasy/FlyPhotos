using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using static FlyPhotos.Display.ImageReading.SvgScene;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Draws an <see cref="SvgHqDisplayItem"/> as vectors at the current zoom, without replaying the vectors every
/// frame. Once the view has settled, the visible part of the SVG (plus a margin) is drawn once into a
/// screen-resolution bitmap, the <em>detail</em>; every frame then just draws that bitmap, so panning and zooming
/// cost a bitmap draw, as on the old 2000 px raster path. A whole-image bitmap, the <em>base</em>, fills whatever
/// the detail doesn't cover while a pan or zoom is in progress. The detail is redrawn sharp
/// <see cref="SettleMs"/> after the view stops moving at a new scale or outside the detail. Every vector draw is
/// spread over frames a time-boxed step at a time, so a heavy SVG never blocks the view.
/// Runs on the W2D thread only.
/// </summary>
/// <param name="canvas">The main canvas; its device is used for the bitmaps and its size gives the visible area.</param>
/// <param name="item">The SVG to draw; owns the scene (the renderer owns its bitmaps).</param>
/// <param name="supportsTransparency">Draw the checkerboard under the SVG when that setting is on.</param>
/// <param name="invalidate">Requests another frame (used to redraw sharp once the view settles).</param>
internal partial class SvgVectorRenderer(CanvasAnimatedControl canvas, SvgHqDisplayItem item,
    bool supportsTransparency, Action invalidate) : IRenderer
{
    /// <summary>How long the view must stay still before the detail is redrawn (a vector draw can take a second).</summary>
    private const int SettleMs = 150;
    /// <summary>The detail extends this fraction of the canvas size past each edge, so short pans need no redraw.</summary>
    private const float Margin = 0.25f;
    /// <summary>Drawing time per frame for a redraw; the rest of the frame stays free for pan and zoom.</summary>
    private const double StepBudgetMs = 15;
    /// <summary>
    /// Drawing time for the first frame, which has nothing else to show: most SVGs open complete in it, and a
    /// heavy one shows what is drawn so far, filling in over the next frames.
    /// </summary>
    private const double FirstFrameBudgetMs = 100;

    /// <summary>A bitmap of (part of) the SVG and where it sits: <see cref="ImageToPx"/> maps HQ image pixels to its pixels.</summary>
    private sealed record Snapshot(CanvasRenderTarget Bitmap, Matrix3x2 ImageToPx, float Scale, CanvasImageInterpolation Quality)
    {
        /// <summary>The whole bitmap, in its pixels.</summary>
        public Rect Pixels => new(0, 0, Bitmap.SizeInPixels.Width, Bitmap.SizeInPixels.Height);

        /// <summary>The part of the image this bitmap covers, in HQ image pixels.</summary>
        public Rect Coverage => TransformRect(Pixels, Inverse(ImageToPx));
    }

    /// <summary>A bitmap still being drawn, a step per frame, and the snapshot it becomes.</summary>
    private sealed record Pending(SvgScene.Painter Painter, Snapshot Snap);

    /// <summary>
    /// This renderer's ownership of the scene, taken at construction (UI thread, where the HQ cache also disposes
    /// items) and dropped in <see cref="Dispose"/> (W2D thread), so the cache dropping the item can't free the
    /// scene mid-draw. Null if the item was already disposed; nothing is drawn then.
    /// </summary>
    private readonly SvgScene.Lease _lease = item.Scene.TryAcquire();
    /// <summary>Set by <see cref="Dispose"/>, so a pending wake-up doesn't request frames for a dead renderer.</summary>
    private volatile bool _disposed;

    /// <summary>What one frame's drawing works from: the device, the view, and the scales derived from them.</summary>
    /// <param name="Viewport">The canvas in pixels.</param>
    /// <param name="K">SVG user units → HQ image pixels.</param>
    /// <param name="Target">SVG user units → screen pixels at this view.</param>
    private readonly record struct Frame(CanvasDevice Device, CanvasViewState View, Rect Viewport, float K, float Target,
        CanvasImageInterpolation Quality);

    /// <summary>Checkerboard brush drawn under transparent images; set on the W2D thread at install.</summary>
    private CanvasImageBrush _checkeredBrush;
    /// <summary>Set by CanvasController on the W2D thread when the renderer is installed.</summary>
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }

    /// <summary>The visible area at screen resolution; null until the first one is drawn.</summary>
    private Snapshot _detail;
    /// <summary>The next detail, while it is being drawn.</summary>
    private Pending _pending;
    /// <summary>The whole image, at least at fit size; may be the same object as <see cref="_detail"/>.</summary>
    private Snapshot _base;
    /// <summary>A fit-size base still being drawn; it takes each frame's drawing time until done.</summary>
    private Pending _basePending;

    /// <summary>The view transform of the last frame, and when it last changed (Stopwatch ticks).</summary>
    private Matrix3x2 _lastMat;
    private long _lastChange;
    /// <summary>1 while a delayed wake-up for the settle redraw is pending.</summary>
    private int _wakePending;

    /// <summary>
    /// Draws the checkerboard (if enabled) and the SVG. Redraws the detail when it doesn't match the view and the
    /// view has settled (or there is none yet); otherwise draws the bitmaps it has. <paramref name="session"/>'s
    /// transform maps the HQ image pixels (<see cref="CanvasViewState.ImageRect"/>) to the screen.
    /// </summary>
    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        if (_lease == null) return;
        session.Units = CanvasUnits.Pixels;
        if (AppConfig.Settings.CheckeredBackground && supportsTransparency)
        {
            session.Antialiasing = CanvasAntialiasing.Aliased;
            _checkeredBrush.Transform = Matrix3x2.CreateScale(1f / viewState.Scale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }

        var now = Stopwatch.GetTimestamp();
        if (viewState.Mat != _lastMat) { _lastMat = viewState.Mat; _lastChange = now; }

        // ImageRect is in the HQ image's pixels; k converts SVG user units to those.
        var k = (float)(viewState.ImageRect.Width / item.Scene.Width);
        var target = k * viewState.Scale;
        var viewport = Viewport(session);
        var visible = TransformRect(viewport, viewState.MatInv);
        visible.Intersect(viewState.ImageRect);
        var frame = new Frame(session.Device, viewState, viewport, k, target, quality);

        var fresh = Matches(_detail, visible, target, quality, isAnimating);
        if (_basePending != null)
        {
            // The base goes first: without it a zoom out or a pan past the detail shows nothing around it.
            // Detail redraws wait for it.
            if (_basePending.Painter.Step(StepBudgetMs))
            {
                ReplaceBase(_basePending.Snap);
                _basePending = null;
            }
            invalidate();
        }
        else if (!fresh)
        {
            var waited = Stopwatch.GetElapsedTime(_lastChange, now).TotalMilliseconds;
            if (_detail == null || (!isAnimating && waited >= SettleMs))
            {
                // The first detail is finished even if the view moves meanwhile (the open zoom), as there is
                // nothing else to show; a later one restarts when it no longer matches the view.
                if (_detail == null ? _pending == null : !Matches(_pending?.Snap, visible, target, quality, false))
                    StartPending(frame);
                if (StepFor(_pending.Painter, _detail == null ? FirstFrameBudgetMs : StepBudgetMs))
                {
                    Promote(frame);
                    fresh = true;
                }
                else invalidate();
            }
            else if (!isAnimating) WakeAfter(SettleMs - (int)waited);
        }

        var shown = _detail ?? _pending.Snap; // the first detail, part drawn
        var saved = session.Transform;
        session.Antialiasing = CanvasAntialiasing.Aliased; // bitmap edges; the pieces below must meet without seams
        if (!fresh && _base != null && !ReferenceEquals(_base, shown))
            DrawBaseAround(session, shown.Coverage, saved, quality);
        // Snapped only when shown alone, so it can't open a seam against the base.
        DrawSnapshot(session, shown, shown.Pixels, saved, quality, snap: fresh);
        session.Transform = saved;
    }

    /// <summary>
    /// Whether <paramref name="s"/> can be shown as is: drawn at <paramref name="target"/> px per user unit and
    /// covering <paramref name="visible"/>. Animation frames pass a cheaper quality, so only at rest does the
    /// quality have to match.
    /// </summary>
    private static bool Matches(Snapshot s, Rect visible, float target, CanvasImageInterpolation quality, bool isAnimating) =>
        s != null && (isAnimating || s.Quality == quality) && MathF.Abs(target / s.Scale - 1f) <= 0.001f
        && (visible.IsEmpty || Contains(s.Coverage, visible));

    /// <summary>Steps <paramref name="p"/> for about <paramref name="budgetMs"/>; whether it is done.</summary>
    private static bool StepFor(SvgScene.Painter p, double budgetMs)
    {
        var sw = Stopwatch.StartNew();
        while (!p.Step(StepBudgetMs))
            if (sw.Elapsed.TotalMilliseconds >= budgetMs) return false;
        return true;
    }

    /// <summary>Starts drawing the visible image area plus <see cref="Margin"/> at the current view into a new bitmap.</summary>
    private void StartPending(in Frame f)
    {
        var vp = f.Viewport;
        var imageOnScreen = TransformRect(f.View.ImageRect, f.View.Mat);
        var region = new Rect(vp.X - vp.Width * Margin, vp.Y - vp.Height * Margin,
            vp.Width * (1 + 2 * Margin), vp.Height * (1 + 2 * Margin));
        region.Intersect(imageOnScreen);
        if (region.IsEmpty) region = new Rect(0, 0, 1, 1); // image off screen: a 1 px placeholder
        int x0 = (int)Math.Floor(region.X), y0 = (int)Math.Floor(region.Y);

        Drop(_pending);
        _pending = Begin(f, f.View.Mat * Matrix3x2.CreateTranslation(-x0, -y0), region.Right - x0, region.Bottom - y0, f.Target);
    }

    /// <summary>
    /// Shows the finished pending bitmap as the detail, and sees to the base: the detail itself when it holds the
    /// whole image at fit size or larger, else (if there is none) a fit-size render, drawn in later frames.
    /// </summary>
    private void Promote(in Frame f)
    {
        var old = _detail;
        _detail = _pending.Snap;
        _pending = null;
        Release(old);

        // Fit the whole image into the canvas size, as the view would be at fit. A whole-image detail zoomed out
        // further would be too small to fill in around a later zoomed-in detail.
        var image = f.View.ImageRect;
        var fit = (float)Math.Min(f.Viewport.Width / image.Width, f.Viewport.Height / image.Height);
        if (Contains(_detail.Coverage, image) && _detail.Scale >= f.K * fit * 0.999f)
        {
            Drop(_basePending);
            _basePending = null;
            ReplaceBase(_detail);
        }
        else if (_base == null && _basePending == null)
            _basePending = Begin(f, Matrix3x2.CreateScale(fit), image.Width * fit, image.Height * fit, f.K * fit);
    }

    /// <summary>
    /// A new bitmap of <paramref name="w"/>×<paramref name="h"/> pixels (rounded up, within the device's limit) for
    /// HQ image pixels mapped by <paramref name="imageToPx"/>, at <paramref name="scale"/> px per user unit, and a
    /// painter to fill it.
    /// </summary>
    private Pending Begin(in Frame f, Matrix3x2 imageToPx, double w, double h, float scale)
    {
        var max = f.Device.MaximumBitmapSizeInPixels;
        var rt = new CanvasRenderTarget(f.Device, Math.Clamp((int)Math.Ceiling(w), 1, max),
            Math.Clamp((int)Math.Ceiling(h), 1, max), 96f);
        var painter = new SvgScene.Painter(item.Scene, rt, Matrix3x2.CreateScale(f.K) * imageToPx, f.Quality);
        return new Pending(painter, new Snapshot(rt, imageToPx, scale, f.Quality));
    }

    /// <summary>Disposes an unfinished bitmap and its painter.</summary>
    private static void Drop(Pending p)
    {
        if (p == null) return;
        p.Painter.Dispose();
        p.Snap.Bitmap.Dispose();
    }

    /// <summary>Makes <paramref name="s"/> the base, disposing the old base unless it is still the detail.</summary>
    private void ReplaceBase(Snapshot s)
    {
        var old = _base;
        _base = s;
        Release(old);
    }

    /// <summary>Disposes <paramref name="s"/>'s bitmap unless it is still the detail or the base.</summary>
    private void Release(Snapshot s)
    {
        if (s != null && !ReferenceEquals(s, _detail) && !ReferenceEquals(s, _base)) s.Bitmap.Dispose();
    }

    /// <summary>
    /// Draws the base everywhere except <paramref name="hole"/> (HQ image pixels), so transparent SVGs aren't drawn
    /// twice where the detail goes on top. View rotations are in 90° steps, so the hole stays a rectangle.
    /// </summary>
    private void DrawBaseAround(CanvasDrawingSession session, Rect hole, Matrix3x2 view, CanvasImageInterpolation quality)
    {
        var all = _base.Pixels;
        var h = TransformRect(hole, _base.ImageToPx);
        h.Intersect(all);
        if (h.IsEmpty) { DrawSnapshot(session, _base, all, view, quality); return; }
        DrawSnapshot(session, _base, new Rect(0, 0, all.Width, h.Top), view, quality);
        DrawSnapshot(session, _base, new Rect(0, h.Bottom, all.Width, all.Height - h.Bottom), view, quality);
        DrawSnapshot(session, _base, new Rect(0, h.Top, h.Left, h.Height), view, quality);
        DrawSnapshot(session, _base, new Rect(h.Right, h.Top, all.Width - h.Right, h.Height), view, quality);
    }

    /// <summary>
    /// Draws the <paramref name="part"/> (its pixels) of <paramref name="s"/> where it belongs under the current
    /// <paramref name="view"/>; with <paramref name="snap"/>, on whole screen pixels when it maps 1:1.
    /// </summary>
    private static void DrawSnapshot(CanvasDrawingSession session, Snapshot s, Rect part, Matrix3x2 view,
        CanvasImageInterpolation quality, bool snap = false)
    {
        if (part.Width <= 0 || part.Height <= 0) return;
        var m = Inverse(s.ImageToPx) * view;
        session.Transform = snap ? SnapToPixels(m) : m;
        session.DrawImage(s.Bitmap, part, part, 1f, quality);
    }

    /// <summary>
    /// <paramref name="m"/> with its offset rounded to whole pixels when it maps bitmap pixels 1:1 onto screen
    /// pixels (in 90° steps): a pan leaves a fractional offset at DPI scales other than 100%, which would
    /// resample the bitmap soft.
    /// </summary>
    private static Matrix3x2 SnapToPixels(Matrix3x2 m)
    {
        static bool Whole(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f;
        if (!(Whole(m.M11) && Whole(m.M12) && Whole(m.M21) && Whole(m.M22))) return m;
        return new Matrix3x2(MathF.Round(m.M11), MathF.Round(m.M12), MathF.Round(m.M21), MathF.Round(m.M22),
            MathF.Round(m.M31), MathF.Round(m.M32));
    }

    /// <summary>Requests a frame in <paramref name="ms"/> ms (one pending at a time), for the settle redraw.</summary>
    private void WakeAfter(int ms)
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 1) return;
        _ = Task.Delay(Math.Max(1, ms)).ContinueWith(_ =>
        {
            Volatile.Write(ref _wakePending, 0);
            if (!_disposed) invalidate();
        });
    }

    /// <summary>The canvas in pixels. CanvasAnimatedControl.Size is safe to read on the game-loop thread.</summary>
    private Rect Viewport(CanvasDrawingSession session)
    {
        var size = canvas.Size;
        var dpi = session.Dpi / 96f;
        return new Rect(0, 0, size.Width * dpi, size.Height * dpi);
    }

    /// <summary>Whether <paramref name="outer"/> contains <paramref name="inner"/>, within half a pixel.</summary>
    private static bool Contains(Rect outer, Rect inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5 &&
        inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    private static Matrix3x2 Inverse(Matrix3x2 m) => Matrix3x2.Invert(m, out var inv) ? inv : Matrix3x2.Identity;

    /// <summary>The next frame at rest sees the new quality and redraws with it.</summary>
    public void HandleScalingMethodChange() => invalidate();

    /// <summary>Disposes the bitmaps and drops this renderer's ownership of the scene.</summary>
    public void Dispose()
    {
        _disposed = true;
        Drop(_pending);
        Drop(_basePending);
        _pending = _basePending = null;
        var (d, b) = (_detail, _base);
        _detail = _base = null;
        Release(d);
        if (!ReferenceEquals(b, d)) Release(b);
        _lease?.Dispose();
    }
}
