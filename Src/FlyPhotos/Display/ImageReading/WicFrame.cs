#nullable enable
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Turns a WIC frame into a bitmap for the canvas: premultiplied BGRA8, EXIF-oriented and in sRGB, decoded to a
/// <see cref="SoftwareBitmap"/> (no managed copy of the pixels) and uploaded with <see cref="ToCanvasBitmap"/>.
/// </summary>
internal static class WicFrame
{
    /// <summary>The longest side of every preview.</summary>
    public const uint PreviewMaxSize = 800;

    private static readonly BitmapTransform Identity = new();

    /// <summary>Decodes the frame at full size. The caller disposes the result.</summary>
    public static IAsyncOperation<SoftwareBitmap> DecodeAsync(BitmapFrame frame) => Decode(frame, Identity);

    /// <summary>Decodes the first frame of any WIC-readable stream scaled to fit <paramref name="maxDimension"/> on
    /// its longest side, inside the decoder (the JPEG decoder scales during the IDCT), so a large source never exists
    /// at full size in memory.</summary>
    /// <returns>The decoded preview (the caller disposes it), and the full image's oriented size.</returns>
    public static async Task<(SoftwareBitmap Bitmap, uint FullWidth, uint FullHeight)> DecodeCappedAsync(
        IRandomAccessStream stream, uint maxDimension = PreviewMaxSize)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint fullWidth = decoder.OrientedPixelWidth, fullHeight = decoder.OrientedPixelHeight;
        // Scaled sizes are in the stored (pre-EXIF-rotation) space; oriented ones squash a quarter-turned image.
        var (width, height) = Fit(decoder.PixelWidth, decoder.PixelHeight, maxDimension);

        var frame = await decoder.GetFrameAsync(0);
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        return (await Decode(frame, transform), fullWidth, fullHeight);
    }

    // A SoftwareBitmap, not GetPixelDataAsync: that hands back a managed LOH array per decode.
    private static IAsyncOperation<SoftwareBitmap> Decode(BitmapFrame frame, BitmapTransform transform) =>
        frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

    /// <summary>
    /// Uploads a decoded Bgra8 premultiplied frame to Win2D at 96 DPI, so its size in DIPs is its pixel size.
    /// CreateFromSoftwareBitmap would otherwise take the file's DPI (72, 300, ...), which changes the layout.
    /// </summary>
    public static CanvasBitmap ToCanvasBitmap(this SoftwareBitmap softwareBitmap, ICanvasResourceCreator resourceCreator)
    {
        softwareBitmap.DpiX = 96;
        softwareBitmap.DpiY = 96;
        return CanvasBitmap.CreateFromSoftwareBitmap(resourceCreator, softwareBitmap);
    }

    /// <summary>
    /// An embedded thumbnail too large to keep as a preview: over twice the preview size. Some codecs return a
    /// full-size image as the thumbnail (the DNG codec gives a 48 MP ProRAW its whole image).
    /// </summary>
    public static bool IsTooLargeForThumbnail(uint width, uint height) => Math.Max(width, height) > 2 * PreviewMaxSize;

    /// <summary>The size that fits <paramref name="max"/> on the longest side, keeping the aspect ratio; never enlarged.</summary>
    public static (uint Width, uint Height) Fit(uint width, uint height, uint max)
    {
        if (width <= max && height <= max) return (width, height);
        return width >= height
            ? (max, Math.Max(1u, (uint)Math.Round(height * (max / (double)width))))
            : (Math.Max(1u, (uint)Math.Round(width * (max / (double)height))), max);
    }
}
