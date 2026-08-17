namespace FlyPhotos.Infra.Configuration;

/// <summary>
/// Holds volatile (non-persisted) runtime state for the current process.
/// </summary>
public class AppVolatileState
{
    /// <summary>
    /// True when this process is a secondary FlyPhotos instance launched while
    /// AllowMultiInstance is enabled. Secondary instances display only the single
    /// selected image — no folder scan, no Settings button, no Delete, no cache status.
    /// </summary>
    public bool IsSecondaryInstance { get; set; } = false;

    /// <summary>
    /// True when the EXIF panel was last left in "show all" mode. Remembered for the
    /// lifetime of the process so reopening the panel keeps the chosen view.
    /// </summary>
    public bool ExifShowAll { get; set; } = false;
}
