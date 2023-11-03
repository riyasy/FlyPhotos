using FlyPhotos.Data;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyPhotos.Controllers;

internal class WpfImageController
{
    private readonly Image _imgDsp;
    private Photo _currentPhoto = Photo.Empty();

    public WpfImageController(Image imgDsp)
    {
        _imgDsp = imgDsp;
    }

    public Photo Source
    {
        get => _currentPhoto;
        set
        {
            _currentPhoto = value;
            _imgDsp.Source = _currentPhoto.Bitmap;
        }
    }

    public void RotateCurrentPhotoBy90()
    {
        _currentPhoto.Bitmap = new TransformedBitmap(_currentPhoto.Bitmap, new RotateTransform(90));
        _imgDsp.Source = _currentPhoto.Bitmap;
    }
}