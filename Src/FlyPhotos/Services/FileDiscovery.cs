#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using NLog;

namespace FlyPhotos.Services;

internal static class FileDiscovery
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static IReadOnlyList<string> DiscoverFiles(string selectedFilePath, bool flyLaunchedExternally)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = ListFiles(selectedFilePath, flyLaunchedExternally);
        sw.Stop();
        Logger.Trace($"Discovered {files.Count} files in {sw.ElapsedMilliseconds} ms");
        return files;
    }

    /// <summary>Whether <paramref name="path"/> has an extension Fly can decode.</summary>
    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && CodecDiscovery.SupportedExtensions.Contains(extension);
    }

    public static int FindSelectedFileIndex(string selectedFilePath, IReadOnlyList<string> files)
    {
        var targetName = Path.GetFileName(selectedFilePath.AsSpan());
        for (var i = 0; i < files.Count; i++)
        {
            var currentName = Path.GetFileName(files[i].AsSpan());
            if (targetName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    private static IReadOnlyList<string> ListFiles(string selectedFilePath, bool flyLaunchedExternally)
    {

        IReadOnlyList<string> files = Array.Empty<string>();

        // 1. If fly launched externally, Attempt to get files from the active Explorer window.
        // Most probably Fly would have been launched from an explorer window
        if (flyLaunchedExternally)
            files = FindAllFilesFromExplorerWindowNative();

        // 2. If explorer gives no files, fall back to reading from the directory.
        if (files.Count == 0)
        {
            // Path.GetDirectoryName can return null if the path is a root directory.
            string? directory = Path.GetDirectoryName(selectedFilePath);
            if (directory != null)
            {
                files = FindAllFilesFromDirectory(directory);
            }
        }

        // 3. Filter for supported extensions, always including the selected file, while preserving order.
        var filteredFiles = new List<string>(files.Count);

        foreach (var file in files)
        {
            // Check if the file meets either of the criteria for inclusion.
            if (IsSupported(file) ||
                (string.Equals(file, selectedFilePath, StringComparison.OrdinalIgnoreCase)))
                filteredFiles.Add(file);
        }

        // 4. If, after all fallbacks and filters, the list is empty (which can happen
        //    if the initial 'files' list was empty or didn't contain the selected file),
        //    add the selected file to ensure we have at least one item.
        if (filteredFiles.Count == 0)
            filteredFiles.Add(selectedFilePath);

        return filteredFiles;
    }

    private static List<string> FindAllFilesFromExplorerWindowNative()
    {
        var fileList = NativeWrapper.GetFileListFromExplorerWindow(AppConfig.Volatile.LaunchForegroundWindow);
        return fileList;
    }

    private static IReadOnlyList<string> FindAllFilesFromDirectory(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return Array.Empty<string>();

        // Same options as Folder mode: not recursive, hidden and system files included.
        string[] files = Directory.GetFiles(dirPath, "*", FolderListing.FileOptions);

        // Must match the order Explorer shows, because the other discovery path (SVGIO_FLAG_VIEWORDER)
        // returns Explorer's own view order. Sorting ordinally here made the two paths disagree
        // whenever filenames contained numbers — see issue #228.
        Array.Sort(files, NaturalComparer.Instance);

        return files;
    }
}