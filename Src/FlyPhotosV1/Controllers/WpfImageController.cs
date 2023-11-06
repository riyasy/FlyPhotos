using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyPhotosV1.Data;

namespace FlyPhotosV1.Controllers;

internal class WpfImageController(Image imgDsp)
{
    private Photo _currentPhoto = Photo.Empty();

    public Photo Source
    {
        get => _currentPhoto;
        set
        {
            _currentPhoto = value;
            imgDsp.Source = _currentPhoto.Bitmap;
        }
    }

    public void RotateCurrentPhotoBy90()
    {
        _currentPhoto.Bitmap = new TransformedBitmap(_currentPhoto.Bitmap, new RotateTransform(90));
        imgDsp.Source = _currentPhoto.Bitmap;
    }
}