#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Infra.Utils;
using NLog;

namespace FlyPhotos.Services;

internal static class FileDiscoveryService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static List<string> DiscoverFiles(string selectedFileName, bool flyLaunchedExternally)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = ListFiles(selectedFileName, flyLaunchedExternally);
        sw.Stop();
        Logger.Trace($"Discovered {files.Count} files in {sw.ElapsedMilliseconds} ms");
        return files;
    }

    private static List<string> ListFiles(string selectedFileName, bool flyLaunchedExternally)
    {
        
        List<string> files = [];

        // 1. If fly launched externally, Attempt to get files from the active Explorer window.
        // Most probably Fly would have been launched from an explorer window
        if (flyLaunchedExternally)
            files = FindAllFilesFromExplorerWindowNative();

        // 2. If explorer gives no files, fall back to reading from the directory.
        if (files.Count == 0)
        {
            // Path.GetDirectoryName can return null if the path is a root directory.
            string? directory = Path.GetDirectoryName(selectedFileName);
            if (directory != null)
            {
                files = FindAllFilesFromDirectory(directory);
            }
        }

        // 3. Filter for supported extensions, always including the selected file, while preserving order.
        var filteredFiles = new List<string>(files.Count);

        foreach (var file in files)
        {
            // Get the file extension once.
            var extension = Path.GetExtension(file);
            // Check if the file meets either of the criteria for inclusion.
            if ((!string.IsNullOrEmpty(extension) && Util.SupportedExtensions.Contains(extension)) ||
                (string.Equals(file, selectedFileName, StringComparison.OrdinalIgnoreCase)))
                filteredFiles.Add(file);
        }

        // 4. If, after all fallbacks and filters, the list is empty (which can happen
        //    if the initial 'files' list was empty or didn't contain the selected file),
        //    add the selected file to ensure we have at least one item.
        if (filteredFiles.Count == 0)
            filteredFiles.Add(selectedFileName);

        return filteredFiles;
    }

    private static List<string> FindAllFilesFromExplorerWindowNative()
    {
        var fileList = NativeWrapper.GetFileListFromExplorerWindow();        
        return fileList;
    }

    private static List<string> FindAllFilesFromDirectory(string? dirPath)
    {
        if (String.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath))
        {
            return [];
        }
        return [.. Directory.EnumerateFiles(dirPath, "*.*", SearchOption.TopDirectoryOnly)];
    }
}