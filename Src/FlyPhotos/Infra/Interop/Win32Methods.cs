#nullable enable
using System;
using System.Runtime.InteropServices;

namespace FlyPhotos.Infra.Interop;

/// <summary>
/// Provides access to various Win32 API methods through P/Invoke.
/// This class encapsulates native functions from `user32.dll` and other system libraries.
/// </summary>
/// <remarks>
/// This is a <c>partial</c> class organised into <c>#region</c> blocks by functional area
/// (keyboard, cursor, window messaging, shell execute, window placement, native streams,
/// shell file operations, icon extraction). Each region is self-contained and can be split
/// into its own partial-class file if this grows further.
/// </remarks>
internal static partial class Win32Methods
{
    #region Keyboard & input (user32.dll)

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

    #endregion

    #region Cursor position (user32.dll)

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

    #endregion

    #region Window messages & inter-process communication (user32.dll)

    /// <summary>
    /// Defines the constant for the `WM_SETICON` window message.
    /// This message is sent to a window to associate a new large or small icon with the window.
    /// </summary>
    internal const uint WM_SETICON = 0x0080;

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

    #endregion

    #region Shell execute — open / properties (shell32.dll)

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
#pragma warning disable SYSLIB1054
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO pExecInfo);
#pragma warning restore SYSLIB1054

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

    #endregion

    #region Window placement (user32.dll)

#pragma warning disable SYSLIB1054
    /// <summary>
    /// Retrieves the show state and the restored, minimized, and maximized positions of the specified window.
    /// This is a managed declaration of the Win32 <c>GetWindowPlacement</c> function from <c>user32.dll</c>.
    /// </summary>
    /// <param name="hWnd">A handle to the window.</param>
    /// <param name="lpwndpl">A <see cref="WINDOWPLACEMENT"/> structure that receives the show state and position information.</param>
    /// <returns>
    /// <see langword="true"/> if the function succeeds; otherwise, <see langword="false"/>.
    /// </returns>
    [DllImport("user32.dll")]
    internal static extern bool GetWindowPlacement(nint hWnd, out WINDOWPLACEMENT lpwndpl);

    /// <summary>
    /// Sets the show state and the restored, minimized, and maximized positions of the specified window.
    /// This is a managed declaration of the Win32 <c>SetWindowPlacement</c> function from <c>user32.dll</c>.
    /// </summary>
    /// <param name="hWnd">A handle to the window.</param>
    /// <param name="lpwndpl">A reference to a <see cref="WINDOWPLACEMENT"/> structure that specifies the new show state and window positions.</param>
    /// <returns>
    /// <see langword="true"/> if the function succeeds; otherwise, <see langword="false"/>.
    /// </returns>
    [DllImport("user32.dll")]
    internal static extern bool SetWindowPlacement(nint hWnd, in WINDOWPLACEMENT lpwndpl);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// Defines a rectangle by the coordinates of its upper-left and lower-right corners.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        /// <summary>Specifies the x-coordinate of the upper-left corner of the rectangle.</summary>
        public int Left;

        /// <summary>Specifies the y-coordinate of the upper-left corner of the rectangle.</summary>
        public int Top;

        /// <summary>Specifies the x-coordinate of the lower-right corner of the rectangle.</summary>
        public int Right;

        /// <summary>Specifies the y-coordinate of the lower-right corner of the rectangle.</summary>
        public int Bottom;
    }

    /// <summary>
    /// Contains information about the placement of a window on the screen.
    /// Used by the <see cref="GetWindowPlacement"/> and <see cref="SetWindowPlacement"/> functions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        /// <summary>
        /// The length of the structure, in bytes. Before calling the placement functions, set this member to <c>Marshal.SizeOf(typeof(WINDOWPLACEMENT))</c>.
        /// </summary>
        public uint length;

        /// <summary>
        /// Specifies flags that control the position of the minimized window and the method by which the window is restored.
        /// </summary>
        public uint flags;

        /// <summary>
        /// The current show state of the window.
        /// </summary>
        public uint showCmd;

        /// <summary>
        /// The coordinates of the window's upper-left corner when the window is minimized.
        /// </summary>
        public POINT ptMinPosition;

        /// <summary>
        /// The coordinates of the window's upper-left corner when the window is maximized.
        /// </summary>
        public POINT ptMaxPosition;

        /// <summary>
        /// The window's coordinates when the window is in the restored position.
        /// </summary>
        public RECT rcNormalPosition;
    }

    /// <summary>
    /// Activates the window and displays it as a maximized window.
    /// This is one of the command flags used in the <see cref="WINDOWPLACEMENT.showCmd"/> member.
    /// </summary>
    internal const uint SW_SHOWMAXIMIZED = 3;

    #endregion

    #region Native stream access — bypasses Windows Storage Broker (shcore.dll)

    /// <summary>
    /// The interface ID (IID) for <c>Windows.Storage.Streams.IRandomAccessStream</c>.
    /// Required as the <c>riid</c> argument to <see cref="CreateRandomAccessStreamOnFile"/>.
    /// </summary>
    internal static Guid IID_IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");

    /// <summary>
    /// Creates a native <c>IRandomAccessStream</c> on the specified file, bypassing the Windows
    /// Storage Broker. Unlike <c>StorageFile.GetFileFromPathAsync</c>, this function accesses
    /// the file handle directly and therefore succeeds for hidden and system files.
    /// </summary>
    /// <param name="filePath">The full path to the file to open.</param>
    /// <param name="accessMode">The desired access mode. Pass <c>0</c> for read-only.</param>
    /// <param name="riid">
    /// A reference to the IID of the requested interface.
    /// Pass <see cref="IID_IRandomAccessStream"/>.
    /// </param>
    /// <param name="stream">
    /// On success, receives the raw COM pointer to the opened stream. The caller must call
    /// <see cref="System.Runtime.InteropServices.Marshal.Release"/> on this pointer after use.
    /// </param>
    /// <returns>
    /// An HRESULT. Pass to <see cref="System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(int)"/>
    /// to convert failures into managed exceptions.
    /// </returns>
    [LibraryImport("shcore.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateRandomAccessStreamOnFile(
        string filePath, uint accessMode, ref Guid riid, out IntPtr stream);

    #endregion

    #region Shell file operations — copy / move / delete to Recycle Bin (shell32.dll)

    /// <summary>
    /// Contains information used by <see cref="SHFileOperation"/> to perform file system
    /// operations such as copy, move, delete, and rename.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        /// <summary>Handle to the parent window for any dialogs. Set to <see cref="IntPtr.Zero"/> for no parent.</summary>
        public IntPtr hwnd;

        /// <summary>The operation to perform. Use a <c>FO_*</c> constant such as <see cref="FO_DELETE"/>.</summary>
        public uint wFunc;

        /// <summary>
        /// The source path. Must be double-null terminated (<c>path + '\0' + '\0'</c>)
        /// even for a single file.
        /// </summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;

        /// <summary>The destination path. Not used for delete operations; leave as <see langword="null"/>.</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;

        /// <summary>Behaviour flags. Combine <c>FOF_*</c> constants such as <see cref="FOF_ALLOWUNDO"/>.</summary>
        public ushort fFlags;

        /// <summary>Set to <see langword="true"/> by the shell if any operations were aborted by the user.</summary>
        public bool fAnyOperationsAborted;

        /// <summary>Handle to a name-mapping object. Typically <see cref="IntPtr.Zero"/>.</summary>
        public IntPtr hNameMappings;

        /// <summary>Title for the progress dialog. Only used when <c>FOF_SIMPLEPROGRESS</c> is set.</summary>
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    /// <summary>Operation code for <see cref="SHFILEOPSTRUCT.wFunc"/>: delete the specified files.</summary>
    internal const uint FO_DELETE = 0x0003;

    /// <summary>Flag for <see cref="SHFILEOPSTRUCT.fFlags"/>: allows the operation to be undone, sending the file to the Recycle Bin.</summary>
    internal const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>Flag for <see cref="SHFILEOPSTRUCT.fFlags"/>: suppresses all confirmation dialogs.</summary>
    internal const ushort FOF_NOCONFIRMATION = 0x0010;

    /// <summary>Flag for <see cref="SHFILEOPSTRUCT.fFlags"/>: suppresses the progress dialog box.</summary>
    internal const ushort FOF_SILENT = 0x0004;

    /// <summary>
    /// Copies, moves, renames, or deletes a file system object. Used here to send files
    /// to the Recycle Bin for hidden and system files that the Storage Broker rejects.
    /// </summary>
    /// <param name="op">
    /// A reference to a <see cref="SHFILEOPSTRUCT"/> that describes the operation to perform.
    /// </param>
    /// <returns>Zero on success; a non-zero Shell error code on failure.</returns>
    /// <remarks>
    /// This function uses <c>[DllImport]</c> instead of <c>[LibraryImport]</c> because its
    /// struct parameter (<see cref="SHFILEOPSTRUCT"/>) is non-blittable (contains strings) and
    /// is passed by reference (<c>ref</c>). This specific scenario is not yet supported by the
    /// <c>[LibraryImport]</c> source generator, making <c>[DllImport]</c> the correct choice here.
    /// </remarks>
#pragma warning disable SYSLIB1054
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
#pragma warning restore SYSLIB1054

    #endregion

    #region Icon extraction — .exe icon to BGRA via GDI (shell32 / user32 / gdi32)

    /// <summary>
    /// Extracts icons from the specified executable, DLL, or icon file.
    /// </summary>
    /// <param name="lpszFile">Path to the file to extract icons from.</param>
    /// <param name="nIconIndex">Zero-based index of the first icon to extract.</param>
    /// <param name="phiconLarge">Receives the handle to the large icon, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="phiconSmall">Receives the handle to the small icon, or <see cref="IntPtr.Zero"/>.</param>
    /// <param name="nIcons">The number of icons to extract.</param>
    /// <returns>The number of icons successfully extracted, or the total icon count when <paramref name="nIconIndex"/> is -1.</returns>
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    /// <summary>Destroys an icon and frees any memory the icon occupied.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>Retrieves the colour and mask bitmaps backing an icon.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    /// <summary>
    /// Contains information about an icon. The <c>fIcon</c> member is declared as <see cref="int"/>
    /// (a Win32 <c>BOOL</c>) to keep the struct blittable for the <c>LibraryImport</c> source generator.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    /// <summary>Retrieves information for the specified graphics object; used here to read a <see cref="BITMAP"/>.</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    internal static partial int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    /// <summary>Describes the dimensions and colour format of a GDI bitmap.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    /// <summary>Creates a memory device context compatible with the specified device.</summary>
    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>Deletes the specified device context.</summary>
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr hdc);

    /// <summary>Deletes a GDI object (pen, brush, bitmap, etc.) and frees its resources.</summary>
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Copies the bits of a bitmap into a buffer in the device-independent format described by
    /// <paramref name="lpbi"/>. Used to read 32-bpp top-down BGRA pixels from an icon's colour bitmap.
    /// </summary>
    [LibraryImport("gdi32.dll")]
    internal static partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    /// <summary>Bitmap header passed to <see cref="GetDIBits"/>. For 32-bpp BI_RGB no colour table follows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>Uncompressed RGB format for <see cref="BITMAPINFOHEADER.biCompression"/>.</summary>
    internal const uint BI_RGB = 0;

    /// <summary>Colour table contains literal RGB values; the <c>uUsage</c> value for <see cref="GetDIBits"/>.</summary>
    internal const uint DIB_RGB_COLORS = 0;

    #endregion
}
