namespace FlyPhotos.Infra.Configuration;

/// <summary>
/// Holds volatile (non-persisted) runtime state for the current process.
/// Populated once at startup and read-only thereafter.
/// </summary>
public class AppVolatileState
{
    /// <summary>
    /// True when this process is a secondary FlyPhotos instance launched while
    /// AllowMultiInstance is enabled. Secondary instances display only the single
    /// selected image — no folder scan, no Settings button, no Delete, no cache status.
    /// </summary>
    public bool IsSecondaryInstance { get; set; } = false;
}
