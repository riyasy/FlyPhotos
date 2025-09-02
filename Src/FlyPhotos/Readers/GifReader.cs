using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
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

    public static async Task<(bool, PreviewDisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            var metaData = new ImageMetadata(canvasBitmap.SizeInPixels.Width, canvasBitmap.SizeInPixels.Height);
            return (true, new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, PreviewDisplayItem.Empty());
        }
    }


    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            // --- Use BitmapDecoder to check frame count ---
            // We must open the file as a stream for the decoder to read.
            var storageFile = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var firstFrame = await CanvasBitmap.LoadAsync(ctrl, stream);

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);

            if (decoder.FrameCount > 1) // Animated GIF
            {
                stream.Seek(0);
                var bytes = new byte[stream.Size];
                await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
                return (true, new AnimatedHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }
            else
            {
                return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process GIF file at {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}