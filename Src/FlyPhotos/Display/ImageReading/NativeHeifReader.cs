#nullable enable
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

internal static class NativeHeifReader
{
    /// <summary>
    /// Gets the embedded thumbnail using the high-performance native HeifDecoder. Without one, a preview scaled
    /// from a full decode, unless <paramref name="embeddedOnly"/>: then null.
    /// </summary>
    public static PreviewDisplayItem? GetEmbedded(ICanvasResourceCreatorWithDpi ctrl, string inputPath,
        bool embeddedOnly = false)
    {
        // 1. Call our new native decoder to get the thumbnail's pixel data and dimensions.
        using var heifImage = NativeHeifWrapper.DecodeThumbnail(inputPath, embeddedOnly);

        // 2. No thumbnail is the expected outcome for some files.
        if (heifImage == null) return null;

        // 3. Create the Win2D bitmap directly from the native pixels.
        var canvasBitmap = heifImage.ToBitmap(ctrl);

        // 4. Create the metadata using the primary image dimensions, which our native code provides.
        var metaData = new ImageMetadata(heifImage.PrimaryImageWidth, heifImage.PrimaryImageHeight);

        // 5. Return the complete display item.
        return new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData);
    }

    /// <summary>
    /// Gets the high-quality primary image using the high-performance native HeifDecoder.
    /// </summary>
    public static async Task<HqDisplayItem> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        // On a share, decode from the bytes as the AVIF reader does, so an animation reuses them (one read).
        if (await file.GetBytesIfNetworkAsync() != null) return await NativeAvifReader.GetHq(ctrl, file);

        // 1. Locally libheif reads the path itself, with our handle closed.
        using var heifImage = NativeHeifWrapper.DecodePrimaryImage(file.ReleaseForPathReader(), out bool isAnimated)
                              ?? throw new InvalidDataException("No primary image could be decoded");

        // 2. An image sequence (e.g. an animated AVIF saved as .heic): AvifAnimator plays it from the file's
        //    bytes. Read before the bitmap exists, so a failed read leaves no GPU bitmap behind.
        var animationBytes = isAnimated ? await file.GetBytesAsync() : null;

        // 3. Create the Win2D bitmap directly from the native pixels.
        var canvasBitmap = heifImage.ToBitmap(ctrl);

        // 4. Return the complete display item.
        if (animationBytes != null)
            return new AnimatedHqDisplayItem(canvasBitmap, Origin.Disk, animationBytes, ImageFormat.Avif);
        return new StaticHqDisplayItem(canvasBitmap, Origin.Disk);
    }
}
