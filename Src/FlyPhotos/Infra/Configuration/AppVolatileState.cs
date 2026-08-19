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

    /// <summary>
    /// The foreground window at the moment this process started — normally the Explorer window the
    /// user launched from. Sampled once in the App constructor, before any FlyPhotos window exists.
    /// <para>
    /// File discovery runs on a background thread while the main thread activates our window, so
    /// resolving the foreground window at discovery time races that activation. Activation is much
    /// slower than reaching the lookup, so the race is very rarely lost in practice — capturing the
    /// handle up front simply removes the possibility.
    /// </para>
    /// </summary>
    public nint LaunchForegroundWindow { get; set; } = nint.Zero;
}
