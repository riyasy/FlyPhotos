#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using ImageMagick;
using ImageMagick.Formats;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class MagickNetWrap
{
    /// <summary>Logger instance for MagickNetWrap.</summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Extracts the larger embedded camera preview JPEG from a RAW image file without performing a full decode.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetEmbeddedForRawFile(
        ICanvasResourceCreator resourceCreator, string path)
    {
        try
        {
            // Ping to read image dimensions and orientation for metadata.
            using var pingImage = new MagickImage();
            pingImage.Ping(path);

            var verticalOrientation = pingImage.Orientation == OrientationType.RightTop ||
                                      pingImage.Orientation == OrientationType.LeftBottom ||
                                      pingImage.Orientation == OrientationType.RightBottom ||
                                      pingImage.Orientation == OrientationType.LeftTop;

            var width = verticalOrientation ? pingImage.Height : pingImage.Width;
            var height = verticalOrientation ? pingImage.Width : pingImage.Height;
            var metadata = new ImageMetadata(width, height);

            var (ok, bitmap) = await LoadEmbeddedPreviewAsync(resourceCreator, path);
            if (!ok || bitmap == null)
                return (false, PreviewDisplayItem.Empty());

            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to extract embedded preview with ImageMagick: {path}");
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Fully decodes an image with ImageMagick and returns a preview resized to fit within
    /// <paramref name="maxDimension"/> on the longest side. Small images are uploaded as raw BGRA
    /// pixel buffers to avoid an encode/decode round-trip. Large images are resized and encoded
    /// to a compact JPEG stream (Q80) before being handed to Win2D.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreator resourceCreator, string path, uint maxDimension = 800)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            image.Depth = 8;

            var metadata = new ImageMetadata(image.Width, image.Height);

            CanvasBitmap bitmap;
            if (image.Width <= maxDimension && image.Height <= maxDimension)
            {
                // Small image: pixels are already decoded in memory.
                // No alpha preservation needed — output is cached as JPEG.
                // Hand the raw BGRA buffer straight to Win2D, no encode/decode round-trip.
                var pixelBytes = image.ToByteArray(MagickFormat.Bgra);
                bitmap = CanvasBitmap.CreateFromBytes(
                    resourceCreator, pixelBytes,
                    (int)image.Width, (int)image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
            }
            else
            {
                // Large image: resize first, then encode to a compact JPEG stream.
                // Q80 is indistinguishable from Q95 at thumbnail sizes and produces
                // a much smaller buffer for CanvasBitmap.LoadAsync to decode.
                var geometry = new MagickGeometry(maxDimension, maxDimension) { Greater = true };
                image.Resize(geometry);

                using var stream = new MemoryStream(256 * 1024); // pre-size to avoid repeated doublings
                image.Format = MagickFormat.Jpeg;
                image.Quality = 80;
                await image.WriteAsync(stream);
                stream.Position = 0;

                bitmap = await CanvasBitmap.LoadAsync(resourceCreator, stream.AsRandomAccessStream());
            }

            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load and resize image with ImageMagick: {path}");
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Returns the highest-quality <see cref="HqDisplayItem"/> for an image decoded via ImageMagick.
    /// For known RAW file extensions, respects <c>AppConfig.Settings.DecodeRawData</c>: when disabled,
    /// it first attempts the fast embedded camera preview JPEG via <c>LoadEmbeddedPreviewAsync</c> and
    /// only falls back to a full RAW sensor decode if the preview is unavailable.
    /// Alpha is premultiplied inside Magick before export to avoid two SoftwareBitmap copies.
    /// </summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl d2dCanvas, string path, bool isRaw = false)
    {
        try
        {
            // If DecodeRawData is disabled and the extension is a known ImageMagick RAW format,
            // use the private LoadEmbeddedPreviewAsync directly to get the larger embedded camera JPEG.
            // We skip the small EXIF thumbnail here — HQ display warrants the best quality available
            // without a full decode.
            var ext = Path.GetExtension(path);
            if (isRaw && !AppConfig.Settings.DecodeRawData)
            {
                var (success, bitmap) = await LoadEmbeddedPreviewAsync(d2dCanvas, path);
                if (success)
                    return (true, new StaticHqDisplayItem(bitmap!, Origin.Disk));
                // Embedded preview unavailable — fall through to full RAW decode below.
            }

            // --- 1. Decode at full resolution ---
            using var image = new MagickImage(path);
            image.AutoOrient();
            image.Depth = 8;
            image.Alpha(AlphaOption.Set);

            // --- 2. Premultiply alpha inside Magick, then export raw BGRA pixels.
            // This avoids the two SoftwareBitmap copies that straight→premultiplied
            // conversion would otherwise require (saves ~2× full-res frame in managed memory).
            image.Alpha(AlphaOption.Associate);
            var pixelBytes = image.ToByteArray(MagickFormat.Bgra);

            // --- 3. Upload directly to Win2D — no SoftwareBitmap intermediaries needed ---
            var bitmap2 = CanvasBitmap.CreateFromBytes(
                d2dCanvas, pixelBytes,
                (int)image.Width, (int)image.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            return (true, new StaticHqDisplayItem(bitmap2, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, HqDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Pings the RAW file with DNG read defines and extracts the larger embedded camera preview JPEG
    /// stored in the dng:thumbnail profile. This is the full-quality JPEG the camera writes at capture
    /// time — much larger than the EXIF thumbnail.
    /// </summary>
    private static async Task<(bool Success, CanvasBitmap? Bitmap)> LoadEmbeddedPreviewAsync(
        ICanvasResourceCreator resourceCreator, string path)
    {
        try
        {
            using var image = new MagickImage();
            // DngReadDefines.ReadThumbnail exposes the full embedded camera preview JPEG in the dng:thumbnail profile.
            var defines = new DngReadDefines { ReadThumbnail = true };
            image.Settings.SetDefines(defines);
            // Ping reads only metadata and embedded streams — no full RAW decode.
            image.Ping(path);

            var previewData = image.GetProfile("dng:thumbnail")?.ToByteArray();
            if (previewData == null) return (false, null);

            using var stream = new MemoryStream(previewData);
            var bitmap = await CanvasBitmap.LoadAsync(resourceCreator, stream.AsRandomAccessStream());
            return (true, bitmap);
        }
        catch
        {
            return (false, null);
        }
    }
}