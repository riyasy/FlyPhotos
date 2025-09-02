#nullable enable
using FlyPhotos.Data;
using FlyPhotos.FlyNativeLibWrapper;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;


namespace FlyPhotos.Utils;

internal static class Util
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static List<CodecInfo>? _codecInfoList;

    public static List<string> SupportedExtensions { get; } = [];
    public static List<string> MemoryLeakingExtensions { get; } = [];

    static Util()
    {
        _codecInfoList = CliWrapper.GetWicDecoders();
        foreach (var codecInfo in _codecInfoList) SupportedExtensions.AddRange(codecInfo.FileExtensions);
        SupportedExtensions.Add(".PSD");
        SupportedExtensions.Add(".SVG");
        SupportedExtensions.Add(".HEIC");

        var memoryLeakingCodecs = _codecInfoList.Where(x => x.FriendlyName.Contains("Raw Image"));
        foreach (var leakingCodec in memoryLeakingCodecs) MemoryLeakingExtensions.AddRange(leakingCodec.FileExtensions);
    }

    public static List<string> FindAllFilesFromExplorerWindowNative()
    {
        var fileList = CliWrapper.GetFileListFromExplorerWindow();
        Logger.Trace($"{fileList.Count} files listed from Explorer");
        return fileList;
    }

    public static List<string> FindAllFilesFromDirectory(string? dirPath)
    {
        if (dirPath == null || !Directory.Exists(dirPath))
        {
            return [];
        }
        return Directory.EnumerateFiles(dirPath, "*.*", SearchOption.TopDirectoryOnly).ToList();
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

    private static readonly Random Random = new();

    public static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[Random.Next(s.Length)]).ToArray());
    }


    public static void ChangeCursor(this UIElement uiElement, InputCursor cursor)
    {
        Type type = typeof(UIElement);
        type.InvokeMember("ProtectedCursor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.SetProperty | BindingFlags.Instance, null, uiElement,
            [cursor]);
    }

    public static VirtualKey GetKeyThatProduces(char character)
    {
        IntPtr layout = NativeMethods.GetKeyboardLayout(0);
        short vkScanResult = NativeMethods.VkKeyScanExA((byte)character, layout);
        int virtualKeyCode = vkScanResult & 0xff;
        return (VirtualKey)virtualKeyCode;
    }

    public static bool IsControlPressed()
    {
        var coreWindow = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var isControlPressed = coreWindow.HasFlag(CoreVirtualKeyStates.Down);
        return isControlPressed;
    }

    public static CanvasImageBrush CreateCheckeredBrush(ICanvasResourceCreator resourceCreator, int checkerSize)
    {
        // Create a render target for the small 2x2 checker pattern
        using var patternRenderTarget = new CanvasRenderTarget(resourceCreator, checkerSize * 2, checkerSize * 2, 96);

        using (var ds = patternRenderTarget.CreateDrawingSession())
        {
            // The pattern is two white and two grey squares, forming a checkerboard
            var grey = Color.FromArgb(255, 204, 204, 204);
            ds.Clear(grey);
            ds.FillRectangle(0, 0, checkerSize, checkerSize, Colors.White);
            ds.FillRectangle(checkerSize, checkerSize, checkerSize, checkerSize, Colors.White);
        }

        // Create a brush from this pattern that can be tiled
        var checkeredBrush = new CanvasImageBrush(resourceCreator, patternRenderTarget)
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap,
            Interpolation = CanvasImageInterpolation.NearestNeighbor
        };
        return checkeredBrush;
    }

}