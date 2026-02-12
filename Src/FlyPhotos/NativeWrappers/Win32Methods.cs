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
    /// Performs an operation on a specified file, such as opening a file or showing its properties.
    /// This is an extended version of ShellExecute and is recommended for new applications.
    /// </summary>
    /// <param name="pExecInfo">A reference to a <see cref="SHELLEXECUTEINFO"/> structure that contains and receives information about the operation.</param>
    /// <returns>
    /// Returns <see langword="true"/> if successful; otherwise, <see langword="false"/>.
    /// Call <see cref="Marshal.GetLastWin32Error"/> to get extended error information on failure.
    /// </returns>
    /// <remarks>
    /// This function uses <c>[DllImport]</c> instead of <c>[LibraryImport]</c> because its struct parameter (<see cref="SHELLEXECUTEINFO"/>)
    /// is non-blittable (contains strings) and is passed by reference (<c>ref</c>). This specific scenario is not yet supported
    /// by the <c>[LibraryImport]</c> source generator, making <c>[DllImport]</c> the correct choice here.
    /// </remarks>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO pExecInfo);

    /// <summary>
    /// Contains information used by the <see cref="ShellExecuteEx"/> function.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpFile;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    /// <summary>
    /// Activates a window and displays it in its current size and position.
    /// This is one of the command flags for the <see cref="SHELLEXECUTEINFO.nShow"/> member.
    /// </summary>
    internal const int SW_SHOW = 5;

    /// <summary>
    /// Use this flag in the <see cref="SHELLEXECUTEINFO.fMask"/> member when the <see cref="SHELLEXECUTEINFO.lpIDList"/> member is being used.
    /// This is typically for operations on Shell namespace objects that are not part of the file system.
    /// </summary>
    internal const uint SEE_MASK_INVOKEIDLIST = 12;

    /// <summary>
    /// Constant value for the <c>WM_COPYDATA</c> message used to send data between processes.
    /// Applications send this message to pass data blocks to another application.
    /// </summary>
    internal const int WM_COPYDATA = 0x004A;

    /// <summary>
    /// Structure used with the <c>WM_COPYDATA</c> message to send data to another process.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        /// <summary>
        /// User-defined data. Applications can use this field to pass a value identifying the data type.
        /// </summary>
        public IntPtr dwData;

        /// <summary>
        /// Size, in bytes, of the data pointed to by <see cref="lpData"/>.
        /// </summary>
        public int cbData;

        /// <summary>
        /// Pointer to data to be passed to the receiving application. The data is copied into the address space of the receiving process.
        /// </summary>
        public IntPtr lpData;
    }

    /// <summary>
    /// Finds a top-level window whose class name and/or window name match the specified strings.
    /// This is a managed declaration of the Win32 <c>FindWindow</c> function from <c>user32.dll</c>.
    /// </summary>
    /// <param name="lpClassName">The class name or a class atom created by a previous call to RegisterClass. If this parameter is null, it finds any window whose title matches <paramref name="lpWindowName"/>.</param>
    /// <param name="lpWindowName">The window name (the window's title). If this parameter is null, all window names match.</param>
    /// <returns>
    /// A handle to the window that has the specified class name and window name. If no such window exists, <see cref="IntPtr.Zero"/> is returned.
    /// </returns>
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string lpClassName, string lpWindowName);

    /// <summary>
    /// Sends the specified message to a window or windows. This overload is used to send a <see cref="COPYDATASTRUCT"/> with the <c>WM_COPYDATA</c> message.
    /// This is a managed declaration of the Win32 <c>SendMessage</c> function from <c>user32.dll</c>.
    /// </summary>
    /// <param name="hWnd">A handle to the window whose window procedure will receive the message.</param>
    /// <param name="Msg">The message to be sent. Use <see cref="WM_COPYDATA"/> to send data blocks.</param>
    /// <param name="wParam">Additional message-specific information. When sending <c>WM_COPYDATA</c>, this is typically the sender window handle.</param>
    /// <param name="lParam">A reference to a <see cref="COPYDATASTRUCT"/> that contains the data to be passed to the receiving application.</param>
    /// <returns>
    /// The return value specifies the result of the message processing; its value depends on the message sent. For <c>WM_COPYDATA</c>, the return value is typically the value returned by the receiving window procedure.
    /// </returns>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);
}