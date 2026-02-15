#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NLog;
using WinRT.Interop;
using Color = Windows.UI.Color;

namespace FlyPhotos.Infra.Utils;

internal static class Util
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly List<CodecInfo>? _codecInfoList;

    public static HashSet<string> SupportedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    static Util()
    {
        _codecInfoList = GetWicCodecs();
        _codecInfoList.AddRange(GetFlyCodecs());
        foreach (var codecInfo in _codecInfoList) 
            SupportedExtensions.UnionWith(codecInfo.FileExtensions);
    }

    public static int FindSelectedFileIndex(string selectedFileName, List<string> files)
    {
        var curIdx = 0;
        for (var i = 0; i < files.Count; i++)
            if (string.Equals(Path.GetFileName(selectedFileName), Path.GetFileName(files[i]),
                    StringComparison.OrdinalIgnoreCase))
            {
                curIdx = i;
                break;
            }

        return curIdx;
    }

    public static IReadOnlyList<CodecInfo> GetAllCodecs()
    {
        return _codecInfoList ?? [];
    }

    private static List<CodecInfo> GetWicCodecs()
    {
        return NativeWrapper.GetWicDecoders() ?? [];
    }

    private static List<CodecInfo> GetFlyCodecs()
    {
        var list = new List<CodecInfo>
        {
            new CodecInfo { FriendlyName = "PSD Decoder", Type = "Fly", FileExtensions = [".PSD"] },
            new CodecInfo { FriendlyName = "SVG Decoder", Type = "Fly", FileExtensions = [".SVG"] },
            new CodecInfo { FriendlyName = "HEIC Decoder", Type = "Fly", FileExtensions = [".HEIC", ".HEIF", ".HIF", ".AVIF"] }
        };
        return list;
    }

    public static VirtualKey GetKeyThatProduces(char character)
    {
        IntPtr layout = Win32Methods.GetKeyboardLayout(0);
        short vkScanResult = Win32Methods.VkKeyScanEx((byte)character, layout);
        int virtualKeyCode = vkScanResult & 0xff;
        return (VirtualKey)virtualKeyCode;
    }

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

    public static CanvasImageBrush CreateCheckeredBrush(CanvasControl canvas, int checkerSize)
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

    public static void ShowFileProperties(string filePath)
    {
        if (!File.Exists(filePath))        
            return;

        try
        {
            var info = new Win32Methods.SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(info);
            info.lpVerb = "properties";
            info.lpParameters = "Details";
            info.lpFile = filePath;
            info.nShow = Win32Methods.SW_SHOW;
            info.fMask = Win32Methods.SEE_MASK_INVOKEIDLIST;
            //info.hwnd = WindowNative.GetWindowHandle(ownerWindow);
            Win32Methods.ShellExecuteEx(ref info);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    /// <summary>
    /// Extracts an icon from an executable file and converts it to an <see cref="ImageSource"/>.
    /// </summary>
    /// <param name="exePath">The path to the executable.</param>
    /// <returns>A task returning the <see cref="ImageSource"/> or null if extraction fails.</returns>
    public static async Task<ImageSource?> ExtractIconFromExe(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;

        byte[]? iconBytes = await Task.Run(() =>
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon == null) return null;

                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ExtractIconFromExe Error Error");
                return null;
            }
        });

        if (iconBytes == null) return null;

        var bmp = new BitmapImage();
        using var ms = new MemoryStream(iconBytes);
        await bmp.SetSourceAsync(ms.AsRandomAccessStream());
        return bmp;
    }

    /// <summary>
    /// Extracts the icon data from a store app list entry.
    /// </summary>
    /// <param name="entry">The app list entry.</param>
    /// <returns>A task returning the byte array of the icon.</returns>
    public static async Task<byte[]> ExtractIconFromAppListEntryAsync(Windows.ApplicationModel.Core.AppListEntry entry)
    {
        try
        {
            var logo = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(50, 50));
            using var stream = await logo.OpenReadAsync();
            await using var input = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ExtractIconFromAppListEntryAsync Error");
            return [];
        }
    }

    public static void SetUnpackagedAppIcon(Window window)
    {
        // 1. Get the AppWindow for the current Window
        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);
    }
}