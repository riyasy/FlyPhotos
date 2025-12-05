#nullable enable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.UI.Input;

namespace FlyPhotos.NativeWrappers;

/// <summary>
/// A static utility class providing methods to interact with native Win32 cursor APIs.
/// It handles loading custom cursors from files and converting native cursor handles (HCURSOR)
/// into WinUI 3's managed InputCursor objects using modern P/Invoke and COM interop source generators.
/// This class must be partial because it uses LibraryImportAttribute.
/// </summary>
public static partial class Win32CursorMethods
{
    /// <summary>
    /// Loads a custom cursor from a specified file path and converts it into a WinUI 3 InputCursor.
    /// </summary>
    /// <param name="filePath">The absolute path to the cursor file (.cur or .ani).</param>
    /// <returns>An InputCursor object representing the loaded cursor.</returns>
    /// <exception cref="ArgumentNullException">Thrown if filePath is null.</exception>
    /// <exception cref="Win32Exception">Thrown if the native LoadCursorFromFileW function fails to load the cursor.</exception>
    public static InputCursor? LoadCursor(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var hCursor = LoadCursorFromFileW(filePath);
        return hCursor == 0 ? throw new Win32Exception(Marshal.GetLastWin32Error()) : CreateCursorFromCursorHandle(hCursor);
    }

    /// <summary>
    /// Creates a WinUI 3 InputCursor from a native Windows cursor handle (HCURSOR).
    /// </summary>
    /// <param name="hCursor">The native cursor handle.</param>
    /// <returns>An InputCursor object if successful; otherwise, null.</returns>
    public static InputCursor? CreateCursorFromCursorHandle(nint hCursor)
    {
        if (hCursor == 0)
            return null;

        // To create a WinRT InputCursor from a native handle, we must use the COM activation factory.
        const string classId = "Microsoft.UI.Input.InputCursor";

        // 1. Create a native HSTRING for the WinRT class ID.
        _ = WindowsCreateString(classId, classId.Length, out var hs);
        // 2. Get the activation factory for the InputCursor class.
        _ = RoGetActivationFactory(hs, typeof(IActivationFactory).GUID, out var fac);
        // 3. Clean up the HSTRING.
        _ = WindowsDeleteString(hs);

        if (fac is not IInputCursorStaticsInterop interop)
            return null;

        // 4. Use the factory's interop interface to create the cursor from the handle.
        interop.CreateFromHCursor(hCursor, out var cursorAbi);

        // 5. Marshal the returned native object (ABI pointer) into a managed .NET object.
        return cursorAbi == 0 ? null : WinRT.MarshalInspectable<InputCursor>.FromAbi(cursorAbi);
    }

    #region COM Interface Definitions

    /// <summary>
    /// Represents the COM interop interface for Microsoft.UI.Input.InputCursor statics.
    /// This specific interface (identified by its GUID) exposes a method to create a cursor from a native HCURSOR.
    /// [GeneratedComInterface] is the modern, source-generated approach for COM interop.
    /// </summary>
    [GeneratedComInterface, Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IInputCursorStaticsInterop
    {
        // Base IInspectable methods (required for WinRT COM interfaces).
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();

        // The key interop method. [PreserveSig] ensures we get the raw HRESULT as an 'int' return value
        // instead of the runtime automatically converting failure HRESULTs into exceptions.
        [PreserveSig]
        int CreateFromHCursor(nint hCursor, out nint inputCursor);
    }

    /// <summary>
    /// Represents the generic WinRT activation factory COM interface (IActivationFactory).
    /// It's used to create instances of WinRT classes that provide a parameterless constructor.
    /// </summary>
    [GeneratedComInterface, Guid("00000035-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IActivationFactory
    {
        // Base IInspectable methods.
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();

        [PreserveSig]
        int ActivateInstance(out nint instance);
    }

    #endregion

    #region P/Invoke Method Definitions

    // P/Invoke definitions use [LibraryImport] for compile-time source generation,
    // which is more performant and AOT-friendly than the older [DllImport].

    /// <summary>Gets the activation factory for a specified WinRT class.</summary>
    [LibraryImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static partial int RoGetActivationFactory(nint runtimeClassId, in Guid iid, out IActivationFactory factory);

    /// <summary>Loads a cursor from a specified file. The 'W' denotes the Unicode version of the function.</summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadCursorFromFileW(string name);

    /// <summary>Destroys a cursor handle created by functions like CreateCursor.
    /// Note: Do not use this on handles from LoadCursorFromFileW, as they are shared system resources.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyCursor(nint hCursor);

    /// <summary>Creates a native WinRT HSTRING from a .NET string.</summary>
    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string? sourceString, int length, out nint @string);

    /// <summary>Frees a native WinRT HSTRING to prevent memory leaks.</summary>
    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static partial int WindowsDeleteString(nint @string);

    #endregion
}