#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
            // Get the file extension once.
            var extension = Path.GetExtension(file);
            // Check if the file meets either of the criteria for inclusion.
            if ((!string.IsNullOrEmpty(extension) && CodecDiscovery.SupportedExtensions.Contains(extension)) ||
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
        var fileList = NativeWrapper.GetFileListFromExplorerWindow();        
        return fileList;
    }

    private static IReadOnlyList<string> FindAllFilesFromDirectory(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return Array.Empty<string>();

        // Standard constructor with object initializer
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0 // Default in .NET is FileAttributes.Hidden | FileAttributes.System
        };

        // Directory.GetFiles is highly optimized in .NET 6+ to work with these options
        string[] files = Directory.GetFiles(dirPath, "*", options);

        // OrdinalIgnoreCase is the fastest way to sort strings in .NET
        // as it uses a simple bitwise comparison after case-folding.
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        return files;
    }
}