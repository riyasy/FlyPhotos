using System;
using System.IO;
using FlyPhotos.Infra.Interop;
using Windows.Storage;


namespace FlyPhotos.Services;

internal static class PathResolver
{
    /// <summary>
    /// Gets a value indicating whether the application is running in a packaged context.
    /// </summary>
    public static bool IsPackagedApp { get; }

    // Data paths resolved once at startup so the getters are plain field reads.
    // Packaged: LocalFolder.Path is a three-hop COM chain (activation factory → StorageFolder →
    // marshalled string) that is guaranteed by Windows to already exist, so we walk it a single
    // time and skip directory creation. Unpackaged: built from Environment/Temp and created once
    // here (Directory.CreateDirectory is a no-op when the folder already exists).
    private static readonly string _baseDataPath;
    private static readonly string _logDataPath;

    static PathResolver()
    {
        IsPackagedApp = DetectPackaged();

        if (IsPackagedApp)
        {
            _baseDataPath = ApplicationData.Current.LocalFolder.Path;
            _logDataPath = _baseDataPath;
        }
        else
        {
            _baseDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyPhotos");
            _logDataPath = Path.Combine(Path.GetTempPath(), "FlyPhotos");
            Directory.CreateDirectory(_baseDataPath);
            Directory.CreateDirectory(_logDataPath);
        }
    }

    // Non-throwing packaging probe. Package.Current throws InvalidOperationException for
    // unpackaged processes, and the throw/catch alone costs ~15 ms on release builds.
    // GetCurrentPackageFullName reports the same fact via a return code: querying with a
    // zero-length buffer returns APPMODEL_ERROR_NO_PACKAGE when unpackaged, or
    // ERROR_INSUFFICIENT_BUFFER (any other value) when packaged.
    private static bool DetectPackaged()
    {
        uint length = 0;
        return Win32Methods.GetCurrentPackageFullName(ref length, IntPtr.Zero)
               != Win32Methods.APPMODEL_ERROR_NO_PACKAGE;
    }

    public static string GetDbFolderPath() => _baseDataPath;

    public static string GetLogFolderPath() => _logDataPath;

    public static string GetUserSettingsFolder() => _baseDataPath;
}
