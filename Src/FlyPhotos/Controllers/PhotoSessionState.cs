#nullable enable
using FlyPhotos.Data;

namespace FlyPhotos.Controllers;

internal class PhotoSessionState
{
    public int CurrentDisplayKey { get; set; }
    public DisplayLevel CurrentDisplayLevel { get; set; }
    public int PhotosCount { get; set; }
    public string FirstPhotoPath { get; init; } = string.Empty;
}