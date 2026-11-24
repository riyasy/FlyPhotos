#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// A reader specifically for .ICO files to correctly handle their multi-frame nature.
/// A multi-frame icon is presented like a multi-page TIFF, with the frames ordered by decreasing
/// resolution, so the highest-resolution frame is what shows first. Previews stay single-frame.
/// </summary>
internal static class IcoReader
{
    /// <summary>
    /// Gets the highest-resolution image from an ICO file for preview purposes.
    /// </summary>
    public static async Task<PreviewDisplayItem> GetPreview(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var decoder = await BitmapDecoder.CreateAsync(await file.GetStreamAsync());
        var order = await GetFramesLargestFirstAsync(decoder);

        var (pixels, width, height) = await DecodeFrameAsync(decoder, order[0]);
        var metadata = new ImageMetadata(width, height);
        return new PreviewDisplayItem(ToBitmap(ctrl, pixels, width, height), Origin.Disk, metadata);
    }

    /// <summary>
    /// Opens an ICO file for high-quality display. A single-frame icon becomes a plain static item;
    /// a multi-frame one becomes a multi-page item whose pages run from the largest frame downwards.
    /// </summary>
    public static async Task<HqDisplayItem?> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var decoder = await BitmapDecoder.CreateAsync(await file.GetStreamAsync());
        // GIF, WebP or PNG data in an .ico: null, so the header check sends it to its own (animating) reader.
        // Anything else WIC can read still decodes here, as before.
        var codec = decoder.DecoderInformation.CodecId;
        if (codec == BitmapDecoder.GifDecoderId || codec == BitmapDecoder.WebpDecoderId || codec == BitmapDecoder.PngDecoderId)
            return null;
        var order = await GetFramesLargestFirstAsync(decoder);

        // Page 1 is the largest frame; it also sizes the initial view via Photo.GetActualSize().
        var (pixels, width, height) = await DecodeFrameAsync(decoder, order[0]);

        // A multi-frame icon's raw bytes, from which the renderer decodes the other frames on demand: read after
        // the decoder is done with the shared stream, and before the GPU bitmap, so a failed read leaves none.
        var bytes = order.Length > 1 ? await file.GetBytesAsync() : null;
        var bitmap = ToBitmap(ctrl, pixels, width, height);

        return bytes != null
            ? new MultiPageHqDisplayItem(bitmap, Origin.Disk, bytes, order)
            : new StaticHqDisplayItem(bitmap, Origin.Disk);
    }

    /// <summary>
    /// Frame indices sorted by decreasing pixel area. Well-formed icons are already stored largest-first,
    /// so this is usually a no-op; it earns its place on icons built by merging two icon groups, whose
    /// directory runs e.g. 48, 32, 16, 256, 48, 32, 16. OrderByDescending is stable, so equal-area
    /// frames (e.g. the same size at different bit depths) keep their file order.
    /// </summary>
    private static async Task<int[]> GetFramesLargestFirstAsync(BitmapDecoder decoder)
    {
        var areas = new uint[decoder.FrameCount];
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            areas[i] = frame.PixelWidth * frame.PixelHeight;
        }
        return Enumerable.Range(0, areas.Length).OrderByDescending(i => areas[i]).ToArray();
    }

    /// <summary>Decodes one frame to premultiplied BGRA8 pixels, with its pixel dimensions.</summary>
    private static async Task<(byte[] pixels, int width, int height)> DecodeFrameAsync(BitmapDecoder decoder, int frameIndex)
    {
        var frame = await decoder.GetFrameAsync((uint)frameIndex);

        var pixelProvider = await frame.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return (pixelProvider.DetachPixelData(), (int)frame.PixelWidth, (int)frame.PixelHeight);
    }

    private static CanvasBitmap ToBitmap(ICanvasResourceCreatorWithDpi ctrl, byte[] pixels, int width, int height) =>
        CanvasBitmap.CreateFromBytes(ctrl, pixels, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
}
