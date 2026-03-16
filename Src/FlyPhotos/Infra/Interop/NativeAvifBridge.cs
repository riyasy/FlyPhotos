using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlyPhotos.Infra.Interop;

/// <summary>
///     Nested class to wrap all P/Invoke declarations to the FlyNativeLibHeif C++ DLL.
/// </summary>
internal static partial class NativeAvifBridge
{
    private const string DllName = "FlyNativeLibHeif.dll";

    /// <summary>
    ///     Opens an AVIF animation given its pinned memory address and size.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr OpenAvifAnimation(IntPtr data, nint size);

    /// <summary>
    ///     Returns whether the provided handle contains a sequence track.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool IsAvifAnimated(IntPtr handle);

    /// <summary>Gets the pixel width of the canvas for the animation sequence.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetAvifCanvasWidth(IntPtr handle);

    /// <summary>Gets the pixel height of the canvas for the animation sequence.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetAvifCanvasHeight(IntPtr handle);

    /// <summary>
    ///     Decodes the next frame from the track into the provided `outBgraBuffer`.
    ///     Returns the duration of the decoded frame in MS, or 0 on EOF/error.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DecodeNextAvifFrame(IntPtr handle, IntPtr outBgraBuffer);

    /// <summary>Resets the internal decoder to the beginning of the sequence to allow looping.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void ResetAvifAnimation(IntPtr handle);

    /// <summary>Frees the unmanaged `AnimatedAvifReader` memory from C++.</summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void CloseAvifAnimation(IntPtr handle);
}