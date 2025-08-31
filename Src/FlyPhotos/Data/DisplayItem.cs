using Microsoft.Graphics.Canvas;
using System;

namespace FlyPhotos.Data;

internal record ImageMetadata(double FullWidth, double FullHeight);

internal abstract class DisplayItem(CanvasBitmap bitmap, int rotation = 0) : IDisposable
{
    public CanvasBitmap Bitmap { get; } = bitmap;
    public int Rotation { get; } = rotation;

    public void Dispose()
    {
        //Bitmap?.Dispose();
    }
}

internal sealed class PreviewDisplayItem(CanvasBitmap bitmap, PreviewSource previewFrom, ImageMetadata metadata = null) : DisplayItem(bitmap)
{
    public PreviewSource PreviewFrom { get; } = previewFrom;
    public ImageMetadata Metadata { get; } = metadata;

    public static PreviewDisplayItem Empty()
    {
        return new PreviewDisplayItem(null, PreviewSource.Undefined, null);
    }
}

internal abstract class HqDisplayItem(CanvasBitmap bitmap, int rotation = 0) : DisplayItem(bitmap, rotation)
{
    public static HqDisplayItem Empty()
    {
        return new StaticHqDisplayItem(null);
    }
}

internal sealed class StaticHqDisplayItem(CanvasBitmap bitmap, int rotation = 0) : HqDisplayItem(bitmap, rotation);

internal sealed class AnimatedHqDisplayItem(byte[] fileAsByteArray) : HqDisplayItem(null)
{
    public byte[] FileAsByteArray { get; } = fileAsByteArray;
}