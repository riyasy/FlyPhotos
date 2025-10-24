#nullable enable
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using FlyPhotos.NativeWrappers;


namespace FlyPhotos.Utils;

internal static class Util
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static List<CodecInfo>? _codecInfoList;

    public static HashSet<string> SupportedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    static Util()
    {
        _codecInfoList = CliWrapper.GetWicDecoders();
        foreach (var codecInfo in _codecInfoList) 
            SupportedExtensions.UnionWith(codecInfo.FileExtensions);
        SupportedExtensions.Add(".PSD");
        SupportedExtensions.Add(".SVG");
        SupportedExtensions.Add(".HEIC");
        SupportedExtensions.Add(".HEIF");
        SupportedExtensions.Add(".HIF");
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

    public static string GetExtensionsDisplayString()
    {
        var sb = new StringBuilder();
        _codecInfoList ??= CliWrapper.GetWicDecoders();

        foreach (var codec in _codecInfoList)
        {
            sb.Append(Environment.NewLine + codec.FriendlyName + Environment.NewLine);
            sb.Append("    " + string.Join(", ", codec.FileExtensions) + Environment.NewLine);
        }
        return sb.ToString();
    }
    
    public static void ChangeCursor(this UIElement uiElement, InputCursor cursor)
    {
        Type type = typeof(UIElement);
        type.InvokeMember("ProtectedCursor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.SetProperty | BindingFlags.Instance, null, uiElement,
            [cursor]);
    }

    public static VirtualKey GetKeyThatProduces(char character)
    {
        IntPtr layout = NativeWrappers.Win32Methods.GetKeyboardLayout(0);
        short vkScanResult = NativeWrappers.Win32Methods.VkKeyScanEx((byte)character, layout);
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
}