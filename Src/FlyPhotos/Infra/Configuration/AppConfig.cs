using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Services;
using NLog;

namespace FlyPhotos.Infra.Configuration;

public class SettingsWrapper
{
    [System.Text.Json.Serialization.JsonPropertyName("Settings")]
    public AppSettings Settings { get; init; }
}


public static class AppConfig
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static SettingsStore _store;
    public static AppSettings Settings { get; internal set; } // tests set their own
    public static AppVolatileState Volatile { get; } = new();

    // Loads user settings on startup. Falls back to AppSettings defaults if the file is
    // absent or corrupt — no separate appsettings.json needed since all defaults live in the class.
    // Also watches the file so changes saved by other running instances are picked up.
    public static void Initialize()
    {
        var path = Path.Combine(PathResolver.GetUserSettingsFolder(), "usersettings.json");
        _store = new SettingsStore(path, "FlyPhotosSettings");
        Settings = _store.Load();
        _store.Reloaded += s => Settings = s;
        _store.StartWatching();
    }

    // This method saves the user's changes to their private settings file.
    public static async Task SaveAsync()
    {
        if (_store == null) return;
        try
        {
            await _store.SaveAsync(Settings);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error saving settings");
        }
    }

    public static void Save()
    {
        if (_store == null) return;
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error saving settings");
        }
    }
}
