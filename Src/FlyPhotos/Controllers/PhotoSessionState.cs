#nullable enable
using FlyPhotos.Data;

namespace FlyPhotos.Controllers;

internal class PhotoSessionState
{
    public int CurrentPhotoKey { get; private set; }
    public int CurrentPhotoListPosition { get; private set; }
    public DisplayLevel CurrentDisplayLevel { get; set; }
    public int PhotosCount { get; set; }
    public string FirstPhotoPath { get; init; } = string.Empty;
    public bool FlyLaunchedExternally { get; set; }

    public void SetCurrentPhotoKeyAndListPosition(int newKey, int newPosition)
    {
        CurrentPhotoKey = newKey;
        CurrentPhotoListPosition = newPosition;
    }
}