#nullable enable

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using ImageMagick;
using ImageMagick.Formats;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class MagickNetWrap
{
    /// <summary>Logger instance for MagickNetWrap.</summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// A preview from the embedded camera preview JPEG of a RAW file, without a RAW decode. The JPEG is often
    /// full size (24 MP+), so it is scaled to the preview size while it is decoded.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetEmbeddedForRawFile(
        ICanvasResourceCreatorWithDpi resourceCreator, string path)
    {
        try
        {
            // One ping gives the embedded JPEG (see LoadEmbeddedPreviewAsync) and the image size and orientation.
            using var pingImage = new MagickImage();
            pingImage.Settings.SetDefines(new DngReadDefines { ReadThumbnail = true });
            pingImage.Ping(path);

            var previewData = pingImage.GetProfile("dng:thumbnail")?.ToByteArray();
            if (previewData == null) return (false, PreviewDisplayItem.Empty());

            var verticalOrientation = pingImage.Orientation == Orientation.RightTop ||
                                      pingImage.Orientation == Orientation.LeftBottom ||
                                      pingImage.Orientation == Orientation.RightBottom ||
                                      pingImage.Orientation == Orientation.LeftTop;

            var width = verticalOrientation ? pingImage.Height : pingImage.Width;
            var height = verticalOrientation ? pingImage.Width : pingImage.Height;
            var metadata = new ImageMetadata(width, height);

            using var stream = new MemoryStream(previewData).AsRandomAccessStream();
            var (pixels, w, h, _, _) = await CappedDecode.DecodeAsync(stream);
            var bitmap = CanvasBitmap.CreateFromBytes(resourceCreator, pixels, (int)w, (int)h,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
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
    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreatorWithDpi resourceCreator, string path, uint maxDimension = 800)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();

            var metadata = new ImageMetadata(image.Width, image.Height);

            CanvasBitmap bitmap;
            if (image.Width <= maxDimension && image.Height <= maxDimension)
            {
                // Small image: pixels are already decoded in memory.
                // Hand the premultiplied BGRA buffer straight to Win2D, no encode/decode round-trip.
                var pixelBytes = PrepareAndExportBgra(image);
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
    public static async Task<(bool, HqDisplayItem)> GetHq(ICanvasResourceCreatorWithDpi d2dCanvas, string path, bool isRaw = false)
    {
        try
        {
            // If DecodeRawData is disabled and the extension is a known ImageMagick RAW format,
            // use the private LoadEmbeddedPreviewAsync directly to get the larger embedded camera JPEG.
            // We skip the small EXIF thumbnail here — HQ display warrants the best quality available
            // without a full decode.
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

            // --- 2. Premultiply alpha inside Magick, then export raw BGRA pixels.
            // This avoids the two SoftwareBitmap copies that straight→premultiplied
            // conversion would otherwise require (saves ~2× full-res frame in managed memory).
            var pixelBytes = PrepareAndExportBgra(image);

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
    /// Readies a decoded image for the BGRA export: converts it to sRGB unless it is already sRGB-compatible
    /// (CMYK and Lab would otherwise export their raw channels as B, G and R; the old <c>MagickFormat.Bgra</c>
    /// coder did the same conversion), and premultiplies alpha inside Magick when the image has alpha. Returns
    /// whether it has. An opaque image is deliberately not given an alpha channel (which would grow the pixel
    /// cache by a third), and no <c>Depth = 8</c> pass is needed: the export scales to 8 bits.
    /// </summary>
    internal static bool PrepareForExport(MagickImage image)
    {
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.RGB or ColorSpace.scRGB or ColorSpace.Gray
            or ColorSpace.LinearGray))
            image.ColorSpace = ColorSpace.sRGB;
        if (!image.HasAlpha) return false;
        image.Alpha(AlphaAction.Associate);
        return true;
    }

    /// <summary>Runs <see cref="PrepareForExport"/> (which changes the image) and exports it whole as BGRA8.</summary>
    private static byte[] PrepareAndExportBgra(MagickImage image)
    {
        bool hasAlpha = PrepareForExport(image);
        using var pixels = image.GetPixelsUnsafe();
        return ExportBgra(pixels, 0, 0, image.Width, image.Height, hasAlpha)
               ?? throw new InvalidOperationException("Magick pixel export failed");
    }

    /// <summary>
    /// Exports a block of pixels as BGRA8; call <see cref="PrepareForExport"/> once first. Without alpha, the alpha bytes are forced to 255: some files (16/32-bit
    /// TGAs with attribute bits) carry alpha values that Magick reports as unused but still exports.
    /// </summary>
    internal static byte[]? ExportBgra(IUnsafePixelCollection<float> pixels, int x, int y, uint width, uint height,
        bool hasAlpha)
    {
        var bytes = pixels.ToByteArray(x, y, width, height, "BGRA");
        if (bytes != null && !hasAlpha) SetOpaque(bytes);
        return bytes;
    }

    private static void SetOpaque(byte[] bgra)
    {
        var px = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
        var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(px);
        var alpha = new Vector<uint>(0xFF000000);
        for (int i = 0; i < vectors.Length; i++) vectors[i] |= alpha;
        for (int i = vectors.Length * Vector<uint>.Count; i < px.Length; i++) px[i] |= 0xFF000000;
    }

    /// <summary>
    /// Pings the RAW file with DNG read defines and extracts the larger embedded camera preview JPEG
    /// stored in the dng:thumbnail profile. This is the full-quality JPEG the camera writes at capture
    /// time — much larger than the EXIF thumbnail.
    /// </summary>
    private static async Task<(bool Success, CanvasBitmap? Bitmap)> LoadEmbeddedPreviewAsync(
        ICanvasResourceCreatorWithDpi resourceCreator, string path)
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