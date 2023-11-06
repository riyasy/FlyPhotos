using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Data;

internal class Photo
{
    public CanvasBitmap Bitmap { get; set; }
    public SoftwareBitmap SoftwareBitmap { get; set; }
    public int Rotation { get; set; }

    public Photo(CanvasBitmap bitmap)
    {
        Bitmap = bitmap;
    }

    public Photo(SoftwareBitmap softwareBitmap)
    {
        SoftwareBitmap = softwareBitmap;
    }

    public Photo(CanvasBitmap bitmap, int rotation)
    {
        Bitmap = bitmap;
        Rotation = rotation;
    }

    public static Photo Empty()
    {
        return new Photo(null, 0);
    }
}