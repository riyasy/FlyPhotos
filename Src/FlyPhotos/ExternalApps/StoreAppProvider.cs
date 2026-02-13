#nullable enable

using FlyPhotos.Utils;
using NLog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace FlyPhotos.ExternalApps;

/// <summary>
/// Provides retrieval of Microsoft Store applications.
/// </summary>
public class StoreAppProvider : AppProvider
{
    /// <summary>
    /// Logger instance for logging errors.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public override async Task<IEnumerable<InstalledApp>> GetAppsAsync()
    {
        var results = new List<InstalledApp>();
        var pm = new PackageManager();

        IEnumerable<Windows.ApplicationModel.Package> packages;
        try
        {
            packages = pm.FindPackagesForUser(string.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "StoreAppProvider GetAppsAsync Error");
            return results; 
        }

        foreach (var package in packages)
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsStub)
                    continue;

                var entries = await package.GetAppListEntriesAsync();

                foreach (var entry in entries)
                {
                    byte[] iconBytes = await Util.ExtractIconFromAppListEntryAsync(entry);

                    var app = new StoreApp
                    {
                        DisplayName = entry.DisplayInfo.DisplayName,
                        Type = AppType.Store,
                        AppUserModelId = entry.AppUserModelId,
                        PackageFamilyName = package.Id.FamilyName,
                        IconData = iconBytes
                    };
                    results.Add(app);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StoreAppProvider Package Processing Error");
            }
        }

        return results;
    }

    /// <summary>
    /// Get a single app from its identifying information, used for restoring from a saved state.
    /// This method is best-effort and may return null if the app cannot be found or restored properly.
    /// </summary>
    /// <param name="name">The display name of the app.</param>
    /// <param name="aumid">The Application User Model ID.</param>
    /// <param name="familyName">The Package Family Name.</param>
    /// <returns>A restored <see cref="StoreApp"/> instance, or null if restoration fails strictly.</returns>
    public static async Task<StoreApp?> GetAppAsync(string name, string aumid, string familyName)
    {
        var app = new StoreApp
        {
            DisplayName = name,
            AppUserModelId = aumid,
            PackageFamilyName = familyName,
            Type = AppType.Store
        };

        var pm = new PackageManager();
        try
        {
            var packages = pm.FindPackagesForUser(string.Empty, familyName);
            foreach (var package in packages)
            {
                var entries = await package.GetAppListEntriesAsync();
                foreach (var entry in entries)
                {
                    if (entry.AppUserModelId != aumid) continue;
                    app.IconData = await Util.ExtractIconFromAppListEntryAsync(entry);
                    await app.DecodeIconAsync();
                    return app;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "StoreApp Restore Error");
        }
        return app; 
    }
}
