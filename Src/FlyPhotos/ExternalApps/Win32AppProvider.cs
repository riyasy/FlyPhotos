using FlyPhotos.NativeWrappers;
using FlyPhotos.Utils;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FlyPhotos.ExternalApps;

/// <summary>
/// Provides retrieval of Win32 applications from the Start Menu.
/// </summary>
public class Win32AppProvider : AppProvider
{
    /// <inheritdoc />
    public override Task<IEnumerable<InstalledApp>> GetAppsAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<InstalledApp>();
            var rawApps = NativeWrapper.LoadAllWin32ProgramShortcuts();

            foreach (var dto in rawApps)
            {
                var app = new Win32App
                {
                    DisplayName = dto.Name,
                    ExePath = dto.Path,
                    Type = AppType.Win32,
                    IconData = new Win32IconData(dto.IconPixels, dto.Width, dto.Height)
                };
                results.Add(app);
            }

            return (IEnumerable<InstalledApp>)results;
        });
    }

    /// <summary>
    /// Gets a single Win32 app from its identifying information, used for restoring from a saved state.
    /// </summary>
    /// <param name="name">The display name of the app.</param>
    /// <param name="path">The path to the executable.</param>
    /// <returns>A restored <see cref="Win32App"/> instance with icon loaded if available.</returns>
    public static async Task<Win32App> GetAppAsync(string name, string path)
    {
        var app = new Win32App
        {
            DisplayName = name,
            ExePath = path,
            Type = AppType.Win32
        };

        if (File.Exists(path))
        {
            app.Icon = await Util.ExtractIconFromExe(path);
        }

        return app;
    }
}


