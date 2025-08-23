#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace FlyPhotos.Utils;

internal class FileDiscoveryService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static List<string> DiscoverFiles(string selectedFileName)
    {
        return ListFiles(selectedFileName);
    }

    /// <summary>
    /// Asynchronously discovers all relevant image files related to the selected file.
    /// This method orchestrates the file discovery on a background STA thread.
    /// </summary>
    /// <param name="selectedFileName">The path to the file that was initially opened.</param>
    /// <returns>A task that represents the asynchronous operation, yielding a list of file paths.</returns>
    public static Task<List<string>> DiscoverFilesAsync(string selectedFileName)
    {
        return DiscoverFilesOnStaThreadAsync(selectedFileName);
    }

    /// <summary>
    /// Executes the entire file discovery process on a new thread set to Single-Threaded Apartment (STA) state.
    /// STA is required for shell COM interactions like Util.FindAllFilesFromExplorerWindowNative().
    /// All subsequent logic (fallback to directory, filtering) is also done on this thread
    /// to keep the entire discovery operation as a single unit of work off the calling thread.
    /// </summary>
    private static Task<List<string>> DiscoverFilesOnStaThreadAsync(string selectedFileName)
    {
        // Use TaskCompletionSource to bridge the gap between the dedicated thread and the async/await world.
        var tcs = new TaskCompletionSource<List<string>>();

        var thread = new Thread(() =>
        {
            try
            {
                var files = ListFiles(selectedFileName);
                tcs.SetResult(files);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static List<string> ListFiles(string selectedFileName)
    {
        // 1. Attempt to get files from the active Explorer window.
        List<string> files = [];
        if (!App.Debug)
        {
            files = Util.FindAllFilesFromExplorerWindowNative();
        }

        // 2. If explorer gives no files, fall back to reading from the directory.
        if (files.Count == 0)
        {
            // Path.GetDirectoryName can return null if the path is a root directory.
            string? directory = Path.GetDirectoryName(selectedFileName);
            if (directory != null)
            {
                files = Util.FindAllFilesFromDirectory(directory);
            }
        }

        // 3. Filter for supported extensions, always including the selected file.
        var supportedExtensions = Util.SupportedExtensions;
        files = files.Where(s =>
            supportedExtensions.Contains(Path.GetExtension(s).ToUpperInvariant()) || s == selectedFileName).ToList();

        // 4. If, after all fallbacks and filters, the list is empty, add the selected file to ensure we have at least one item.
        if (files.Count == 0)
        {
            files.Add(selectedFileName);
        }

        return files;
    }
}