using System;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Core.Model;

internal record ImageMetadata(double FullWidth, double FullHeight);

/// <summary>
/// A decoded image of a photo. Its lifetime is counted in <b>leases</b>: the creator holds the first one (handed
/// on to the photo's slot when stored), and anyone else that uses the bitmap outside the slot — the canvas while
/// it is on screen, a clipboard copy, a disk-cache write — takes its own with <see cref="TryAcquire"/>. The last
/// <see cref="Release"/> frees it, so it is never disposed while something still draws or reads it.
/// Error and placeholder items share bitmaps across photos; they are never freed and their leases are free.
/// </summary>
internal abstract partial class DisplayItem(CanvasBitmap bitmap, Origin origin, int rotation)
{
    public CanvasBitmap Bitmap { get; } = bitmap;
    public Origin Origin { get; } = origin;
    public int Rotation { get; } = rotation;

    private LeaseCount _leases = new();

    /// <summary>
    /// Where an item goes when its last lease ends. The canvas routes it to its render thread, so bitmaps are
    /// freed there as they are today; unset (no canvas, or it is gone), the item is freed at once.
    /// </summary>
    public static Action<DisplayItem> FreeSink { get; set; }

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

internal sealed partial class PreviewDisplayItem(CanvasBitmap bitmap, Origin origin, ImageMetadata metadata = null, int rotation = 0) : DisplayItem(bitmap, origin, rotation)
{    
    public ImageMetadata Metadata { get; } = metadata;
    private static readonly PreviewDisplayItem _empty = new(null, Origin.Undefined);
    public static PreviewDisplayItem Empty() => _empty;
}

internal abstract partial class HqDisplayItem(CanvasBitmap bitmap, Origin origin, int rotation) : DisplayItem(bitmap, origin, rotation)
{
    private static readonly HqDisplayItem _empty = new StaticHqDisplayItem(null, Origin.Undefined);
    public static HqDisplayItem Empty() => _empty;
}

internal sealed partial class StaticHqDisplayItem(CanvasBitmap bitmap, Origin origin, int rotation = 0) : HqDisplayItem(bitmap, origin, rotation);

internal sealed partial class AnimatedHqDisplayItem(CanvasBitmap firstFrame, Origin origin, byte[] fileAsByteArray,
    ImageFormat format) : HqDisplayItem(firstFrame, origin, 0)
{
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
    public TilePyramid Pyramid { get; } = pyramid;
}

/// <summary>
/// An SVG drawn as vectors on the GPU at any zoom by SvgVectorRenderer. Has no <see cref="DisplayItem.Bitmap"/>:
/// drawing one up front would cost a full extra draw of the scene, so <see cref="Rasterize"/> makes one only
/// when pixels are needed (copy to clipboard).
/// </summary>
/// <param name="lease">The SVG reader's lease on the GPU scene; this item owns it from now on.</param>
/// <param name="width">The size the photo reports (<see cref="PhotoImages.GetActualSize"/>): the scene fitted to
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

internal sealed partial class MultiPageHqDisplayItem(CanvasBitmap firstFrame, Origin origin, byte[] fileAsByteArray,
    int[] pageOrder) : HqDisplayItem(firstFrame, origin, 0)
{
    public byte[] FileAsByteArray { get; } = fileAsByteArray;

    /// <summary>
    /// Display page -> decoder frame index. TIFF passes identity, ICO passes largest-frame-first. It is
    /// also the page count, so a count and a map can never disagree.
    /// </summary>
    public int[] PageOrder { get; } = pageOrder;

    public int PageCount => PageOrder.Length;
}

/// <summary>
/// Device-independent thumbnail pixels. The GPU copy is owned by ThumbNailController on its W2D
/// thread, so dropping a Thumbnail from any thread never races a bitmap that is being drawn.
/// </summary>
internal sealed partial class Thumbnail(byte[] pixels)
{
    public byte[] Pixels { get; } = pixels;
}