#nullable enable
using FlyPhotos;
using FlyPhotos.Data;

namespace FlyPhotos.Controllers;

internal class PhotoSessionState
{
    public int CurrentDisplayIndex { get; set; }
    public DisplayLevel CurrentDisplayLevel { get; set; }
    public int PhotosCount { get; set; }
    public string FirstPhotoPath { get; set; }
}