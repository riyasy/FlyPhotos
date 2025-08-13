// In AppConfig.cs

using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using Microsoft.Extensions.Configuration;

namespace FlyPhotos.AppSettings;

public class SettingsWrapper
{
    public AppSettings Settings { get; set; }
}


public static class AppConfig
{
    private static string _userSettingsPath;
    public static AppSettings Settings { get; private set; }

    // This method reads the config files when the app starts.
    public static void Initialize()
    {
        string userSettingsFolder;

        if (App.Packaged)
        {
            userSettingsFolder = ApplicationData.Current.LocalFolder.Path;
        }
        else
        {
            var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "FlyPhotos";
            userSettingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                appName);
            if (!Directory.Exists(userSettingsFolder)) Directory.CreateDirectory(userSettingsFolder);
        }

        _userSettingsPath = Path.Combine(userSettingsFolder, "usersettings.json");

        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false)
            .AddJsonFile(_userSettingsPath, true, true)
            .Build();

        Settings = configuration.GetSection("Settings").Get<AppSettings>();
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
            Console.WriteLine($"Error saving settings: {ex.Message}");
        }
    }
}