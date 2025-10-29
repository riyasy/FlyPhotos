// Assuming you have 'using Microsoft.Graphics.Canvas;' and other relevant usings at the top.
// Also assuming the HeifDecoder class we created is available in this project.

using System;
using FlyPhotos.Data;
using FlyPhotos.NativeWrappers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;

internal static class NativeHeifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets the embedded thumbnail using the high-performance native HeifDecoder.
    /// </summary>
    public static (bool, PreviewDisplayItem) GetEmbedded(CanvasControl ctrl, string inputPath)
    {
        try
        {
            // 1. Call our new native decoder to get the thumbnail's pixel data and dimensions.
            var heifImage = NativeHeifWrapper.DecodeThumbnail(inputPath);

            // 2. Check if a valid thumbnail was returned.
            if (heifImage == null || heifImage.Pixels == null || heifImage.Pixels.Length == 0)
            {
                // This is the expected outcome if a file has no thumbnail.
                return (false, PreviewDisplayItem.Empty());
            }

            // 3. Create the Win2D bitmap directly from the raw BGRA byte array.
            //    This is extremely fast as no further decoding is needed.
            var canvasBitmap = CanvasBitmap.CreateFromBytes(
                ctrl, // The resource creator
                heifImage.Pixels,
                heifImage.Width,
                heifImage.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized // This MUST match our C++ output
            );

            // 4. Create the metadata using the primary image dimensions, which our native code provides.
            var metaData = new ImageMetadata(heifImage.PrimaryImageWidth, heifImage.PrimaryImageHeight);

            // 5. Return the complete display item.
            return (true, new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to decode preview from: {inputPath} using native decoder.");
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Gets the high-quality primary image using the high-performance native HeifDecoder.
    /// </summary>
    public static (bool, HqDisplayItem) GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            // 1. Call our new native decoder to get the primary image's pixel data.
            var heifImage = NativeHeifWrapper.DecodePrimaryImage(inputPath);

            // 2. Check if a valid image was returned.
            if (heifImage == null || heifImage.Pixels == null || heifImage.Pixels.Length == 0)
            {
                Logger.Warn($"No primary image could be decoded from HEIF file: {inputPath}");
                return (false, HqDisplayItem.Empty());
            }

            // 3. Create the Win2D bitmap directly from the raw BGRA byte array.
            var canvasBitmap = CanvasBitmap.CreateFromBytes(
                ctrl,
                heifImage.Pixels,
                heifImage.Width,
                heifImage.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized
            );

            // 4. Return the complete display item.
            return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to decode HQ image from: {inputPath} using native decoder.");
            return (false, HqDisplayItem.Empty());
        }
    }
}