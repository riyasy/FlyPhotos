using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FlyPhotos.Readers;

/// <summary>
/// Reads and decodes GIF files, handling frame composition and disposal logic.
/// NOTE: The consumer of the returned 'DisplayItem' (and the List of CanvasBitmaps within it)
/// is responsible for disposing those resources to prevent VRAM leaks.
/// </summary>
internal class GifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Constants for GIF frame delay calculation
    private const int GifDelayMultiplier = 10; // GIF delay is in 1/100s, this converts to ms.
    private const int MinimumGifDelayMs = 20;  // Treat delays under 20ms as invalid/too fast.
    private const int DefaultGifDelayMs = 100; // Use 100ms for invalid or zero-delay frames.

    public static async Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, inputPath);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }


    public static async Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            // --- Use BitmapDecoder to check frame count ---
            // We must open the file as a stream for the decoder to read.
            var storageFile = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);

            // --- Decide loading strategy based on frame count ---
            if (decoder.FrameCount > 1)
            {
                // More than one frame: It's an animated GIF.
                // Load the raw bytes using your helper method.
                //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] GIF has {decoder.FrameCount} frames. Loading as byte array.");
                return await LoadGifAsFile(inputPath);
            }
            else
            {
                // Single frame: Load as a static image (CanvasBitmap).
                //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Image has 1 frame. Loading as CanvasBitmap.");
                var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, inputPath);
                return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
            }
        }
        catch (Exception ex)
        {
            // This will catch errors from GetFileFromPathAsync, BitmapDecoder, or CanvasBitmap.LoadAsync
            // Logger.Error(ex, "Failed to process image file at {0}", inputPath);
            //Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ERROR in GetHq: {ex.Message}");
            return (false, DisplayItem.Empty());
        }
    }

    private static async Task<(bool, DisplayItem)> LoadGifAsFile(string inputPath)
    {
        // Basic validation to prevent unnecessary exceptions.
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
        {
            Logger.Warn("Input path is null, empty, or file does not exist: {0}", inputPath);
            return (false, null); 
        }

        try
        {
            // File.ReadAllBytesAsync is the most efficient way to asynchronously 
            // read an entire file into a byte array. It avoids blocking threads.
            byte[] fileBytes = await File.ReadAllBytesAsync(inputPath);
            return (true, new DisplayItem(fileBytes, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            // Catches potential I/O errors, like permission issues.
            Logger.Error(ex, "Failed to read byte stream from GIF file: {0}", inputPath);
            return (false, null); 
        }
    }
}