#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Services;

/// <summary>
/// Writes a photo to the Windows clipboard: a file reference (with hidden/system-file fallback)
/// plus the in-memory bitmap at the current display level for paste-as-image targets. A pure leaf
/// operation — depends only on the photo and its display level, not on navigation, the photo list,
/// or the prefetch cache.
/// </summary>
internal static class PhotoClipboard
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Copies <paramref name="photo"/> to the clipboard. Errors are swallowed and logged — a failed
    /// copy must never crash the caller. No-ops if the source file no longer exists on disk.
    /// </summary>
    public static async Task CopyToClipboardAsync(Photo photo, DisplayLevel level)
    {
        try
        {
            var filePath = photo.FilePath;

            if (!File.Exists(filePath)) return;

            var dataPackage = new DataPackage();

            // 1. FILE REFERENCE (with hidden/system file bypass)
            var sourceFile = await StorageOps.GetStorageFileOrVirtualFile(filePath);
            dataPackage.SetStorageItems((List<IStorageItem>)[sourceFile]);

            // 2. IN-MEMORY BITMAP (for paste-as-image targets)
            var canvasBitmap = level switch
            {
                DisplayLevel.Hq => photo.Hq?.Bitmap,
                DisplayLevel.Preview => photo.Preview?.Bitmap,
                _ => null
            };
            if (canvasBitmap != null)
            {
                var streamReference = await GetCanvasBitmapAsAccessStream(canvasBitmap);
                dataPackage.SetBitmap(streamReference);
            }

            // 3. Set Everything to Clipboard
            Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to copy file to clipboard.");
        }
    }

    private static async Task<RandomAccessStreamReference> GetCanvasBitmapAsAccessStream(CanvasBitmap canvasBitmap)
    {
        // NOTE: memoryStream is intentionally NOT disposed here.
        // RandomAccessStreamReference holds a COM reference to the stream, and the
        // clipboard reads from it lazily at paste-time (e.g. when the user pastes
        // into Word or Paint). Disposing early would corrupt the paste.
        // The WinRT runtime releases it when the clipboard content is replaced.
        var memoryStream = new InMemoryRandomAccessStream();
        await canvasBitmap.SaveAsync(memoryStream, CanvasBitmapFileFormat.Png);
        memoryStream.Seek(0);
        var streamReference = RandomAccessStreamReference.CreateFromStream(memoryStream);
        return streamReference;
    }
}
