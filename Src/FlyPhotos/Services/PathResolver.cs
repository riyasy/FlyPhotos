using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.Storage;


namespace FlyPhotos.Services;

internal static class PathResolver
{
    /// <summary>
    /// Gets a value indicating whether the application is running in a packaged context.
    /// </summary>
    public static bool IsPackagedApp { get; }

    static PathResolver()
    {
        try
        {
            // If this call succeeds, the application is packaged.
            // Package.Current will throw an exception if the process is not packaged.
            if (Package.Current != null)
            {
                IsPackagedApp = true;
            }
        }
        catch (InvalidOperationException)
        {
            // The exception indicates the process is not packaged.
            IsPackagedApp = false;
        }
    }

    public static string GetDbFolderPath()
    {
        var dbFolderPath = IsPackagedApp ?
            ApplicationData.Current.LocalFolder.Path :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyPhotos");

        if (!IsPackagedApp  && dbFolderPath != null && !Directory.Exists(dbFolderPath)) 
            Directory.CreateDirectory(dbFolderPath);
        return dbFolderPath;
    }

    public static string GetLogFolderPath()
    {
        var logFolder = IsPackagedApp ?
            ApplicationData.Current.LocalFolder.Path :
            Path.Combine(Path.GetTempPath(), "FlyPhotos");

        if (!IsPackagedApp && logFolder != null && !Directory.Exists(logFolder)) 
            Directory.CreateDirectory(logFolder);

        return logFolder;
    }

    public static string GetDefaultSettingsFolder()
    {
        return AppContext.BaseDirectory;
    }

    public static string GetUserSettingsFolder()
    {
        var userSettingsFolder = IsPackagedApp
            ? ApplicationData.Current.LocalFolder.Path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlyPhotos");

        if (!IsPackagedApp && userSettingsFolder != null && !Directory.Exists(userSettingsFolder)) 
            Directory.CreateDirectory(userSettingsFolder);

        return userSettingsFolder;
    }
}