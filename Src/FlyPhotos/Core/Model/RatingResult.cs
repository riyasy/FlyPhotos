namespace FlyPhotos.Core.Model;

internal class RatingResult(bool success, string failMessage = "", int rating = 0, bool unsupported = false)
{
    public bool Success { get; } = success;
    public string FailMessage { get; } = failMessage;

    /// <summary>The rating on disk after the call (0-5), or the one a write already running will save next.</summary>
    public int Rating { get; } = rating;

    /// <summary>The file type cannot hold a rating Fly writes; nothing was changed.</summary>
    public bool Unsupported { get; } = unsupported;
}
