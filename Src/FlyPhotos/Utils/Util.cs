#nullable enable
using FlyPhotos.Data;
using FlyPhotos.NativeWrappers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NLog;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Color = Windows.UI.Color;

namespace FlyPhotos.Utils;

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

    public static async Task SetButtonIconFromExeAsync(Button button, string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return;

        IntPtr[] iconPtr = new IntPtr[1];
        Win32Methods.ExtractIconEx(exePath, 0, iconPtr, null, 1);
        BitmapImage? bmp = null;

        if (iconPtr[0] != IntPtr.Zero)
        {
            try
            {
                using var icon = Icon.FromHandle(iconPtr[0]);
                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            }
            finally
            {
                Win32Methods.DestroyIcon(iconPtr[0]);
            }
        }
        if (bmp != null)
            button.Content = new Microsoft.UI.Xaml.Controls.Image { Source = bmp, Width = 32, Height = 32 };
        else
            button.Content = new FontIcon { Glyph = "\uED35", FontSize = 32 }; // Default icon
    }
}