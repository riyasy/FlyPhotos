using System.Windows.Media.Imaging;

namespace FlyPhotosV1.Data;

internal class Photo
{
    public BitmapSource Bitmap { get; set; }
    public int Rotation { get; set; }

    public Photo(BitmapSource bitmap)
    {
        Bitmap = bitmap;
    }

    public Photo(BitmapSource bitmap, int rotation)
    {
        Bitmap = bitmap;
        Rotation = rotation;
    }

    public static Photo Empty()
    {
        return new Photo(null);
    }
}