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

    private volatile MultiPagePosition? _currentPage;

    /// <summary>
    /// The page currently shown by a multi-page renderer (multi-page TIFF, multi-frame ICO), or null
    /// when the current photo is not multi-page. Written by CanvasController on the UI thread, read on
    /// PhotoDisplayController's STA thread to label the filename bar. A volatile reference assignment
    /// publishes the whole value at once, the same guarantee the volatile ints above rely on.
    /// </summary>
    public MultiPagePosition? CurrentPage { get => _currentPage; set => _currentPage = value; }
    public int PhotosCount { get; set; }
    public string FirstPhotoPath { get; init; } = string.Empty;
    public bool FlyLaunchedExternally { get; set; }

    public void SetCurrentPhotoKeyAndListPosition(int newKey, int newPosition)
    {
        _currentPhotoKey = newKey;
        _currentPhotoListPosition = newPosition;
    }
}