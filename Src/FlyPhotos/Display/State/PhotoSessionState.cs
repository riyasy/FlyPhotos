#nullable enable

using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.State;

internal class PhotoSessionState
{
    private volatile int _currentPhotoKey;
    private volatile int _currentPhotoListPosition;

    public int CurrentPhotoKey => _currentPhotoKey;
    public int CurrentPhotoListPosition => _currentPhotoListPosition;

    public DisplayLevel CurrentDisplayLevel { get; set; }
    public int PhotosCount { get; set; }
    public string FirstPhotoPath { get; init; } = string.Empty;
    public bool FlyLaunchedExternally { get; set; }

    public void SetCurrentPhotoKeyAndListPosition(int newKey, int newPosition)
    {
        _currentPhotoKey = newKey;
        _currentPhotoListPosition = newPosition;
    }
}