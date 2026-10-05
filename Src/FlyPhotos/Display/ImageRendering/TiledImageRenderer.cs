using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Renderer for images larger than the GPU's maximum bitmap size. Picks the pyramid level matching the
/// zoom, uploads only the visible tiles (a few per frame) into an LRU of GPU bitmaps, and draws the
/// overview underneath so tiles still uploading show blurry instead of blank.
/// Everything here runs on the W2D thread, so the tile cache needs no locking. The pyramid and the
/// overview bitmap belong to the <see cref="TiledHqDisplayItem"/>, not to this renderer.
/// </summary>
internal partial class TiledImageRenderer : IRenderer
{
    /// <summary>Tile uploads per frame. At ~4 MB each, this keeps a frame's upload cost to a few ms.</summary>
    private const int MaxUploadsPerFrame = 4;
    /// <summary>GPU tile cache size, ~400 MB of VRAM. Tiles on screen are never evicted, even past this.</summary>
    private const int MaxCachedTiles = 96;

    /// <summary>The canvas; its size gives the visible area.</summary>
    private readonly CanvasAnimatedControl _canvas;
    /// <summary>The image being drawn: its pyramid and overview bitmap.</summary>
    private readonly TiledHqDisplayItem _item;
    /// <summary>Whether the image may have alpha, so the checkerboard is drawn under it.</summary>
    private readonly bool _supportsTransparency;
    /// <summary>Requests another frame; used while tiles are still uploading.</summary>
    private readonly Action _invalidate;

    /// <summary>Checkerboard brush drawn under transparent images.</summary>
    private CanvasImageBrush _checkeredBrush;
    /// <summary>Set by CanvasController on the W2D thread when the renderer is installed.</summary>
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }

    /// <summary>Uploaded tiles by (level, col, row), with the frame each was last drawn in (for LRU eviction).</summary>
    private readonly Dictionary<(int Level, int Col, int Row), (CanvasBitmap Bitmap, long LastUsed)> _gpuTiles = [];
    /// <summary>Scratch list of visible tiles of the current frame's level that aren't uploaded yet.</summary>
    private readonly List<(int Col, int Row)> _missing = [];
    /// <summary>Frame counter; the LRU clock.</summary>
    private long _frame;

    /// <summary>Creates a renderer for <paramref name="item"/>. GPU tiles are uploaded lazily in <see cref="Draw"/>.</summary>
    public TiledImageRenderer(CanvasAnimatedControl canvas, TiledHqDisplayItem item,
        bool supportsTransparency, Action invalidate)
    {
        _canvas = canvas;
        _item = item;
        _supportsTransparency = supportsTransparency;
        _invalidate = invalidate;
    }

    /// <summary>
    /// Draws the visible part of the image from the pyramid level matching the zoom. Uploads up to
    /// <see cref="MaxUploadsPerFrame"/> missing tiles, fills the rest from the overview, and requests another
    /// frame while any remain. When zoomed out to the top level, draws just the overview.
    /// </summary>
    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        _frame++;
        session.Units = CanvasUnits.Pixels;
        // Aliased edges, otherwise adjacent tile rects leave hairline gaps where their AA edges meet.
        session.Antialiasing = CanvasAntialiasing.Aliased;

        if (AppConfig.Settings.CheckeredBackground && _supportsTransparency)
        {
            _checkeredBrush.Transform = Matrix3x2.CreateScale(1f / viewState.Scale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }

        var pyramid = _item.Pyramid;
        var overview = _item.Bitmap;
        // Same level choice as StaticImageRenderer.SelectBitmapForScale: never upscale from a coarser level.
        var k = viewState.Scale >= 1f ? 0 : (int)Math.Floor(Math.Log2(1.0 / viewState.Scale));
        k = Math.Clamp(k, 0, pyramid.Levels.Length - 1);
        if (k == pyramid.Levels.Length - 1) // the top level is the overview itself
        {
            session.DrawImage(overview, viewState.ImageRect, overview.Bounds, 1f, quality);
            return;
        }

        var level = pyramid.Levels[k];
        var f = 1 << k; // level pixel → full-image pixel
        var visible = VisibleImageRect(session, viewState);
        if (visible.IsEmpty) return;

        var c0 = Math.Max(0, (int)(visible.Left / f) / TileLevel.Size);
        var c1 = Math.Min(level.Cols - 1, (int)(visible.Right / f) / TileLevel.Size);
        var r0 = Math.Max(0, (int)(visible.Top / f) / TileLevel.Size);
        var r1 = Math.Min(level.Rows - 1, (int)(visible.Bottom / f) / TileLevel.Size);

        _missing.Clear();
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_gpuTiles, (k, c, r));
                if (Unsafe.IsNullRef(ref entry))
                {
                    _missing.Add((c, r));
                    continue;
                }
                entry.LastUsed = _frame;
                var (dest, src) = TileRects(level, f, c, r);
                session.DrawImage(entry.Bitmap, dest, src, 1f, quality);
            }

        // Upload a few missing tiles; stand in for the rest with the matching patch of the overview.
        // Only under missing tiles: drawn under present ones too, it would show through transparent pixels.
        var uploads = Math.Min(_missing.Count, MaxUploadsPerFrame);
        var overviewScale = overview.SizeInPixels.Width / (double)pyramid.FullWidth;
        for (var i = 0; i < _missing.Count; i++)
        {
            var (c, r) = _missing[i];
            var (dest, src) = TileRects(level, f, c, r);
            if (i < uploads)
            {
                var bmp = CanvasBitmap.CreateFromBytes(session.Device, level.Tile(c, r),
                    TileLevel.Stride, TileLevel.Stride, pyramid.Format, 96f);
                _gpuTiles[(k, c, r)] = (bmp, _frame);
                session.DrawImage(bmp, dest, src, 1f, quality);
            }
            else
            {
                var ovSrc = new Rect(dest.X * overviewScale, dest.Y * overviewScale,
                    dest.Width * overviewScale, dest.Height * overviewScale);
                session.DrawImage(overview, dest, ovSrc, 1f, quality);
            }
        }

        EvictOldTiles();
        if (_missing.Count > uploads) _invalidate(); // keep going next frame
    }

    /// <summary>
    /// Where tile (col, row) of a level lands in full-image pixels, and the part of its bitmap to sample
    /// (inside the gutter).
    /// </summary>
    private (Rect Dest, Rect Src) TileRects(TileLevel level, int f, int col, int row)
    {
        var x = col * TileLevel.Size;
        var y = row * TileLevel.Size;
        var w = Math.Min(TileLevel.Size, level.Width - x);
        var h = Math.Min(TileLevel.Size, level.Height - y);

        // Level sizes round up, so the last tile can overhang the full image by < f pixels; clip it.
        var destW = Math.Min(w * f, _item.Pyramid.FullWidth - x * f);
        var destH = Math.Min(h * f, _item.Pyramid.FullHeight - y * f);
        return (new Rect(x * f, y * f, destW, destH),
                new Rect(TileLevel.Gutter, TileLevel.Gutter, (double)destW / f, (double)destH / f));
    }

    /// <summary>The part of the full-resolution image that is on screen, in image pixels.</summary>
    private Rect VisibleImageRect(CanvasDrawingSession session, CanvasViewState viewState)
    {
        // CanvasAnimatedControl.Size is safe to read from the game-loop thread; read it once.
        var size = _canvas.Size;
        var scale = session.Dpi / 96f;
        var w = (float)size.Width * scale;
        var h = (float)size.Height * scale;

        // MatInv maps screen → image, rotation included; take the bounding box of the four corners.
        var p0 = Vector2.Transform(new Vector2(0, 0), viewState.MatInv);
        var p1 = Vector2.Transform(new Vector2(w, 0), viewState.MatInv);
        var p2 = Vector2.Transform(new Vector2(0, h), viewState.MatInv);
        var p3 = Vector2.Transform(new Vector2(w, h), viewState.MatInv);
        var min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
        var max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));

        var rect = new Rect(new Point(min.X, min.Y), new Point(max.X, max.Y));
        rect.Intersect(viewState.ImageRect);
        return rect;
    }

    /// <summary>Disposes least-recently-drawn tiles until the cache is back to <see cref="MaxCachedTiles"/>.</summary>
    private void EvictOldTiles()
    {
        // Linear scan for the oldest tile; fine at ~100 entries. Switch to a linked-list LRU
        // (O(1) touch/evict) if MaxCachedTiles grows by an order of magnitude.
        while (_gpuTiles.Count > MaxCachedTiles)
        {
            var oldest = default((int, int, int));
            var oldestFrame = long.MaxValue;
            foreach (var (key, entry) in _gpuTiles)
                if (entry.LastUsed < oldestFrame)
                {
                    oldest = key;
                    oldestFrame = entry.LastUsed;
                }
            if (oldestFrame == _frame) return; // everything is on screen right now; don't thrash
            _gpuTiles[oldest].Bitmap.Dispose();
            _gpuTiles.Remove(oldest);
        }
    }

    /// <summary>Redraws with the new interpolation; the pyramid doesn't depend on it, so nothing is rebuilt.</summary>
    public void HandleScalingMethodChange() => _invalidate();

    /// <summary>Disposes the uploaded GPU tiles. The pyramid and overview belong to the display item.</summary>
    public void Dispose()
    {
        foreach (var entry in _gpuTiles.Values) entry.Bitmap.Dispose();
        _gpuTiles.Clear();
    }
}
