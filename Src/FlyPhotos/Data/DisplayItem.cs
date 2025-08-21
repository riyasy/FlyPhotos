using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Data;

internal class DisplayItem
{
    public byte[] FileAsByteArray { get; }
    public CanvasBitmap Bitmap { get; set; }
    public int Rotation { get; }
    public PreviewSource PreviewFrom { get; }

    public DisplayItem(CanvasBitmap bitmap, PreviewSource previewFrom, int rotation = 0)
    {
        Bitmap = bitmap;
        PreviewFrom = previewFrom;
        Rotation = rotation;
    }

    public DisplayItem(byte[] fileFileBytes, PreviewSource previewFrom)
    {
        FileAsByteArray = fileFileBytes;
        PreviewFrom = previewFrom;
    }

    public static DisplayItem Empty()
    {
        CanvasBitmap bitmap = null;
        return new DisplayItem(bitmap, PreviewSource.Undefined, 0);
    }

    public bool IsGifOrAnimatedPng()
    {
        return FileAsByteArray != null;
    }
}