using System;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Core.Model;

/// <summary>The full image's pixel size, known to a preview that is smaller than the photo (0 when unknown).</summary>
internal record ImageMetadata(double FullWidth, double FullHeight);

/// <summary>
/// A decoded image of a photo. Its lifetime is counted in <b>leases</b>: the creator holds the first one (handed
/// on to the photo's slot when stored), and anyone else that uses the bitmap outside the slot — the canvas while
/// it is on screen, a clipboard copy, a disk-cache write — takes its own with <see cref="TryAcquire"/>. The last
/// <see cref="Release"/> frees it, so it is never disposed while something still draws or reads it.
/// Error and placeholder items share bitmaps across photos; they are never freed and their leases are free.
/// <para>
/// The usual lifetime of a prefetched image: the decode creates it (lease 1) → the prefetch cache stores it in the
/// photo's slot, which takes over that lease → the UI thread leases it for the canvas (lease 2) → the cache unloads
/// the photo and releases the slot's lease (1 left, the canvas still draws it) → the canvas swaps to another image
/// and releases its lease (0) → the item is freed on the canvas's render thread (<see cref="FreeSink"/>).
/// </para>
/// <para>Thread-safe: leases may be taken and ended on any thread.</para>
/// </summary>
internal abstract partial class DisplayItem(CanvasBitmap bitmap, Origin origin, int rotation)
{
    /// <summary>The decoded pixels on the canvas device. Null for an SVG (drawn as vectors) and the empty items.</summary>
    public CanvasBitmap Bitmap { get; } = bitmap;
    /// <summary>Where the image came from (file, disk cache, …); <c>ErrorScreen</c>/<c>Undefined</c> mark shared items.</summary>
    public Origin Origin { get; } = origin;
    /// <summary>Degrees to rotate when drawing (EXIF orientation).</summary>
    public int Rotation { get; } = rotation;

    // The creator's lease, then one per holder; the last Release frees the item. A mutable struct: called in place,
    // never copied (see LeaseCount). Unused for the shared error and placeholder items.
    private LeaseCount _leases = new();

    /// <summary>
    /// Where an item goes when its last lease ends. The canvas routes it to its render thread, so bitmaps are
    /// freed there, never while a draw is using the device; unset (no canvas, or it is gone), the item is freed at
    /// once on the thread that ended the last lease. Set by the canvas on the UI thread; read on any thread.
    /// </summary>
    public static Action<DisplayItem> FreeSink { get; set; }

    /// <summary>A shared error or placeholder item: never freed, and its leases cost nothing.</summary>
    public bool IsErrorOrUndefined() =>
        Origin == Origin.ErrorScreen || Origin == Origin.Undefined;

    /// <summary>Takes a lease. False once the item has been freed: the caller must not touch it.</summary>
    public bool TryAcquire() => IsErrorOrUndefined() || _leases.TryAcquire();

    /// <summary>Whether the last lease has ended. Never true for the shared error and placeholder items.</summary>
    public bool IsFreed => !IsErrorOrUndefined() && _leases.IsReleased;

    /// <summary>Ends a lease; the last one frees the item.</summary>
    public void Release()
    {
        if (IsErrorOrUndefined() || !_leases.Release()) return;
        if (FreeSink is { } sink) sink(this);
        else Free();
    }

    /// <summary>
    /// Frees the bitmap. Called once, after the last lease ends: by <see cref="Release"/>, or by whoever
    /// <see cref="FreeSink"/> handed the item to (the canvas, on its render thread). Never call it on an item that
    /// still has leases. Virtual so items that own more, such as <see cref="SvgHqDisplayItem"/>'s scene, can release
    /// it too.
    /// </summary>
    public virtual void Free() => Bitmap?.Dispose();
}

/// <summary>A preview: a fast, usually downsized image (disk cache, embedded thumbnail, or a reduced decode).</summary>
internal sealed partial class PreviewDisplayItem(CanvasBitmap bitmap, Origin origin, ImageMetadata metadata = null, int rotation = 0) : DisplayItem(bitmap, origin, rotation)
{
    /// <summary>The full image's size, so the photo is laid out at its real size before the HQ arrives.</summary>
    public ImageMetadata Metadata { get; } = metadata;
    // The one shared empty preview (Origin.Undefined, so never freed).
    private static readonly PreviewDisplayItem _empty = new(null, Origin.Undefined);
    /// <summary>A shared item with no bitmap (never freed).</summary>
    public static PreviewDisplayItem Empty() => _empty;
}

/// <summary>A full-quality image. The subclass says how the canvas draws it.</summary>
internal abstract partial class HqDisplayItem(CanvasBitmap bitmap, Origin origin, int rotation) : DisplayItem(bitmap, origin, rotation)
{
    // The one shared empty HQ (Origin.Undefined, so never freed).
    private static readonly HqDisplayItem _empty = new StaticHqDisplayItem(null, Origin.Undefined);
    /// <summary>A shared item with no bitmap (never freed).</summary>
    public static HqDisplayItem Empty() => _empty;
}

/// <summary>A single-frame image drawn from its <see cref="DisplayItem.Bitmap"/>.</summary>
internal sealed partial class StaticHqDisplayItem(CanvasBitmap bitmap, Origin origin, int rotation = 0) : HqDisplayItem(bitmap, origin, rotation);

/// <summary>
/// An animation (GIF, WebP, APNG, AVIF). <see cref="DisplayItem.Bitmap"/> is its first frame, shown at once; the
/// animator then decodes the frames from the file bytes itself.
/// </summary>
internal sealed partial class AnimatedHqDisplayItem(CanvasBitmap firstFrame, Origin origin, byte[] fileAsByteArray,
    ImageFormat format) : HqDisplayItem(firstFrame, origin, 0)
{
    /// <summary>The whole file, for the animator.</summary>
    public byte[] FileAsByteArray { get; } = fileAsByteArray;

    /// <summary>The format the reader decoded, which picks the animator; the file's extension may be wrong.</summary>
    public ImageFormat Format { get; } = format;
}

/// <summary>
/// An image larger than the GPU's maximum bitmap size. <see cref="DisplayItem.Bitmap"/> is a small overview
/// (the pyramid's top level); the full-resolution pixels live in CPU-side tiles drawn by TiledImageRenderer.
/// </summary>
internal sealed partial class TiledHqDisplayItem(CanvasBitmap overview, Origin origin, TilePyramid pyramid)
    : HqDisplayItem(overview, origin, 0)
{
    /// <summary>The CPU-side tiles at every level, and the image's full size. Can be gigabytes: only the photo on
    /// screen keeps one (see PrefetchPlanner).</summary>
    public TilePyramid Pyramid { get; } = pyramid;
}

/// <summary>
/// An SVG drawn as vectors on the GPU at any zoom by SvgVectorRenderer. Has no <see cref="DisplayItem.Bitmap"/>:
/// drawing one up front would cost a full extra draw of the scene, so <see cref="Rasterize"/> makes one only
/// when pixels are needed (copy to clipboard).
/// </summary>
/// <param name="lease">The SVG reader's lease on the GPU scene; this item owns it from now on.</param>
/// <param name="width">The size the photo reports (<see cref="DisplaySlots.GetActualSize"/>): the scene fitted to
/// 2000 px on the longer side, as the resvg raster was.</param>
internal sealed partial class SvgHqDisplayItem(Origin origin, SvgScene.Lease lease, int width, int height)
    : HqDisplayItem(null, origin, 0)
{
    /// <summary>The GPU scene the renderer draws.</summary>
    public SvgScene Scene => lease.Scene;
    /// <summary>The reported width in pixels.</summary>
    public int Width { get; } = width;
    /// <summary>The reported height in pixels.</summary>
    public int Height { get; } = height;

    /// <summary>
    /// A new bitmap of the scene at <see cref="Width"/>×<see cref="Height"/>, owned by the caller; null once the
    /// scene has been released (this item disposed and no renderer still drawing it). Throws if drawing fails.
    /// </summary>
    public CanvasBitmap Rasterize()
    {
        using var hold = Scene.TryAcquire();
        return hold == null ? null
            : Scene.Rasterize(Math.Max(Width, Height), AppConfig.Settings.ImageScalingQuality.ToCanvasInterpolation(false));
    }

    /// <summary>Drops this item's ownership of the scene; a renderer still drawing it keeps it alive.</summary>
    public override void Free() => lease.Dispose();
}

/// <summary>
/// A multi-page image (multi-page TIFF, multi-frame ICO). <see cref="DisplayItem.Bitmap"/> is the first page; the
/// renderer decodes other pages from the file bytes as the user pages through.
/// </summary>
internal sealed partial class MultiPageHqDisplayItem(CanvasBitmap firstFrame, Origin origin, byte[] fileAsByteArray,
    int[] pageOrder) : HqDisplayItem(firstFrame, origin, 0)
{
    /// <summary>The whole file, for decoding the other pages.</summary>
    public byte[] FileAsByteArray { get; } = fileAsByteArray;

    /// <summary>
    /// Display page -> decoder frame index. TIFF passes identity, ICO passes largest-frame-first. It is
    /// also the page count, so a count and a map can never disagree.
    /// </summary>
    public int[] PageOrder { get; } = pageOrder;

    /// <summary>How many pages the image has.</summary>
    public int PageCount => PageOrder.Length;
}

/// <summary>
/// Device-independent thumbnail pixels. The GPU copy is owned by ThumbNailController on its W2D
/// thread, so dropping a Thumbnail from any thread never races a bitmap that is being drawn.
/// </summary>
internal sealed partial class Thumbnail(byte[] pixels)
{
    /// <summary>BGRA pixels of a square Constants.ThumbnailPixelBufferSize image.</summary>
    public byte[] Pixels { get; } = pixels;
}