#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using FlyPhotos.Infra.Interop;
using NLog;

namespace FlyPhotos.Services.ExternalAppListing;

/// <summary>
/// Provides retrieval of all installed applications (both Win32 and UWP/Store)
/// by delegating to a single native C++ scan of the FOLDERID_AppsFolder virtual folder.
/// Replaces the separate <see cref="Win32AppProvider"/> and <see cref="StoreAppProvider"/>
/// listing paths.
/// </summary>
public class ShellAppProvider : AppProvider
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public override Task<IEnumerable<InstalledApp>> GetAppsAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<InstalledApp>();

            List<AppShortcutDto> rawApps;
            try
            {
                rawApps = NativeWrapper.LoadAllApps();
            }
            catch (System.Exception ex)
            {
                Logger.Error(ex, "ShellAppProvider - LoadAllApps failed");
                return (IEnumerable<InstalledApp>)results;
            }

            Logger.Info($"ShellAppProvider: loaded {rawApps.Count} apps from native scan.");

            foreach (var dto in rawApps)
            {
                if (dto.IsUwp)
                {
                    // UWP / Store app – AUMID is the primary identifier.
                    // Package family name is the part before '!' in the AUMID
                    // (e.g. "Microsoft.Photos_8wekyb3d8bbwe!App" → "Microsoft.Photos_8wekyb3d8bbwe").
                    // This matches what StoreApp.LaunchAsync already derives at runtime.
                    string familyName = string.Empty;
                    if (!string.IsNullOrEmpty(dto.Aumid))
                    {
                        int bang = dto.Aumid.IndexOf('!');
                        familyName = bang > 0 ? dto.Aumid[..bang] : dto.Aumid;
                    }

                    results.Add(new StoreApp
                    {
                        DisplayName       = dto.Name,
                        AppUserModelId    = dto.Aumid,
                        PackageFamilyName = familyName,
                        RawIconData       = new RawIconData(dto.IconPixels, dto.Width, dto.Height),
                        Type              = AppType.Store
                    });
                }
                else
                {
                    // Win32 executable.
                    results.Add(new Win32App
                    {
                        DisplayName = dto.Name,
                        ExePath     = dto.Path,
                        IconData    = new RawIconData(dto.IconPixels, dto.Width, dto.Height),
                        Type        = AppType.Win32
                    });
                }
            }

            return (IEnumerable<InstalledApp>)results;
        });
    }
}
