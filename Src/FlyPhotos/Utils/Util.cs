﻿#nullable enable
using CliWrapper;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace FlyPhotos.Utils;

internal static class Util
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static List<CodecInfo>? _codecInfoList;
    public static string ExternalWicReaderPath;

    public static List<string> SupportedExtensions { get; } = [];
    public static List<string> MemoryLeakingExtensions { get; } = [];

    static Util()
    {
        var executingDir = GetExecutingDirectoryName();
        ExternalWicReaderPath = Path.Combine(executingDir, "WicImageFileReaderNative.exe");

        var msu = new ManagedShellUtility();
        _codecInfoList = msu.GetWicCodecList();
        foreach (var codecInfo in _codecInfoList) SupportedExtensions.AddRange(codecInfo.fileExtensions);
        SupportedExtensions.Add(".PSD");
        SupportedExtensions.Add(".SVG");

        var memoryLeakingCodecs = _codecInfoList.Where(x => x.friendlyName.Contains("Raw Image"));
        foreach (var leakingCodec in memoryLeakingCodecs) MemoryLeakingExtensions.AddRange(leakingCodec.fileExtensions);
    }

    public static List<string> FindAllFilesFromExplorerWindowNative()
    {
        var msu = new ManagedShellUtility();
        var fileList = msu.GetFileListFromExplorerWindow();
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

    public static string GetExtensionsDisplayString()
    {
        var sb = new StringBuilder();
        if (_codecInfoList == null)
        {
            var msu = new ManagedShellUtility();
            _codecInfoList = msu.GetWicCodecList();
        }

        foreach (var codec in _codecInfoList)
        {
            sb.Append(Environment.NewLine + codec.friendlyName + Environment.NewLine);
            sb.Append("    " + string.Join(", ", codec.fileExtensions) + Environment.NewLine);
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

    public static string GetExecutingDirectoryName()
    {
        var uriString = Assembly.GetEntryAssembly()?.GetName().CodeBase;
        if (uriString == null) return string.Empty;
        var location = new Uri(uriString);
        var directoryInfo = new FileInfo(location.AbsolutePath).Directory;
        if (directoryInfo == null) return string.Empty;
        return Uri.UnescapeDataString(directoryInfo.FullName);
    }

    public static void ChangeCursor(this UIElement uiElement, InputCursor cursor)
    {
        Type type = typeof(UIElement);
        type.InvokeMember("ProtectedCursor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.SetProperty | BindingFlags.Instance, null, uiElement, new object[] { cursor });
    }

    //public static void OpenUrl(string url)
    //{
    //    try
    //    {
    //        Process.Start(url);
    //    }
    //    catch
    //    {
    //        // hack because of this: https://github.com/dotnet/corefx/issues/10361
    //        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    //        {
    //            url = url.Replace("&", "^&");
    //            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    //        }
    //        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    //        {
    //            Process.Start("xdg-open", url);
    //        }
    //        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    //        {
    //            Process.Start("open", url);
    //        }
    //        else
    //        {
    //            throw;
    //        }
    //    }
    //}
}