#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NLog;

namespace FlyPhotos.Services.ExternalAppListing;

/// <summary>
/// Specifies the type of an installed application.
/// </summary>
public enum AppType
{
    /// <summary>
    /// Represents a Microsoft Store application.
    /// </summary>
    Store,
    /// <summary>
    /// Represents a standard Win32 desktop application.
    /// </summary>
    Win32
}

/// <summary>
/// Represents an installed application.
/// This abstract class serves as the base for concrete application types (Win32, Store).
/// </summary>
public abstract class InstalledApp
{
    /// <summary>
    /// Gets or sets the display name of the application.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icon of the application.
    /// This is used for binding in the UI.
    /// </summary>
    public ImageSource? Icon { get; set; }

    /// <summary>
    /// Gets or sets the type of the application.
    /// </summary>
    public AppType Type { get; set; }

    /// <summary>
    /// Launches the application.
    /// </summary>
    /// <param name="filePath">Path to image</param>
    public abstract Task LaunchAsync(string filePath);

    /// <summary>
    /// Decodes the raw icon data into an <see cref="ImageSource"/>.
    /// </summary>
    public abstract Task DecodeIconAsync();

    /// <summary>
    /// Serializes the application state to a string.
    /// </summary>
    /// <returns>A string representation of the app's state.</returns>
    public abstract string GetSerializedState();

}

/// <summary>
/// Represents the raw icon pixels and dimensions for a Win32 app.
/// </summary>
/// <param name="IconPixels">The raw pixel data (BGRA32).</param>
/// <param name="Width">The width of the icon.</param>
/// <param name="Height">The height of the icon.</param>
public record Win32IconData(byte[] IconPixels, int Width, int Height);

/// <summary>
/// Represents a Win32 desktop application.
/// </summary>
public class Win32App : InstalledApp
{
    /// <summary>
    /// Logger instance for logging errors.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets or sets the path to the executable file.
    /// </summary>
    public string? ExePath { get; set; }

    /// <summary>
    /// Gets or sets the icon data extracted from native code.
    /// </summary>
    public Win32IconData? IconData { get; set; }

    public override Task LaunchAsync(string filePath)
    {
        if (string.IsNullOrEmpty(ExePath)) return Task.CompletedTask;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = ExePath, Arguments = $"\"{filePath}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Win32App Launch Error");
        }
        return Task.CompletedTask;
    }

    public override Task DecodeIconAsync()
    {
        if (IconData != null)
        {
            var bmp = new WriteableBitmap(IconData.Width, IconData.Height);
            using var stream = bmp.PixelBuffer.AsStream();
            stream.Write(IconData.IconPixels, 0, IconData.IconPixels.Length);
            Icon = bmp;
        }
        return Task.CompletedTask;
    }

    public override string GetSerializedState()
    {
        return $"Win32|{DisplayName}|{ExePath}";
    }

}

/// <summary>
/// Represents a Microsoft Store application.
/// </summary>
public class StoreApp : InstalledApp
{
    /// <summary>
    /// Logger instance for logging errors.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets or sets the Application User Model ID (AUMID).
    /// Used for launching the app.
    /// </summary>
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// Gets or sets the Package Family Name.
    /// Used for looking up the app package.
    /// </summary>
    public string? PackageFamilyName { get; set; }

    /// <summary>
    /// Gets or sets the raw icon data (bytes).
    /// </summary>
    public byte[]? IconData { get; set; }

    public override async Task LaunchAsync(string filePath)
    {
        if (string.IsNullOrEmpty(AppUserModelId) || string.IsNullOrEmpty(filePath))
            return;

        try
        {
            // Convert path to StorageFile
            var file = await StorageFile.GetFileFromPathAsync(filePath);

            // Extract Package Family Name from AUMID
            string packageFamilyName = AppUserModelId.Split('!')[0];

            var options = new LauncherOptions
            {
                TargetApplicationPackageFamilyName = packageFamilyName,
                DisplayApplicationPicker = false
            };

            bool success = await Launcher.LaunchFileAsync(file, options);

            if (!success)
            {
                Logger.Error("StoreApp Launch failed");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "StoreApp Launch Error");
        }
    }

    public override async Task DecodeIconAsync()
    {
        if (IconData == null)
            return;

        var image = new BitmapImage();
        using var ms = new MemoryStream(IconData);
        await image.SetSourceAsync(ms.AsRandomAccessStream());
        Icon = image;
    }

    public override string GetSerializedState()
    {
        return $"Store|{DisplayName}|{AppUserModelId}|{PackageFamilyName}";
    }
}
