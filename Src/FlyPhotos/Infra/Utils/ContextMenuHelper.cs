using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Xaml;
using NLog;
using WinRT.Interop;

namespace FlyPhotos.Infra.Utils;

/// <summary>
/// Helper class responsible for displaying the Windows Explorer context menu for a
/// specified file. The helper can either invoke an in-process native wrapper or
/// send an IPC request to an external helper executable that hosts the Shell
/// context menu logic.
/// </summary>
/// <remarks>
/// Usage:
/// - Call <see cref="ShowContextMenu(Window, string)"/> to show the menu for a given file
///   at the current mouse position.
/// - If <see cref="AppSettings.UseExternalExeForContextMenu"/> is true, the
///   helper will attempt to locate or launch the external helper process and send
///   the request via WM_COPYDATA. Otherwise it will call the native wrapper directly.
/// </remarks>
internal class ContextMenuHelper
{
    // The window class name used by the external context menu helper process.
    public const string ContextMenuHelperWindowClassName = "context_menu_helper_fly";
    // Specifies the process name for the context menu helper executable used by the application.
    public const string ContextMenuHelperProcessName = "FlyContextMenuHelper.exe";

    /// <summary>
    /// NLog logger instance for logging messages, warnings, and errors.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Displays the Windows Explorer context menu for a specified file at the current cursor position.
    /// </summary>
    /// <param name="ownerWindow">The owner <see cref="Microsoft.UI.Xaml.Window"/> whose HWND will be used
    /// as the owner of the context menu. This is used for proper activation and focus handling.</param>
    /// <param name="filePath">The full path to the file for which to show the context menu. If the path is null or empty,
    /// the call will be a no-op and an error will be logged.</param>
    /// <remarks>
    /// This method determines the current cursor position and then either calls into the
    /// in-process native wrapper (<see cref="NativeWrapper.ShowContextMenu"/>) or sends
    /// an IPC message to an external helper process depending on application settings.
    /// All exceptions are caught and logged to avoid crashing the UI.
    /// </remarks>
    public static void ShowContextMenu(Window ownerWindow, string filePath)
    {
        try
        {
            Win32Methods.GetCursorPos(out Win32Methods.POINT mousePosScreen);
            IntPtr hWnd = WindowNative.GetWindowHandle(ownerWindow);

            if (AppConfig.Settings.UseExternalExeForContextMenu)
            {
                ShowContextMenuUsingExternalProcess(hWnd, filePath, mousePosScreen);
            }
            else
            {
                NativeWrapper.ShowContextMenu(hWnd, filePath, mousePosScreen.X, mousePosScreen.Y);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NativeBridge - ShowContextMenu failed with exception");
        }
    }


    /// <summary>
    /// Sends a request to an external helper process to display the Shell context menu for a file.
    /// </summary>
    /// <param name="hwndSender">The HWND of the window sending the request. This HWND is forwarded to the
    /// helper so it can restore activation to the requester after the context menu closes.</param>
    /// <param name="filePath">The full path of the file for which the context menu should be shown.</param>
    /// <param name="mousePosScreen">Screen coordinates of the desired menu location (typically the cursor position).</param>
    /// <remarks>
    /// The method attempts to find a running helper window. If none is found
    /// it will try to launch the helper executable from the application base directory and wait briefly for the
    /// helper window to appear. The IPC payload is formatted as "x|y|<FilePath>" encoded as UTF-8 and sent via
    /// WM_COPYDATA. All unmanaged memory allocations are released in a finally block.
    /// </remarks>
    public static void ShowContextMenuUsingExternalProcess(IntPtr hwndSender, string filePath, Win32Methods.POINT mousePosScreen)
    {
        try
        {
            var target = Win32Methods.FindWindow(ContextMenuHelperWindowClassName, null);
            if (target == IntPtr.Zero)
            {
                // Try to start the helper executable from a specific path
                try
                {
                    // WinUI 3 apps(Packaged) run with Full Trust. You do not need to copy executables
                    // to AppData to run them.You can run them directly from the installation
                    // directory, just like the Unpackaged version.
                    var exePath = Path.Combine(AppContext.BaseDirectory, ContextMenuHelperProcessName);

                    // var exePath = ResolveHelperExePath(); // uncomment this line if any issue arises for packaged app type

                    if (File.Exists(exePath))
                    {
                        var psi = new ProcessStartInfo(exePath)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = false
                        };
                        var proc = Process.Start(psi);
                    }

                    // Wait briefly for the window to appear
                    var sw = Stopwatch.StartNew();
                    const int timeoutMs = 2000;
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        target = Win32Methods.FindWindow(ContextMenuHelperWindowClassName, null);
                        if (target != IntPtr.Zero) break;
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "ShowContextMenuUsingExternalProcess failed with exception");
                }
            }

            if (target == IntPtr.Zero)
            {
                Logger.Error("ShowContextMenuUsingExternalProcess - ContextMenu helper window not found");
                return;
            }

            var payload = $"{mousePosScreen.X}|{mousePosScreen.Y}|{filePath}";
            var bytes = Encoding.UTF8.GetBytes(payload + "\0");
            var pData = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pData, bytes.Length);
                var cds = new Win32Methods.COPYDATASTRUCT
                {
                    dwData = IntPtr.Zero,
                    cbData = bytes.Length,
                    lpData = pData
                };
                Win32Methods.SendMessage(target, Win32Methods.WM_COPYDATA, hwndSender, ref cds);
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ShowContextMenuUsingExternalProcess failed with exception");
        }
    }
}