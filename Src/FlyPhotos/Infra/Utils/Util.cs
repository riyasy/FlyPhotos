#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NLog;
using Color = Windows.UI.Color;

namespace FlyPhotos.Infra.Utils;

internal static class Util
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The key that types <paramref name="character"/> on the active layout, and whether Shift is
    /// needed to reach it. VkKeyScanEx packs the shift state into the high byte; discarding it means
    /// a chord built from the result never matches what the user actually presses, because on US and
    /// most EU layouts '+' is Shift and the OEM_PLUS key rather than a key of its own.
    /// </summary>
    public static (VirtualKey Key, bool NeedsShift) GetKeyThatProduces(char character)
    {
        IntPtr layout = Win32Methods.GetKeyboardLayout(0);
        short vkScanResult = Win32Methods.VkKeyScanEx((byte)character, layout);
        return ((VirtualKey)(vkScanResult & 0xff), (vkScanResult & 0x100) != 0);
    }

    /// <summary>
    /// The character printed on <paramref name="key"/>'s keycap on the active layout, or an empty
    /// string when the key types none. The inverse of <see cref="GetKeyThatProduces"/>.
    ///
    /// VirtualKey names no key in the OEM range, so ToString on a punctuation key yields its raw
    /// number - a shortcut shown as "Ctrl + 186" rather than "Ctrl + ;". Asking the layout is also
    /// the only correct answer, because which character key 186 types differs per layout.
    /// </summary>
    public static string GetKeyCapText(VirtualKey key)
    {
        // Asks the OS per call and the rows are built once, so switching layouts with the Settings
        // window open leaves stale keycaps until it is reopened.
        IntPtr layout = Win32Methods.GetKeyboardLayout(0);
        uint mapped = Win32Methods.MapVirtualKeyEx((uint)key, Win32Methods.MAPVK_VK_TO_CHAR, layout);

        // The top bit only flags a dead key; the character itself is still in the low word, and a
        // dead key has a keycap like any other. 0 means the key types nothing - a function key.
        var c = (char)(mapped & 0xffff);
        return char.IsControl(c) || c == '\0' ? string.Empty : c.ToString();
    }

    /// <summary>
    /// Null-tolerant, case-insensitive substring test for search boxes. Culture-aware rather than
    /// ordinal because what is being matched is localized text the user can see, and ordinal gets
    /// the Turkish dotted I and the German sharp S wrong.
    /// </summary>
    public static bool ContainsIgnoreCase(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    public static bool IsControlPressed()
    {
        var coreWindow = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return coreWindow.HasFlag(CoreVirtualKeyStates.Down);
    }

    public static bool IsAltPressed()
    {
        var coreWindow = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        return coreWindow.HasFlag(CoreVirtualKeyStates.Down);
    }

    public static bool IsShiftPressed()
    {
        var coreWindow = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return coreWindow.HasFlag(CoreVirtualKeyStates.Down);
    }

    public static CanvasImageBrush CreateCheckeredBrush(ICanvasResourceCreatorWithDpi canvas, int checkerSize)
    {
        // Create a render target for the small 2x2 checker pattern
        using var patternRenderTarget = new CanvasRenderTarget(canvas, checkerSize * 2, checkerSize * 2, canvas.Dpi);

        using (var ds = patternRenderTarget.CreateDrawingSession())
        {
            // The pattern is two white and two grey squares, forming a checkerboard
            var grey = Color.FromArgb(255, 204, 204, 204);
            ds.Clear(grey);
            ds.FillRectangle(0, 0, checkerSize, checkerSize, Colors.White);
            ds.FillRectangle(checkerSize, checkerSize, checkerSize, checkerSize, Colors.White);
        }

        // Create a brush from this pattern that can be tiled
        var checkeredBrush = new CanvasImageBrush(canvas, patternRenderTarget)
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap,
            Interpolation = CanvasImageInterpolation.NearestNeighbor
        };
        return checkeredBrush;
    }

    public static void MoveWindowToMonitor(Window window, ulong monitorId)
    {
        try
        {
            var allMonitors = DisplayArea.FindAll();
            if (allMonitors.Count <= 1) return;
            // NEVER CONVERT TO FOREACH OR LINQ - IT WILL CAUSE A CRASH
            // https://github.com/microsoft/microsoft-ui-xaml/issues/6454
            DisplayArea? targetMonitor = null;
            for (var index = 0; index < allMonitors.Count; index++)
            {
                var m = allMonitors[index];
                if (m.DisplayId.Value != monitorId) continue;
                targetMonitor = m;
                break;
            }
            if (targetMonitor == null) return;
            var newPosition = new PointInt32(targetMonitor.WorkArea.X, targetMonitor.WorkArea.Y);
            // IMPORTANT: We move it first before resizing or maximizing.
            window.AppWindow.Move(newPosition);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public static ulong GetMonitorForWindow(Window window)
    {
        try
        {
            DisplayArea currentDisplayArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest);
            return currentDisplayArea.DisplayId.Value;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
        return 0;
    }

    /// <summary>
    /// Opens the Windows Properties dialog for the specified file.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="showDetailsPane">If true, opens on the Details tab. If false, opens on the General tab.</param>
    public static void ShowFileProperties(string filePath, bool showDetailsPane = false)
    {
        if (!File.Exists(filePath))
            return;

        try
        {
            var info = new Win32Methods.SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(info);
            info.lpVerb = "properties";
            info.lpParameters = showDetailsPane ? "Details" : "";
            info.lpFile = filePath;
            info.nShow = Win32Methods.SW_SHOW;
            info.fMask = Win32Methods.SEE_MASK_INVOKEIDLIST;

            Win32Methods.ShellExecuteEx(ref info);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    /// <summary>
    /// Invokes the OS "print" shell verb for the specified file, sending it to the user's
    /// default printer via the associated application's print handler.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    public static void PrintFile(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        try
        {
            var info = new Win32Methods.SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(info);
            info.lpVerb = "print";
            info.lpFile = filePath;
            info.nShow = Win32Methods.SW_SHOW;
            info.fMask = Win32Methods.SEE_MASK_INVOKEIDLIST;

            if (!Win32Methods.ShellExecuteEx(ref info))
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Error($"PrintFile: ShellExecuteEx failed for '{filePath}' with Win32 error {error}.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    /// <summary>
    /// Builds a <see cref="WriteableBitmap"/> from a premultiplied BGRA8 pixel buffer.
    /// Must be called on the UI thread.
    /// </summary>
    public static WriteableBitmap CreateBitmapFromBgra(int width, int height, byte[] bgraPixels)
    {
        var bmp = new WriteableBitmap(width, height);
        using (var stream = bmp.PixelBuffer.AsStream())
            stream.Write(bgraPixels, 0, bgraPixels.Length);
        bmp.Invalidate();
        return bmp;
    }

    public static void SetWindowIcon(Window window)
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        if (File.Exists(iconPath))
            window.AppWindow.SetIcon(iconPath);
    }
}