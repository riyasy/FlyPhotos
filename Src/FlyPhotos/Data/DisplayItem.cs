using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Data;

internal class DisplayItem
{
    public enum PreviewSource
    {
        FromDiskCache,
        FromDisk,
        ErrorScreen, 
        Undefined
    }

    public CanvasBitmap Bitmap { get; set; }
    public SoftwareBitmap SoftwareBitmap { get; set; }
    public int Rotation { get; set; }

    public PreviewSource PreviewFrom { get; set; }

    public DisplayItem(CanvasBitmap bitmap, PreviewSource previewFrom, int rotation = 0)
    {
        Bitmap = bitmap;
        PreviewFrom = previewFrom;
        Rotation = rotation;
    }

    public DisplayItem(SoftwareBitmap softwareBitmap, PreviewSource previewFrom, int rotation = 0)
    {
        SoftwareBitmap = softwareBitmap;
        PreviewFrom = previewFrom;
        Rotation = rotation;
    }

    //public Photo(CanvasBitmap bitmap, int rotation)
    //{
    //    Bitmap = bitmap;
    //    Rotation = rotation;
    //}

    public static DisplayItem Empty()
    {
        CanvasBitmap bitmap = null;
        return new DisplayItem(bitmap, PreviewSource.Undefined, 0);
    }
}