#nullable enable
using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// A WIC decode scaled down to a preview size inside the decoder (the JPEG decoder scales during the IDCT), so a
/// large source never exists at full size in memory.
/// </summary>
internal static class CappedDecode
{
    /// <summary>The longest side of every preview.</summary>
    public const uint PreviewMaxSize = 800;

    /// <summary>Decodes the first frame of any WIC-readable stream as premultiplied BGRA8, EXIF-oriented and in sRGB,
    /// scaled to fit <paramref name="maxDimension"/> on its longest side.</summary>
    /// <returns>The pixels and their size, and the full image's oriented size.</returns>
    public static async Task<(byte[] Pixels, uint Width, uint Height, uint FullWidth, uint FullHeight)> DecodeAsync(
        IRandomAccessStream stream, uint maxDimension = PreviewMaxSize)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint fullWidth = decoder.OrientedPixelWidth, fullHeight = decoder.OrientedPixelHeight;
        var (width, height) = Fit(fullWidth, fullHeight, maxDimension);

        var frame = await decoder.GetFrameAsync(0);
        // Scaled sizes are in the oriented (post-EXIF-rotation) space.
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return (data.DetachPixelData(), width, height, fullWidth, fullHeight);
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
