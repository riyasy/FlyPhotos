using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FlyPhotos.Services;

namespace FlyPhotos.Infra.Configuration;

public class SettingsWrapper
{
    [System.Text.Json.Serialization.JsonPropertyName("Settings")]
    public AppSettings Settings { get; init; }
}


public static class AppConfig
{
    private static string _userSettingsPath;
    public static AppSettings Settings { get; private set; }
    public static AppVolatileState Volatile { get; } = new();

    // Loads user settings on startup. Falls back to AppSettings defaults if the file is
    // absent or corrupt — no separate appsettings.json needed since all defaults live in the class.
    public static void Initialize()
    {
        _userSettingsPath = Path.Combine(PathResolver.GetUserSettingsFolder(), "usersettings.json");

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
                // Corrupt or unreadable file — fall through to defaults below
                System.Diagnostics.Debug.WriteLine($"Error loading user settings: {ex.Message}");
            }
        }

        // First launch, or recovery from a corrupt file
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

    public static void Save()
    {
        try
        {
            var settingsWrapper = new SettingsWrapper { Settings = Settings };
            var json = JsonSerializer.Serialize(
                settingsWrapper,
                JsonSourceGenerationContext.Default.SettingsWrapper // Get the pre-generated info for our type
            );
            File.WriteAllText(_userSettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
        }
    }
}