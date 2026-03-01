#nullable enable
using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using FlyPhotos.Services;


namespace FlyPhotos.Display.ImageReading;

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

        if (bitmap != null)
        {
            var metadata = new ImageMetadata(width, height);
            var previewItem = new PreviewDisplayItem(bitmap, Origin.Disk, metadata);
            return (true, previewItem);
        }
        return (false, PreviewDisplayItem.Empty());
    }

    /// <summary>
    /// Gets the highest-resolution image from an ICO file for high-quality display.
    /// </summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        var (bitmap, _, _) = await LoadLargestFrameAsync(ctrl, inputPath);

        if (bitmap != null)
        {
            var hqItem = new StaticHqDisplayItem(bitmap, Origin.Disk);
            return (true, hqItem);
        }
        return (false, HqDisplayItem.Empty());
    }

    /// <summary>
    /// Core logic to open an ICO file, find the frame with the largest dimensions, and load it into a CanvasBitmap.
    /// </summary>
    /// <returns>A tuple containing the loaded bitmap, its width, and its height. Returns null on failure.</returns>
    private static async Task<(CanvasBitmap? bitmap, int width, int height)> LoadLargestFrameAsync(CanvasControl canvasControl, string filePath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(filePath);

            // The BitmapDecoder is essential for inspecting multi-frame images.
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // Find the index of the frame with the most pixels.
            var bestFrame = await FindLargestFrameAsync(decoder);

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
    /// Iterates through all frames in a BitmapDecoder to find the one with the largest area.
    /// Returns the frame itself to avoid a redundant GetFrameAsync call by the caller.
    /// </summary>
    private static async Task<BitmapFrame> FindLargestFrameAsync(BitmapDecoder decoder)
    {
        var bestFrame = await decoder.GetFrameAsync(0);
        if (decoder.FrameCount <= 1) return bestFrame;

        uint maxPixelCount = bestFrame.PixelWidth * bestFrame.PixelHeight;

        for (uint i = 1; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var pixelCount = frame.PixelWidth * frame.PixelHeight;

            if (pixelCount > maxPixelCount)
            {
                maxPixelCount = pixelCount;
                bestFrame = frame;
            }
        }
        return bestFrame;
    }
}