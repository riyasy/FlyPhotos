#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using FlyPhotos.Core.Model;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class WicReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly string[] OrientationKey = ["System.Photo.Orientation"];

    public static async Task<(bool, PreviewDisplayItem)> GetEmbedded(CanvasControl ctrl, string inputPath)
    {
        var (bmp, width, height, rotation) = await GetThumbnail(ctrl, inputPath);
        if (bmp == null) return (false, PreviewDisplayItem.Empty());

        var metadata = new ImageMetadata(width, height);
        return (true, new PreviewDisplayItem(bmp, Origin.Disk, metadata, rotation));
    }

    /// <summary>
    /// Decodes and resizes any WIC-supported image to a preview, capped at
    /// <paramref name="maxDimension"/> on the longest side. Uses a single BitmapDecoder
    /// pass — WIC applies the resize natively inside GetPixelDataAsync.
    /// Works for JPEG, PNG, GIF (frame 0), TIFF (frame 0), BMP, and any other
    /// format WIC can auto-detect.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetResized(CanvasControl ctrl, string inputPath,
        uint maxDimension = 800)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);

            // Auto-detect format — no codec ID needed.
            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint originalWidth = decoder.OrientedPixelWidth;
            uint originalHeight = decoder.OrientedPixelHeight;
            var metadata = new ImageMetadata(originalWidth, originalHeight);

            // Maintain aspect ratio, cap longest side at maxDimension.
            uint scaledWidth, scaledHeight;
            if (originalWidth <= maxDimension && originalHeight <= maxDimension)
            {
                scaledWidth = originalWidth;
                scaledHeight = originalHeight;
            }
            else if (originalWidth >= originalHeight)
            {
                scaledWidth = maxDimension;
                scaledHeight = (uint)Math.Round(originalHeight * (maxDimension / (double)originalWidth));
            }
            else
            {
                scaledHeight = maxDimension;
                scaledWidth = (uint)Math.Round(originalWidth * (maxDimension / (double)originalHeight));
            }

            // WIC applies the resize natively inside GetPixelDataAsync —
            // single decode pass, no intermediate buffer or separate library needed.
            var frame0 = await decoder.GetFrameAsync(0);
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var pixelProvider = await frame0.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var pixels = pixelProvider.DetachPixelData();

            var bitmap = CanvasBitmap.CreateFromBytes(
                ctrl, pixels,
                (int)scaledWidth, (int)scaledHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);

            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WicReader.GetResized failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);

            // Nikon NEF codec doesn't respect EXIF orientation — read and apply it manually.
            // For non Nikon codecs, it simply returns 0.
            var rotation = await GetNikonSpecialExifRotation(inputPath, stream);

            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, HqDisplayItem.Empty());
        }
    }

    private static async Task<int> GetNikonSpecialExifRotation(string inputPath, IRandomAccessStream stream, BitmapDecoder? decoder = null)
    {
        var rotation = 0;
        var isNef = Path.GetExtension(inputPath).Equals(".nef", StringComparison.OrdinalIgnoreCase);
        if (!isNef) return rotation;
        if (decoder == null) decoder = await BitmapDecoder.CreateAsync(stream);
        if (IsNikonNefCodec(decoder))
            rotation = await GetRotationFromMetaData(decoder.BitmapProperties);
        stream.Seek(0);
        return rotation;
    }

    private static bool IsNikonNefCodec(BitmapDecoder decoder)
    {
        var name = decoder.DecoderInformation?.FriendlyName ?? string.Empty;
        return name.Contains("Nikon", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("NEF", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(CanvasBitmap? Bitmap, int Width, int Height, int Rotation)> GetThumbnail(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // OrientedPixelWidth/Height account for EXIF rotation automatically —
            // no need to read the property bag and swap manually.
            var originalWidth = (int)decoder.OrientedPixelWidth;
            var originalHeight = (int)decoder.OrientedPixelHeight;
            using var preview = await decoder.GetThumbnailAsync();

            var rotation = await GetNikonSpecialExifRotation(inputPath, stream, decoder);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);

            return (canvasBitmap, originalWidth, originalHeight, rotation);
        }
        catch (Exception)
        {
            return (null, 0, 0, 0);
        }
    }

    private static async Task<int> GetRotationFromMetaData(BitmapPropertiesView bmpProps)
    {
        // OrientationKey is static readonly — avoids allocating a new string[] on every call.
        var result = await bmpProps.GetPropertiesAsync(OrientationKey);

        if (!result.TryGetValue(OrientationKey[0], out var orientation)) return 0;
        return (ushort)orientation.Value switch
        {
            6 => 270,
            3 => 180,
            8 => 90,
            _ => 0
        };
    }
}