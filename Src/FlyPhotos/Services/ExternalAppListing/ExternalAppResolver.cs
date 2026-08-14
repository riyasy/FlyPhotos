#nullable enable
using System.Threading.Tasks;
using FlyPhotos.Infra.Configuration;

namespace FlyPhotos.Services.ExternalAppListing;

internal static class ExternalAppResolver
{
    // ponytail: unlocked static cache — all callers are UI-thread async; last-writer-wins on a
    // concurrent miss just re-resolves identical data. Add a lock only if a non-UI caller appears.
    private static (string, string, string, string) _cachedKey;
    private static InstalledApp?[]? _cached;

    /// <summary>
    /// Resolves the 4 configured external-app shortcut slots (Settings &gt; External apps) in
    /// parallel, caching by slot identity (an empty/unresolved slot is null at that index).
    /// Position is stable so callers like Ctrl+1..4 map to fixed slots. Resolving involves
    /// off-UI-thread work (icon extraction for Win32 apps, native lookups for Store apps).
    /// </summary>
    public static async Task<InstalledApp?[]> GetConfiguredAsync()
    {
        var slots = new[]
        {
            AppConfig.Settings.ExternalApp1, AppConfig.Settings.ExternalApp2,
            AppConfig.Settings.ExternalApp3, AppConfig.Settings.ExternalApp4
        };
        var key = (slots[0], slots[1], slots[2], slots[3]);
        if (_cached != null && _cachedKey.Equals(key)) return _cached;

        var tasks = new Task<InstalledApp?>[slots.Length];
        for (var i = 0; i < slots.Length; i++)
            tasks[i] = string.IsNullOrEmpty(slots[i])
                ? Task.FromResult<InstalledApp?>(null)
                : ShellAppProvider.GetAppAsync(slots[i]);

        var apps = await Task.WhenAll(tasks);
        _cachedKey = key;
        _cached = apps;
        return apps;
    }
}
