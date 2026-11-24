// Assuming you have 'using Microsoft.Graphics.Canvas;' and other relevant usings at the top.
// Also assuming the HeifDecoder class we created is available in this project.

using System;
using System.IO;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class NativeHeifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets the embedded thumbnail using the high-performance native HeifDecoder. Without one, a preview scaled
    /// from a full decode, unless <paramref name="embeddedOnly"/>: then it fails.
    /// </summary>
    public static (bool, PreviewDisplayItem) GetEmbedded(ICanvasResourceCreatorWithDpi ctrl, string inputPath,
        bool embeddedOnly = false)
    {
        try
        {
            // 1. Call our new native decoder to get the thumbnail's pixel data and dimensions.
            using var heifImage = NativeHeifWrapper.DecodeThumbnail(inputPath, embeddedOnly);

            // 2. Check if a valid thumbnail was returned.
            if (heifImage == null)
            {
                // This is the expected outcome if a file has no thumbnail.
                return (false, PreviewDisplayItem.Empty());
            }

            // 3. Create the Win2D bitmap directly from the native pixels.
            var canvasBitmap = heifImage.ToBitmap(ctrl);

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
    public static (bool, HqDisplayItem) GetHq(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        try
        {
            // 1. Call our new native decoder to get the primary image's pixel data.
            using var heifImage = NativeHeifWrapper.DecodePrimaryImage(inputPath, out bool isAnimated);

            // 2. Check if a valid image was returned.
            if (heifImage == null)
            {
                Logger.Warn($"No primary image could be decoded from HEIF file: {inputPath}");
                return (false, HqDisplayItem.Empty());
            }

            // 3. An image sequence (e.g. an animated AVIF saved as .heic): only then are the file's bytes read, for
            //    AvifAnimator, which plays any libheif sequence. Read before the bitmap exists, so a failed read
            //    leaves no GPU bitmap behind.
            var animationBytes = isAnimated ? File.ReadAllBytes(inputPath) : null;

            // 4. Create the Win2D bitmap directly from the native pixels.
            var canvasBitmap = heifImage.ToBitmap(ctrl);

            // 5. Return the complete display item.
            if (animationBytes != null)
                return (true, new AnimatedHqDisplayItem(canvasBitmap, Origin.Disk, animationBytes, ImageFormat.Avif));
            return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to decode HQ image from: {inputPath} using native decoder.");
            return (false, HqDisplayItem.Empty());
        }
    }
}