using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FlyPhotos.Utils;


namespace FlyPhotos.AppSettings;

public class SettingsWrapper
{
    [System.Text.Json.Serialization.JsonPropertyName("Settings")]
    public AppSettings Settings { get; init; }
}


public static class AppConfig
{
    private static string _userSettingsPath;
    public static AppSettings Settings { get; private set; }

    // This method reads the config files when the app starts.
    public static void Initialize()
    {
        _userSettingsPath = Path.Combine(PathResolver.GetUserSettingsFolder(), "usersettings.json");
        var defaultSettingsPath = Path.Combine(PathResolver.GetDefaultSettingsFolder(), "appsettings.json");

        // AOT-Safe Loading Logic:
        // 1. Try to load user settings first.
        // 2. If they don't exist or fail to load, fall back to default settings.

        // Try loading the user's settings
        if (File.Exists(_userSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(_userSettingsPath);
                var settingsWrapper = JsonSerializer.Deserialize(
                    json,
                    JsonSourceGenerationContext.Default.SettingsWrapper
                );
                Settings = settingsWrapper?.Settings;
            }
            catch (Exception ex)
            {
                // Log error if user settings are corrupt
                System.Diagnostics.Debug.WriteLine($"Error loading user settings, falling back to default: {ex.Message}");
                Settings = null; // Ensure we fall back
            }
        }

        // If user settings were not loaded, load the defaults from appsettings.json
        if (Settings == null && File.Exists(defaultSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(defaultSettingsPath);
                var settingsWrapper = JsonSerializer.Deserialize(
                    json,
                    JsonSourceGenerationContext.Default.SettingsWrapper
                );
                Settings = settingsWrapper?.Settings;
            }
            catch (Exception ex)
            {
                // Log error if default settings are corrupt
                System.Diagnostics.Debug.WriteLine($"FATAL: Could not load default settings: {ex.Message}");
            }
        }

        // If all loading fails, create a new default instance to prevent null references
        Settings ??= new AppSettings();
    }

    // This method saves the user's changes to their private settings file.
    public static async Task SaveAsync()
    {
        try
        {
            var settingsWrapper = new SettingsWrapper { Settings = Settings };
            var json = JsonSerializer.Serialize(
                settingsWrapper,
                JsonSourceGenerationContext.Default.SettingsWrapper // Get the pre-generated info for our type
            );
            await File.WriteAllTextAsync(_userSettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
        }
    }
}