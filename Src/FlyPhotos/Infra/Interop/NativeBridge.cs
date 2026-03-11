#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlyPhotos.Core.Model;
using FlyPhotos.Services.ExternalAppListing;
using NLog;

namespace FlyPhotos.Infra.Interop;

#region P/Invoke Declarations

/// <summary>
/// Provides static methods for direct interoperability with the native library (FlyNativeLib.dll).
/// This class handles the P/Invoke declarations for functions exposed by the C++ DLL.
/// </summary>
internal static partial class NativeBridge
{
    /// <summary>
    /// The name of the native DLL containing the functions to be imported.
    /// </summary>
    private const string DllName = "FlyNativeLib.dll";

    /// <summary>
    /// Represents the signature of a callback function used by <see cref="GetFileListFromExplorer"/>.
    /// This delegate is invoked by the native code for each file path found.
    /// </summary>
    /// <param name="filePath">The path of a file, marshalled as a wide character string.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FileListCallback([MarshalAs(UnmanagedType.LPWStr)] string filePath);

    /// <summary>
    /// Imports the native `GetFileListFromExplorer` function from `FlyNativeLib.dll`.
    /// This function typically interacts with the Windows Explorer shell to retrieve list of files in the open File Explorer window.
    /// </summary>
    /// <param name="callback">A delegate that the native function will call for each file path it finds.</param>
    /// <returns>An HRESULT indicating the success or failure of the native operation.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetFileListFromExplorer(FileListCallback callback);

    /// <summary>
    /// Represents the signature of a callback function used by <see cref="GetWicDecoders"/>.
    /// This delegate is invoked by the native code for each WIC codec found.
    /// </summary>
    /// <param name="friendlyName">The friendly name of the codec, marshalled as a wide character string.</param>
    /// <param name="fileExtensions">A comma-separated string of file extensions supported by the codec, marshalled as a wide character string.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CodecInfoCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName,
        [MarshalAs(UnmanagedType.LPWStr)] string fileExtensions);

    /// <summary>
    /// Imports the native `GetWicDecoders` function from `FlyNativeLib.dll`.
    /// This function enumerates available Windows Imaging Component (WIC) decoders.
    /// </summary>
    /// <param name="callback">A delegate that the native function will call for each WIC decoder it finds.</param>
    /// <returns>An HRESULT indicating the success or failure of the native operation.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetWicDecoders(CodecInfoCallback callback);

    /// <summary>
    /// Imports the native `ShowExplorerContextMenu` function from `FlyNativeLib.dll`.
    /// This function displays the Windows Explorer context menu for a specified file at a given screen coordinate.
    /// </summary>
    /// <param name="ownerHwnd">The window handle (HWND) of the owner window for the context menu.</param>
    /// <param name="filePath">The path of the file for which to show the context menu.</param>
    /// <param name="x">The X-coordinate (screen coordinates) where the context menu should appear.</param>
    /// <param name="y">The Y-coordinate (screen coordinates) where the context menu should appear.</param>
    /// <returns>An integer indicating the result of the operation (0 for success, non-zero for failure).</returns>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int ShowExplorerContextMenu(IntPtr ownerHwnd, string filePath, int x, int y);

    /// <summary>
    /// Callback delegate for receiving app entry information (Win32 or UWP) from native code.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void ShortcutCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPWStr)] string aumid,
        int isUwp,
        IntPtr iconData, int iconSize, int width, int height
    );

    /// <summary>
    /// Enumerates Start Menu shortcuts using the native library.
    /// </summary>
    /// <param name="callback">The callback to invoke for each shortcut found.</param>
    /// <returns>Wait status or error code.</returns>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EnumerateStartMenuShortcuts(ShortcutCallback callback);

    /// <summary>
    /// Callback delegate for receiving a single extracted icon from native code.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SingleIconCallback(IntPtr iconData, int iconSize, int width, int height);

    /// <summary>
    /// Extracts the icon for a single UWP app using its AUMID.
    /// </summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetUwpAppIcon(string aumid, SingleIconCallback callback);
}

#endregion

/// <summary>
/// Data Transfer Object for an installed application entry (Win32 or UWP).
/// </summary>
public record AppShortcutDto(
    string Name,
    string Path,
    string Aumid,
    bool IsUwp,
    byte[] IconPixels,
    int Width,
    int Height);

/// <summary>
/// Provides a high-level, managed wrapper for native functionalities exposed by `FlyNativeLib.dll`.
/// This class simplifies interaction with the native library and handles common tasks like error checking and data marshaling.
/// </summary>
public static class NativeWrapper
{
    /// <summary>
    /// NLog logger instance for logging messages, warnings, and errors.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Retrieves a list of file paths from the Windows Explorer shell.
    /// This typically corresponds to files in the currently open and active Explorer window.
    /// </summary>
    /// <returns>A <see cref="List{T}"/> of strings, where each string is a full file path.</returns>
    public static List<string> GetFileListFromExplorerWindow()
    {
        var files = new List<string>();
        var hresult = NativeBridge.GetFileListFromExplorer(FileListCallback);
        if (hresult >= 0) // SUCCEEDED
            return files;

        Logger.Error($"GetFileListFromExplorer failed with HRESULT: {hresult}");
        return [];

        void FileListCallback(string filePath)
        {
            files.Add(filePath);
        }
    }

    /// <summary>
    /// Retrieves a list of available Windows Imaging Component (WIC) decoders.
    /// Each decoder's friendly name and supported file extensions are captured.
    /// </summary>
    /// <returns>A <see cref="List{T}"/> of <see cref="CodecInfo"/> objects, each representing a WIC decoder.</returns>
    /// <exception cref="MarshalDirectiveException">Thrown if the native call to get WIC decoders fails (HRESULT is negative).</exception>
    public static List<CodecInfo> GetWicDecoders()
    {
        var codecs = new List<CodecInfo>();
        var hresult = NativeBridge.GetWicDecoders(CodecInfoCallback);
        if (hresult >= 0)
            return codecs;
        Logger.Error($"GetWicDecoders failed with HRESULT: {hresult}");
        return [];

        void CodecInfoCallback(string friendlyName, string extensions)
        {
            var fileExtensions = new List<string>();
            // Create a ReadOnlySpan to slice the string without allocating memory
            ReadOnlySpan<char> span = extensions;

            foreach (Range range in span.Split(','))
            {
                ReadOnlySpan<char> part = span[range];
                if (part.Length > 0)
                    fileExtensions.Add(part.ToString());
            }

            var codec = new CodecInfo
            {
                FriendlyName = friendlyName,
                Type = "WIC",
                FileExtensions = fileExtensions
            };
            codecs.Add(codec);
        }
    }

    /// <summary>
    /// Displays the Windows Explorer context menu for a specified file at given screen coordinates.
    /// </summary>
    /// <param name="ownerWindow">The HWND of owner <see cref="Microsoft.UI.Xaml.Window"/> for the context menu.</param>
    /// <param name="filePath">The full path to the file for which to show the context menu.</param>
    /// <param name="lpPointX">The X-coordinate (screen coordinates) where the context menu should appear.</param>
    /// <param name="lpPointY">The Y-coordinate (screen coordinates) where the context menu should appear.</param>
    public static void ShowContextMenu(IntPtr ownerWindow, string filePath, int lpPointX,
        int lpPointY)
    {
        try
        {
            var returnCode = NativeBridge.ShowExplorerContextMenu(ownerWindow, filePath, lpPointX, lpPointY);
            if (returnCode != 0) // Check for non-zero return code, which indicates failure.
            {
                Logger.Error($"NativeBridge - ShowExplorerContextMenu failed with error code {returnCode}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NativeBridge - ShowContextMenu failed with exception");
        }
    }

    /// <summary>
    /// Blocking call to scan the Apps virtual folder for all installed apps (Win32 + UWP).
    /// Run this on a background thread. Replaces the old LoadAllWin32ProgramShortcuts method.
    /// </summary>
    public static List<AppShortcutDto> LoadAllApps()
    {
        var rawDataList = new List<AppShortcutDto>(256);
        try
        {
            // Keep the delegate alive for the entire duration of the native call.
            NativeBridge.ShortcutCallback callback = CallbackDelegate;
            NativeBridge.EnumerateStartMenuShortcuts(callback);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NativeBridge - Error scanning apps");
        }
        return rawDataList;

        void CallbackDelegate(string name, string path, string aumid, int isUwp,
                              IntPtr iconData, int iconSize, int width, int height)
        {
            // Sanity-check pixel buffer before allocating.
            if (iconSize <= 0 || iconSize > 10 * 1024 * 1024 || width <= 0 || height <= 0)
                return;

            byte[] pixels = new byte[iconSize];
            Marshal.Copy(iconData, pixels, 0, iconSize);

            rawDataList.Add(new AppShortcutDto(
                Name:       name,
                Path:       path,
                Aumid:      aumid,
                IsUwp:      isUwp != 0,
                IconPixels: pixels,
                Width:      width,
                Height:     height));
        }
    }

    /// <summary>
    /// Extracts the raw BGRA icon for a single UWP app via its AUMID, matching the scan quality.
    /// </summary>
    public static RawIconData? GetUwpAppIcon(string aumid)
    {
        RawIconData? result = null;
        try
        {
            NativeBridge.SingleIconCallback callback = CallbackDelegate;
            NativeBridge.GetUwpAppIcon(aumid, callback);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"NativeBridge - Error extracting UWP icon for {aumid}");
        }
        return result;

        void CallbackDelegate(IntPtr iconData, int iconSize, int width, int height)
        {
            if (iconSize <= 0 || iconSize > 10 * 1024 * 1024 || width <= 0 || height <= 0)
                return;

            byte[] pixels = new byte[iconSize];
            Marshal.Copy(iconData, pixels, 0, iconSize);

            result = new RawIconData(pixels, width, height);
        }
    }
}



