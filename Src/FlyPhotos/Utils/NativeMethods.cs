using System;
using System.Runtime.InteropServices;

namespace FlyPhotos.Utils;

internal static partial class NativeMethods
{
    // Converted to LibraryImport.
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetKeyboardLayout(uint idThread);

    // Converted to LibraryImport. The 'A' suffix in the method name makes CharSet redundant.
    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanExA")]
    internal static partial short VkKeyScanEx(byte ch, IntPtr dwhkl);


    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    // Converted to LibraryImport, preserving SetLastError.
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] // This is still valid and good for clarity.
    internal static partial bool GetCursorPos(out POINT lpPoint);


    // Define the constant for the window message
    internal const uint WM_SETICON = 0x0080;

    // Converted to LibraryImport, preserving SetLastError.
    // CharSet is removed because this overload does not marshal any strings.
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    internal static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}