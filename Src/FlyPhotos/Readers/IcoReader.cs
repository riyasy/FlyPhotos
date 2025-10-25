#nullable enable
using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;

/// <summary>
/// A reader specifically for .ICO files to correctly handle their multi-frame nature.
/// This reader finds and loads the frame with the highest resolution.
/// </summary>
internal static class IcoReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets the highest-resolution image from an ICO file for preview purposes.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        var (bitmap, width, height) = await LoadLargestFrameAsync(ctrl, inputPath);

        if (bitmap == null)
        {
            return (false, PreviewDisplayItem.Empty());
        }

        var metadata = new ImageMetadata(width, height);
        var previewItem = new PreviewDisplayItem(bitmap, Origin.Disk, metadata);
        return (true, previewItem);
    }

    /// <summary>
    /// Gets the highest-resolution image from an ICO file for high-quality display.
    /// </summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        var (bitmap, _, _) = await LoadLargestFrameAsync(ctrl, inputPath);

        if (bitmap == null)
        {
            return (false, HqDisplayItem.Empty());
        }

        var hqItem = new StaticHqDisplayItem(bitmap, Origin.Disk);
        return (true, hqItem);
    }

    /// <summary>
    /// Core logic to open an ICO file, find the frame with the largest dimensions, and load it into a CanvasBitmap.
    /// </summary>
    /// <returns>A tuple containing the loaded bitmap, its width, and its height. Returns null on failure.</returns>
    private static async Task<(CanvasBitmap? bitmap, int width, int height)> LoadLargestFrameAsync(CanvasControl canvasControl, string filePath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using IRandomAccessStream stream = await file.OpenReadAsync();

            // The BitmapDecoder is essential for inspecting multi-frame images.
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // Find the index of the frame with the most pixels.
            var bestFrameIndex = await FindLargestFrameIndexAsync(decoder);

            // Retrieve the specific frame we identified as the largest.
            var bestFrame = await decoder.GetFrameAsync(bestFrameIndex);

            // Get the raw pixel data from that frame.
            var pixelProvider = await bestFrame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var pixelData = pixelProvider.DetachPixelData();

            // Create the final CanvasBitmap directly from the pixel data of the chosen frame.
            var canvasBitmap = CanvasBitmap.CreateFromBytes(
                canvasControl,
                pixelData,
                (int)bestFrame.PixelWidth,
                (int)bestFrame.PixelHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

            return (canvasBitmap, (int)bestFrame.PixelWidth, (int)bestFrame.PixelHeight);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load ICO file {path}", filePath);
            return (null, 0, 0);
        }
    }

    /// <summary>
    /// Iterates through all frames in a BitmapDecoder to find the one with the largest area (width * height).
    /// </summary>
    /// <param name="decoder">The BitmapDecoder for the image file.</param>
    /// <returns>The index of the largest frame.</returns>
    private static async Task<uint> FindLargestFrameIndexAsync(BitmapDecoder decoder)
    {
        // If there's only one frame, no need to search.
        if (decoder.FrameCount <= 1)
        {
            return 0;
        }

        uint bestFrameIndex = 0;
        uint maxPixelCount = 0;

        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var pixelCount = frame.PixelWidth * frame.PixelHeight;

            if (pixelCount > maxPixelCount)
            {
                maxPixelCount = pixelCount;
                bestFrameIndex = i;
            }
        }

        return bestFrameIndex;
    }
}