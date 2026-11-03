using System;
using System.Collections.Generic;
using System.Numerics;
using FlyPhotos.Display.State;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Draws bitmaps at extreme zoom without the float view matrix. Through it, an image's screen coordinates reach
/// 1e9 px, where a float only resolves steps of 32-128 px; Direct2D draws a large image in pieces, each edge is
/// rounded on its own, and gaps open between them that show the window behind.
/// <para>
/// Here only what is on screen is drawn, and every number Direct2D gets is small: the source pixels in view are
/// copied (with a margin for the interpolation filter) into a small patch bitmap, so texture coordinates span a
/// few texels instead of the whole image, and the patch is drawn onto just the visible rectangle, with a
/// transform anchored at its corner and composed in double. A renderer owns one instance, calls
/// <see cref="BeginFrame"/> at the start of every Draw and disposes it with itself.
/// </para>
/// </summary>
internal sealed class PreciseImageDrawing : IDisposable
{
    /// <summary>
    /// Beyond this many pixels of image on screen (width or height, 2^20), the float view matrix is off by more
    /// than a tenth of a pixel; below it the renderers draw as they always did.
    /// </summary>
    private const double ExtentPixels = 1 << 20;

    /// <summary>Source pixels copied beyond the visible ones, for the interpolation filter's neighbours.</summary>
    private const int MarginPixels = 2;

    /// <summary>Patches by source image and region, with the frame each was last drawn in.</summary>
    private readonly Dictionary<(ICanvasImage Image, RectD Region), (CanvasRenderTarget Patch, long LastUsed)> _patches = [];
    private readonly List<(ICanvasImage, RectD)> _stale = [];
    private long _frame;

    public static bool IsNeeded(CanvasViewState viewState) =>
        Math.Max(viewState.ImageRect.Width, viewState.ImageRect.Height) * viewState.Scale > ExtentPixels;

    /// <summary>The visible part of the image on <paramref name="canvas"/>, in image pixels.</summary>
    public static RectD VisibleArea(CanvasViewState viewState, CanvasAnimatedControl canvas, CanvasDrawingSession session)
    {
        // CanvasAnimatedControl.Size is safe to read from the game-loop thread; in DIPs, so scale to device pixels.
        var size = canvas.Size;
        var dpiScale = session.Dpi / 96.0;
        return View(viewState).VisibleArea(size.Width * dpiScale, size.Height * dpiScale,
            viewState.ImageRect.Width, viewState.ImageRect.Height);
    }

    /// <summary>
    /// Starts a frame: frees the patches not drawn in the previous one. Those of the frame before last are no
    /// longer in use by Direct2D, as that frame has been presented.
    /// </summary>
    public void BeginFrame()
    {
        _frame++;
        foreach (var (key, entry) in _patches)
            if (entry.LastUsed < _frame - 1)
                _stale.Add(key);
        foreach (var key in _stale)
        {
            _patches[key].Patch.Dispose();
            _patches.Remove(key);
        }
        _stale.Clear();
    }

    /// <summary>
    /// Draws a whole-image renderer's frame: the checkerboard (when <paramref name="checkeredBrush"/> is given) and
    /// <paramref name="src"/> of <paramref name="image"/> over the image rectangle, both limited to what is visible.
    /// <paramref name="contentChanges"/> is for an image whose pixels change between frames (an animation surface).
    /// </summary>
    public void DrawWholeImage(CanvasDrawingSession session, CanvasViewState viewState, CanvasAnimatedControl canvas,
        ICanvasImage image, Rect src, CanvasImageBrush checkeredBrush, CanvasImageInterpolation quality,
        bool contentChanges = false)
    {
        var visible = VisibleArea(viewState, canvas, session);
        if (checkeredBrush != null) FillCheckered(session, viewState, visible, checkeredBrush);
        DrawImage(session, viewState, visible, image, viewState.ImageRect, src, 1f, quality, contentChanges);
    }

    /// <summary>
    /// Draws the part of <paramref name="image"/>'s <paramref name="src"/> (drawn onto <paramref name="dest"/>, in
    /// image pixels) that covers <paramref name="visible"/>. Leaves the session transform at the view's.
    /// </summary>
    public void DrawImage(CanvasDrawingSession session, CanvasViewState viewState, RectD visible,
        ICanvasImage image, Rect dest, Rect src, float opacity, CanvasImageInterpolation quality,
        bool contentChanges = false)
    {
        var destD = ToRectD(dest);
        if (!PreciseRegion.TryClip(destD, ToRectD(src), visible, MarginPixels, out var srcRegion, out var destRegion))
            return;
        var shown = destD.Intersect(visible);
        var patch = Patch(session, image, srcRegion, contentChanges);

        // The visible rectangle in the patch's texels: the patch starts at srcRegion.Min.
        var kx = srcRegion.Width / destRegion.Width;
        var ky = srcRegion.Height / destRegion.Height;
        var patchSrc = new Rect((shown.MinX - destRegion.MinX) * kx, (shown.MinY - destRegion.MinY) * ky,
            shown.Width * kx, shown.Height * ky);

        session.Transform = TransformAt(viewState, shown.MinX, shown.MinY);
        session.DrawImage(patch, new Rect(0, 0, shown.Width, shown.Height), patchSrc, opacity, quality);
        session.Transform = viewState.Mat;
    }

    /// <summary>
    /// Fills the visible part of the image with the checkerboard, keeping the pattern where the float path would
    /// put it (fixed in screen size, anchored at the image origin). Leaves the session transform at the view's.
    /// </summary>
    public static void FillCheckered(CanvasDrawingSession session, CanvasViewState viewState, RectD visible,
        CanvasImageBrush brush)
    {
        if (visible.IsEmpty) return;
        session.Transform = TransformAt(viewState, visible.MinX, visible.MinY);
        // The brush maps pattern space to image pixels with 1/scale, so the region (starting at visible.Min) starts
        // visible.Min * scale pattern units into the pattern. Only that offset modulo the pattern's period matters,
        // so that is all a float gets: pattern = local * scale + phase, i.e. local = (pattern - phase) / scale.
        var period = brush.Image is CanvasBitmap pattern ? pattern.SizeInPixels : default;
        var scale = (double)viewState.Scale;
        var phaseX = period.Width > 0 ? PreciseRegion.PatternPhase(visible.MinX * scale, period.Width) : 0;
        var phaseY = period.Height > 0 ? PreciseRegion.PatternPhase(visible.MinY * scale, period.Height) : 0;
        brush.Transform = Matrix3x2.CreateTranslation((float)-phaseX, (float)-phaseY)
                          * Matrix3x2.CreateScale(1f / viewState.Scale);
        session.FillRectangle(new Rect(0, 0, visible.Width, visible.Height), brush);
        session.Transform = viewState.Mat;
    }

    /// <summary>
    /// A copy of <paramref name="region"/> of <paramref name="image"/>, made on first use and kept while drawn;
    /// re-copied once per frame when <paramref name="contentChanges"/>.
    /// </summary>
    private CanvasRenderTarget Patch(CanvasDrawingSession session, ICanvasImage image, RectD region, bool contentChanges)
    {
        var key = (image, region);
        if (_patches.TryGetValue(key, out var entry))
        {
            if (contentChanges && entry.LastUsed != _frame) Copy(image, region, entry.Patch);
            _patches[key] = (entry.Patch, _frame);
            return entry.Patch;
        }

        var patch = new CanvasRenderTarget(session.Device, (float)Math.Ceiling(region.Width),
            (float)Math.Ceiling(region.Height), 96);
        Copy(image, region, patch);
        _patches[key] = (patch, _frame);
        return patch;
    }

    /// <summary>Copies whole source pixels (the region is rounded out to them) 1:1 without filtering.</summary>
    private static void Copy(ICanvasImage image, RectD region, CanvasRenderTarget patch)
    {
        using var ds = patch.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        ds.DrawImage(image, new Rect(0, 0, region.Width, region.Height),
            new Rect(region.MinX, region.MinY, region.Width, region.Height), 1f,
            CanvasImageInterpolation.NearestNeighbor);
    }

    private static PreciseView View(CanvasViewState viewState) =>
        PreciseView.Compose(viewState.ImageRect.Width, viewState.ImageRect.Height, viewState.Scale,
            viewState.Rotation, viewState.ImagePos.X, viewState.ImagePos.Y, viewState.SnapTranslation);

    /// <summary>The view's transform for coordinates relative to the image point (x, y), composed in double.</summary>
    private static Matrix3x2 TransformAt(CanvasViewState viewState, double x, double y)
    {
        var view = View(viewState);
        var (offsetX, offsetY) = view.ToScreen(x, y);
        return new Matrix3x2((float)view.M11, (float)view.M12, (float)view.M21, (float)view.M22,
            (float)offsetX, (float)offsetY);
    }

    private static RectD ToRectD(Rect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

    public void Dispose()
    {
        foreach (var entry in _patches.Values) entry.Patch.Dispose();
        _patches.Clear();
    }
}
