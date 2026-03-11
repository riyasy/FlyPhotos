#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Infra.Utils;
using NLog;

namespace FlyPhotos.Services.ExternalAppListing;

/// <summary>
/// Defines a contract for retrieving installed applications.
/// </summary>
public abstract class AppProvider
{
    /// <summary>
    /// Asynchronously retrieves a collection of installed applications.
    /// </summary>
    /// <returns>A task containing the list of installed apps.</returns>
    public abstract Task<IEnumerable<InstalledApp>> GetAppsAsync();

    /// <summary>
    /// Retrieves a single installed application from its serialized state string,
    /// used for restoring a previously chosen app from settings.
    /// </summary>
    /// <remarks>
    /// The shortcut string format is either "Win32|name|exePath" or
    /// "Store|name|aumid|familyName". Returns null if the format is invalid or
    /// the application cannot be found.
    /// </remarks>
    /// <param name="appShortCut">Serialized state string identifying the application.</param>
    /// <returns>A task whose result is an <see cref="InstalledApp"/> instance, or null.</returns>
    public static async Task<InstalledApp?> GetAppAsync(string appShortCut)
    {
        var parts = appShortCut.Split('|');
        if (parts.Length < 3) return null;

        InstalledApp? app = null;
        switch (parts[0])
        {
            case "Win32" when parts.Length >= 3:
                app = await RestoreWin32AppAsync(parts[1], parts[2]);
                break;
            case "Store" when parts.Length >= 4:
                app = await RestoreStoreAppAsync(parts[1], parts[2], parts[3]);
                break;
        }
        return app;
    }

    // -------------------------------------------------------------------------
    // Win32 restore
    // -------------------------------------------------------------------------

    /// <summary>
    /// Restores a <see cref="Win32App"/> from its name and exe path.
    /// The icon is re-extracted from the executable if it still exists on disk.
    /// </summary>
    private static async Task<Win32App> RestoreWin32AppAsync(string name, string path)
    {
        var app = new Win32App
        {
            DisplayName = name,
            ExePath     = path,
            Type        = AppType.Win32
        };

        if (File.Exists(path))
        {
            app.Icon = await Util.ExtractIconFromExe(path);
        }

        return app;
    }

    // -------------------------------------------------------------------------
    // Store restore
    // -------------------------------------------------------------------------

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Restores a <see cref="StoreApp"/> from its name, AUMID, and package family name.
    /// This is best-effort: if the package is no longer installed the app is returned
    /// without icon data so the caller can still display the name.
    /// </summary>
    private static async Task<StoreApp?> RestoreStoreAppAsync(string name, string aumid, string familyName)
    {
        var app = new StoreApp
        {
            DisplayName       = name,
            AppUserModelId    = aumid,
            PackageFamilyName = familyName,
            Type              = AppType.Store
        };

        // Get the unplated, high-quality icon directly from the Windows Shell by AUMID.
        var rawIcon = await Task.Run(() => Infra.Interop.NativeWrapper.GetUwpAppIcon(aumid));
        if (rawIcon != null)
        {
            app.RawIconData = rawIcon;
            await app.DecodeIconAsync();
        }
        else
        {
            Logger.Warn($"Failed to restore UWP icon for AUMID {aumid}");
        }

        return app;
    }
}
