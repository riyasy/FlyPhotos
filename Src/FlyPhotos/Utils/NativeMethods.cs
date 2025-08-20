using System;
using System.Runtime.InteropServices;

namespace FlyPhotos.Utils;

internal static class NativeMethods
{
    // HKL is essentially a handle, so IntPtr is appropriate.
    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    // We specify CharSet.Ansi because we are calling the 'A' (ANSI) version.
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    internal static extern short VkKeyScanExA(byte ch, IntPtr dwhkl);


    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    // DllImport tells .NET how to call the native function from user32.dll
    // SetLastError=true is a good practice for Win32 functions that support it.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] // Explicitly state the return is a Win32 BOOL
    internal static extern bool GetCursorPos(out POINT lpPoint);


    // Define the constant for the window message
    internal const uint WM_SETICON = 0x0080;

    // Define the P/Invoke signature for SendMessage
    // We use IntPtr for the return and parameters to make it general-purpose.
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}