using System;
using FlyPhotos.Display.ImageReading;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace FlyPhotos.Core.Model;

internal record ImageMetadata(double FullWidth, double FullHeight);

internal abstract partial class DisplayItem(CanvasBitmap bitmap, Origin origin, int rotation) : IDisposable
{
    public CanvasBitmap Bitmap { get; } = bitmap;
    public Origin Origin { get; } = origin;
    public int Rotation { get; } = rotation;

    public bool IsErrorOrUndefined() => 
        Origin == Origin.ErrorScreen || Origin == Origin.Undefined;

    public virtual void Dispose()
    {
        // We don't dispose bitmaps coming from the error screen as they are reused.
        if (!IsErrorOrUndefined()) 
            Bitmap?.Dispose();
    }
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

/// <summary>
/// One entry of an SVG's display list: a recorded top-level draw (a fill, a stroke, or a whole layer) and the
/// bounds of what it paints, in SVG user units. Culling tests <see cref="Bounds"/> against the visible area.
/// <see cref="Shape"/> is the raw data of a single top-level fill or stroke (null for a layer), which lets the
/// renderer draw a huge one from a pre-chopped path instead of <see cref="Commands"/>.
/// </summary>
internal readonly record struct SvgDisplayItem(CanvasCommandList Commands, Rect Bounds, SvgShape Shape = null);

/// <summary>
/// An SVG kept as a retained vector display list so it stays sharp at any zoom. <see cref="DisplayItem.Bitmap"/> is
/// the rasterised fallback, used when zoomed out or while the SVG is animating too slowly as vectors. The items
/// are in paint order, in SVG user units (<see cref="SvgWidth"/> x <see cref="SvgHeight"/>), and are drawn by
/// SvgVectorRenderer, which skips the ones outside the view.
/// </summary>
internal sealed partial class SvgVectorHqDisplayItem(CanvasBitmap bitmap, Origin origin, SvgDisplayItem[] items,
    float svgWidth, float svgHeight, bool isHeavy) : HqDisplayItem(bitmap, origin, 0)
{
    /// <summary>Paint order. A flat array so a spatial index could later replace the renderer's linear scan.</summary>
    public SvgDisplayItem[] Items { get; } = items;
    public float SvgWidth { get; } = svgWidth;
    public float SvgHeight { get; } = svgHeight;

    /// <summary>Many points or ops: replaying the vectors every frame would be too slow while panning/zooming.</summary>
    public bool IsHeavy { get; } = isHeavy;

    public override void Dispose()
    {
        base.Dispose();
        foreach (var item in Items)
            item.Commands?.Dispose();
    }
}

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