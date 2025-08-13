#nullable enable
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


namespace FlyPhotos.Utils;

internal static class Util
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
// TODO REVERSE AOT
    //private static List<CodecInfo>? _codecInfoList;
    public static string ExternalWicReaderPath;

    public static List<string> SupportedExtensions { get; } = [];
    public static List<string> MemoryLeakingExtensions { get; } = [];

// TODO REVERSE AOT
    static Util()
    {
        var executingDir = GetExecutingDirectoryName();
        ExternalWicReaderPath = Path.Combine(executingDir, "WicImageFileReaderNative.exe");

        //var msu = new ManagedShellUtility();
        //_codecInfoList = msu.GetWicCodecList();
        //foreach (var codecInfo in _codecInfoList) SupportedExtensions.AddRange(codecInfo.fileExtensions);
        SupportedExtensions.Add(".PSD");
        SupportedExtensions.Add(".SVG");
        SupportedExtensions.Add(".JPG");

        //var memoryLeakingCodecs = _codecInfoList.Where(x => x.friendlyName.Contains("Raw Image"));
        //foreach (var leakingCodec in memoryLeakingCodecs) MemoryLeakingExtensions.AddRange(leakingCodec.fileExtensions);
        MemoryLeakingExtensions.AddRange([".ARW", ".CR2"]);
    }

// TODO REVERSE AOT
    public static List<string> FindAllFilesFromExplorerWindowNative()
    {
        //var msu = new ManagedShellUtility();
        //var fileList = msu.GetFileListFromExplorerWindow();
        //Logger.Trace($"{fileList.Count} files listed from Explorer");
        //return fileList;
        return new List<string>();
    }

    public static List<string> FindAllFilesFromDirectory(string? dirPath)
    {
        if (dirPath == null || !Directory.Exists(dirPath))
        {
            return [];
        }
        return Directory.EnumerateFiles(dirPath, "*.*", SearchOption.TopDirectoryOnly).ToList();
    }

    public static string GetFileNameFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var sb = new StringBuilder();
        var first = true;
        foreach (var a in args)
            if (first)
            {
                first = false;
            }
            else
            {
                sb.Append(a);
                sb.Append(" ");
            }

        var fileName = sb.ToString().Trim();
        return fileName;
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

// TODO REVERSE AOT
    public static string GetExtensionsDisplayString()
    {
        return "";
        //var sb = new StringBuilder();
        //if (_codecInfoList == null)
        //{
        //    var msu = new ManagedShellUtility();
        //    _codecInfoList = msu.GetWicCodecList();
        //}

        //foreach (var codec in _codecInfoList)
        //{
        //    sb.Append(Environment.NewLine + codec.friendlyName + Environment.NewLine);
        //    sb.Append("    " + string.Join(", ", codec.fileExtensions) + Environment.NewLine);
        //}

        //return sb.ToString();
    }

    private static readonly Random Random = new();

    public static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[Random.Next(s.Length)]).ToArray());
    }

    public static string GetExecutingDirectoryName()
    {
        return Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
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

}