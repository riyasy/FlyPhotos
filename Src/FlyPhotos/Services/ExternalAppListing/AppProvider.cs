#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;

namespace FlyPhotos.Services.ExternalAppListing;

/// <summary>
/// Defines a contract for retrieving installed applications.
/// </summary>
public abstract class AppProvider
{
    /// <summary>
    /// Asynchronously retrieves a collection of installed applications.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The value of the TResult parameter contains the list of installed apps.</returns>
    public abstract Task<IEnumerable<InstalledApp>> GetAppsAsync();

    /// <summary>
    /// Retrieves information about an installed application based on the specified shortcut string.
    /// </summary>
    /// <remarks>The shortcut string determines which provider is used to locate the application. If the
    /// format is invalid or the application cannot be found, the method returns null.</remarks>
    /// <param name="appShortCut">A shortcut string identifying the application.
    /// The format must be either "Win32|publisher|appId" or
    /// "Store|publisher|appId|storeId". The string cannot be null or empty.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains an InstalledApp object if the
    /// application is found; otherwise, null.</returns>
    public static async Task<InstalledApp?> GetAppAsync(string appShortCut)
    {
        var parts = appShortCut.Split('|');
        if (parts.Length < 3) return null;

        InstalledApp? app = null;
        switch (parts[0])
        {
            case "Win32" when parts.Length >= 3:
                app = await Win32AppProvider.GetAppAsync(parts[1], parts[2]);
                break;
            case "Store" when parts.Length >= 4:
                app = await StoreAppProvider.GetAppAsync(parts[1], parts[2], parts[3]);
                break;
        }
        return app;
    }

}


