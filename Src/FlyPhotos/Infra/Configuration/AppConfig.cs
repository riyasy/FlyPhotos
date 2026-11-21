using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Services;
using Microsoft.UI.Dispatching;
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
    private static DispatcherQueue _uiQueue;
    public static AppSettings Settings { get; internal set; } // tests set their own
    public static AppVolatileState Volatile { get; } = new();

    /// <summary>
    /// Raised on the UI thread right after <see cref="Settings"/> was replaced by settings another
    /// instance saved, for code that holds on to parts of the old settings object.
    /// </summary>
    public static event Action SettingsReloaded;

    // Loads user settings on startup. Falls back to AppSettings defaults if the file is
    // absent or corrupt — no separate appsettings.json needed since all defaults live in the class.
    // Also watches the file so changes saved by other running instances are picked up.
    // Call on the UI thread: reloads are applied on the thread that called this.
    public static void Initialize()
    {
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        var path = Path.Combine(PathResolver.GetUserSettingsFolder(), "usersettings.json");
        _store = new SettingsStore(path, "FlyPhotosSettings");
        Settings = _store.Load();
        _store.Reloaded += OnStoreReloaded;
        _store.StartWatching();
    }

    // The store raises Reloaded on a worker thread; swap on the UI thread so UI code never sees
    // Settings change under it mid-handler.
    private static void OnStoreReloaded(AppSettings settings)
    {
        if (_uiQueue == null)
        {
            ApplyReload(settings);
            return;
        }
        if (!_uiQueue.TryEnqueue(() => ApplyReload(settings)))
            Logger.Warn("Could not queue a settings reload onto the UI thread");
    }

    private static void ApplyReload(AppSettings settings)
    {
        Settings = settings;
        SettingsReloaded?.Invoke();
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
