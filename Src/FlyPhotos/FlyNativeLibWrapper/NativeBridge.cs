using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices; // Required for CallConvStdcall
using System.Runtime.InteropServices;
using FlyPhotos.Data;

namespace FlyPhotos.FlyNativeLibWrapper;

// The class is now internal to encapsulate the P/Invoke calls and partial for the source generator.
internal static partial class NativeBridge
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

    // Switched to LibraryImport for compile-time marshalling code generation.
    // Added UnmanagedCallConv to preserve the StdCall calling convention.
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetFileListFromExplorer(FileListCallback callback);

    // Set StringMarshalling to match the original CharSet.Unicode.
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowExplorerContextMenu(string filePath, int x, int y);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int GetWicDecoders(CodecInfoCallback callback);
}

public static class CliWrapper
{
    public static List<string> GetFileListFromExplorerWindow()
    {
        var files = new List<string>();

        // Call the native function with our callback.
        // This code remains unchanged as it correctly calls the wrapper.
        var hr = NativeBridge.GetFileListFromExplorer(FileListCallback);

        // Optional: Check HRESULT (hr) for success or failure
        if (hr >= 0) // SUCCEEDED
            return files;

        // Handle error, maybe return an empty list or throw an exception
        return [];

        // The callback function adds each file to our list.
        void FileListCallback(string filePath)
        {
            files.Add(filePath);
        }
    }

    // You can call this directly via the ShowContextMenu wrapper
    // bool success = CliWrapper.ShowContextMenu("C:\\path\\to\\file.jpg", x, y);

    // A high-level wrapper for WIC codecs
    public static List<CodecInfo> GetWicDecoders()
    {
        var codecs = new List<CodecInfo>();

        // Call the native function
        var hresult = NativeBridge.GetWicDecoders(CodecInfoCallback);

        // Optional: Check the HRESULT for errors
        if (hresult < 0)
            // Throw an exception or handle the error from the native call
            Marshal.ThrowExceptionForHR(hresult);

        return codecs;

        // The delegate instance must be kept alive during the native call,
        // so the garbage collector doesn't clean it up prematurely.
        // Assigning it to a local variable is sufficient.
        void CodecInfoCallback(string friendlyName, string extensions)
        {
            var codec = new CodecInfo
            {
                FriendlyName = friendlyName,
                // The extensions string from C++ is comma-separated, e.g., ".jpg,.jpeg,.jpe"
                FileExtensions = [.. extensions.ToUpperInvariant().Split([','], StringSplitOptions.RemoveEmptyEntries)]
            };
            codecs.Add(codec);
        }
    }

    public static void ShowContextMenu(string filePath, int lpPointX, int lpPointY)
    {
        NativeBridge.ShowExplorerContextMenu(filePath, lpPointX, lpPointY);
    }
}