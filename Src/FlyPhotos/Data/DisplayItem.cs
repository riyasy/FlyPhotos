using Microsoft.Graphics.Canvas;
using System;

namespace FlyPhotos.Data;

internal record ImageMetadata(double FullWidth, double FullHeight);

internal abstract partial class DisplayItem(CanvasBitmap bitmap, Origin origin, int rotation) : IDisposable
{
    public CanvasBitmap Bitmap { get; } = bitmap;
    public Origin Origin { get; } = origin;
    public int Rotation { get; } = rotation;

    public void Dispose()
    {
        // We don't dispose bitmaps coming from the error screen as they are reused.
        if (Origin != Origin.ErrorScreen)
            Bitmap?.Dispose();
    }
}

internal sealed partial class PreviewDisplayItem(CanvasBitmap bitmap, Origin origin, ImageMetadata metadata = null) : DisplayItem(bitmap, origin, 0)
{    
    public ImageMetadata Metadata { get; } = metadata;

    public static PreviewDisplayItem Empty()
    {
        return new PreviewDisplayItem(null, Origin.Undefined);
    }
}

internal abstract partial class HqDisplayItem(CanvasBitmap bitmap, Origin origin, int rotation) : DisplayItem(bitmap, origin, rotation)
{
    public static HqDisplayItem Empty()
    {
        return new StaticHqDisplayItem(null, Origin.Undefined);
    }
}

internal sealed partial class StaticHqDisplayItem(CanvasBitmap bitmap, Origin origin, int rotation = 0) : HqDisplayItem(bitmap, origin, rotation);

internal sealed partial class AnimatedHqDisplayItem(CanvasBitmap firstFrame, Origin origin, byte[] fileAsByteArray) : HqDisplayItem(firstFrame, origin, 0)
{
    public byte[] FileAsByteArray { get; } = fileAsByteArray;
}