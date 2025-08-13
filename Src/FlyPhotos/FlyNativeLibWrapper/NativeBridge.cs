using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FlyPhotos.FlyNativeLibWrapper;

public static class NativeBridge
{
    // Define the delegate for the codec info callback
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CodecInfoCallback(
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName,
        [MarshalAs(UnmanagedType.LPWStr)] string fileExtensions);

    // Define the delegate that matches the C++ FileListCallback
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FileListCallback([MarshalAs(UnmanagedType.LPWStr)] string filePath);

    private const string DllName = "FlyNativeLib.dll"; // The name of your new native DLL

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int GetFileListFromExplorer(FileListCallback callback);

    [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowExplorerContextMenu(string filePath, int x, int y);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int GetWicDecoders(CodecInfoCallback callback);
}

public static class CliWrapper
{
    public static List<string> GetFileListFromExplorerWindow()
    {
        var files = new List<string>();

        // The callback function adds each file to our list.
        NativeBridge.FileListCallback callback = filePath => { files.Add(filePath); };

        // Call the native function with our callback.
        var hr = NativeBridge.GetFileListFromExplorer(callback);

        // Optional: Check HRESULT (hr) for success or failure
        if (hr >= 0) // SUCCEEDED
            return files;

        // Handle error, maybe return an empty list or throw an exception
        return new List<string>();
    }

    // You can call this directly
    // bool success = NativeBridge.ShowExplorerContextMenu("C:\\path\\to\\file.jpg", x, y);

    // A high-level wrapper for WIC codecs
    public static List<CodecInfo> GetWicDecoders()
    {
        var codecs = new List<CodecInfo>();

        // The delegate instance must be kept alive during the native call,
        // so the garbage collector doesn't clean it up prematurely.
        // Assigning it to a local variable is sufficient.
        NativeBridge.CodecInfoCallback callback = (friendlyName, extensions) =>
        {
            var codec = new CodecInfo
            {
                FriendlyName = friendlyName,
                // The extensions string from C++ is comma-separated, e.g., ".jpg,.jpeg,.jpe"
                FileExtensions = new List<string>(extensions.ToUpperInvariant()
                    .Split([','], StringSplitOptions.RemoveEmptyEntries))
            };
            codecs.Add(codec);
        };

        // Call the native function
        var hresult = NativeBridge.GetWicDecoders(callback);

        // Optional: Check the HRESULT for errors
        if (hresult < 0)
            // Throw an exception or handle the error from the native call
            Marshal.ThrowExceptionForHR(hresult);

        return codecs;
    }

    public static void ShowContextMenu(string filePath, int lpPointX, int lpPointY)
    {

    }
}