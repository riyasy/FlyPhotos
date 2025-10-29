using System;
using System.Runtime.InteropServices;

namespace FlyPhotos.NativeWrappers;

/// <summary>
/// Provides access to various Win32 API methods through P/Invoke.
/// This class encapsulates native functions from `user32.dll` and other system libraries.
/// </summary>
internal static partial class Win32Methods
{
    /// <summary>
    /// Imports the `GetKeyboardLayout` function from `user32.dll`.
    /// This function retrieves the active input locale identifier (formerly called the keyboard layout handle) for the specified thread.
    /// </summary>
    /// <param name="idThread">The identifier of the thread to query, or 0 for the current thread.</param>
    /// <returns>The input locale identifier for the thread.</returns>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>
    /// Imports the `VkKeyScanExA` function from `user32.dll`.
    /// This function translates a character to the corresponding virtual-key code and shift status for the current keyboard.
    /// The 'A' suffix indicates the ANSI version, taking a single-byte character.
    /// </summary>
    /// <param name="ch">The character to be translated.</param>
    /// <param name="dwhkl">The input locale identifier to use for the translation.</param>
    /// <returns>
    /// If the function succeeds, the low-order byte of the return value contains the virtual-key code,
    /// and the high-order byte contains the shift state. If the function fails, the return value is –1.
    /// </returns>
    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanExA")]
    internal static partial short VkKeyScanEx(byte ch, IntPtr dwhkl);

    /// <summary>
    /// Represents a point on the screen with X and Y coordinates.
    /// This structure is used for various GDI and user interface functions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    /// <summary>
    /// Imports the `GetCursorPos` function from `user32.dll`.
    /// This function retrieves the position of the mouse cursor, in screen coordinates.
    /// </summary>
    /// <param name="lpPoint">A <see cref="POINT"/> structure that receives the screen coordinates of the cursor.</param>
    /// <returns>
    /// <see langword="true"/> if the function succeeds; otherwise, <see langword="false"/>.
    /// To get extended error information, call <see cref="Marshal.GetLastWin32Error"/>.
    /// </returns>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] // Specifies how the boolean return value is marshalled.
    internal static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Defines the constant for the `WM_SETICON` window message.
    /// This message is sent to a window to associate a new large or small icon with the window.
    /// </summary>
    internal const uint WM_SETICON = 0x0080;

    /// <summary>
    /// Imports the `SendMessageW` function from `user32.dll`.
    /// This function sends the specified message to a window or windows.
    /// The 'W' suffix indicates the Unicode version.
    /// </summary>
    /// <param name="hWnd">A handle to the window whose window procedure will receive the message.</param>
    /// <param name="Msg">The message to be sent.</param>
    /// <param name="wParam">Additional message-specific information.</param>
    /// <param name="lParam">Additional message-specific information.</param>
    /// <returns>The return value specifies the result of the message processing; its value depends on the message sent.</returns>
    /// <remarks>
    /// <paramref name="wParam"/> and <paramref name="lParam"/> are typically `IntPtr` to accommodate various message parameters.
    /// To get extended error information, call <see cref="Marshal.GetLastWin32Error"/>.
    /// </remarks>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    internal static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}