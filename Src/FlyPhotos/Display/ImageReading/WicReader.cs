#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class WicReader
{
    private const string MS_RAW_DECODER = "Microsoft Raw Image Decoder";
    private const string NIKON_NEF_DECODER = "Nikon .NEF Raw File Decoder";

    /// <summary>
    /// Logger instance for WicReader.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Windows Property key used to extract EXIF orientation metadata from an image.
    /// </summary>
    private static readonly string[] OrientationKey = ["System.Photo.Orientation"];

    /// <summary>
    /// Extracts the embedded thumbnail/preview from an image file without fully decoding the high-resolution pixel data.
    /// Typically used for fast initial rendering or gallery views.
    /// </summary>
    /// <param name="ctrl">The Win2D ICanvasResourceCreatorWithDpi used for creating the CanvasBitmap.</param>
    /// <param name="file">The image file.</param>
    /// <returns>A tuple indicating success and the resulting PreviewDisplayItem.</returns>
    public static async Task<(bool, PreviewDisplayItem)> GetEmbedded(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var (bmp, width, height, rotation) = await GetThumbnail(ctrl, file);
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
    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreatorWithDpi ctrl, ImageFile file,
        uint maxDimension = CappedDecode.PreviewMaxSize)
    {
        try
        {
            var (pixels, width, height, fullWidth, fullHeight) =
                await CappedDecode.DecodeAsync(await file.GetPartialReadStreamAsync(), maxDimension);
            var bitmap = CanvasBitmap.CreateFromBytes(ctrl, pixels, (int)width, (int)height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, new ImageMetadata(fullWidth, fullHeight)));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WicReader.GetResized failed for {0}", file.Path);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Retrieves the highest quality representation of an image. For standard images, this directly loads the full pixel data.
    /// For RAW images (.nef, .cr2, etc.), it implements a complex control flow: 
    /// First, it attempts to load the embedded preview if enabled in settings.
    /// If that fails or raw ingestion is configured, it falls back to decoding and extracting the raw sensor data natively using WIC,
    /// while intercepting and manually correcting specific EXIF orientation constraints mandated by Microsoft or Nikon Decoders.
    /// </summary>
    /// <param name="ctrl">The Win2D ICanvasResourceCreatorWithDpi used for creating the CanvasBitmap.</param>
    /// <param name="file">The image file.</param>
    /// <param name="isRaw">Is a camera RAW file</param>
    /// <returns>A tuple indicating success and the resulting high-quality HqDisplayItem.</returns>
    public static async Task<(bool, HqDisplayItem)> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file, bool isRaw = false)
    {
        try
        {
            // The real extension, not file.Extension: it picks a camera codec quirk, and RAW files are never re-typed.
            var ext = Path.GetExtension(file.Path);

            // If the extension is not present in MICROSOFT RAW DECODER OR NIKON NEF DECODER
            if (!isRaw)
            {
                var stream = await file.GetStreamAsync();
                var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, 0));
            }
            else
            {
                var isNefExt = ext.Equals(".nef", StringComparison.OrdinalIgnoreCase);
                var stream = await file.GetStreamAsync();
                var loadRawData = AppConfig.Settings.DecodeRawData;

                BitmapDecoder? decoder = null;

                if (!loadRawData) // LOAD EMBEDDED PREVIEW FROM RAW FILE
                {
                    try
                    {
                        decoder = await BitmapDecoder.CreateAsync(stream);
                        using var preview = await decoder.GetPreviewAsync();
                        var rotation = 0;
                        // NIKON NEF Decoder applies EXIF orientation directly to the preview
                        // Microsoft RAW decoder seems not to apply EXIF orientation in preview.
                        // So we save rotation for applying during rendering.
                        if (IsMsRawCodec(decoder))
                            rotation = await GetRotationFromMetaData(decoder.BitmapProperties);

                        var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
                        return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation));
                    }
                    catch (Exception)
                    {
                        // If Loading embedded preview in the last if failed, fallback to loading RAW data
                        loadRawData = true;
                        stream.Seek(0);
                    }
                }

                if (loadRawData) // TRY TO LOAD RAW DATA ITSELF
                {
                    var rotation = 0;
                    // NIKON NEF decoder doesn't apply EXIF orientation to RAW decode. 
                    // Also 90/270 seems inverted for them, so we save rotation to apply during rendering.
                    if (isNefExt)
                    {
                        decoder ??= await BitmapDecoder.CreateAsync(stream);
                        if (IsNikonNefCodec(decoder))
                        {
                            rotation = await GetRotationFromMetaDataNikonNefCodec(decoder.BitmapProperties);
                        }
                        stream.Seek(0);
                    }

                    var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                    return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation));
                }
            }

            return (false, HqDisplayItem.Empty());
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, HqDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Evaluates if the provided WIC decoder was specifically spawned via the "Nikon .NEF Raw File Decoder".
    /// </summary>
    /// <param name="decoder">The active Bitmap Decoder used to read the image.</param>
    /// <returns>True if the friendly name represents the Nikon Codec.</returns>
    private static bool IsNikonNefCodec(BitmapDecoder decoder)
    {
        var name = decoder.DecoderInformation?.FriendlyName ?? string.Empty;
        return name.Equals(NIKON_NEF_DECODER, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates if the provided WIC decoder was specifically spawned via the "Microsoft Raw Image Decoder".
    /// </summary>
    /// <param name="decoder">The active Bitmap Decoder used to read the image.</param>
    /// <returns>True if the friendly name represents the Microsoft Codec.</returns>
    private static bool IsMsRawCodec(BitmapDecoder decoder)
    {
        var name = decoder.DecoderInformation?.FriendlyName ?? string.Empty;
        return name.Equals(MS_RAW_DECODER, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asynchronously extracts the thumbnail frame natively embedded within an image using WIC. 
    /// Computes the original oriented dimensions while intercepting unapplied EXIF rotation degrees dynamically, 
    /// especially for NEF components decoded using Nikon codecs.
    /// </summary>
    /// <param name="ctrl">The Win2D ICanvasResourceCreatorWithDpi to bind initialization to.</param>
    /// <param name="file">The image file.</param>
    /// <returns>A tuple containing the Win2D bitmap, its dimensions, and unapplied rotation (in degrees).</returns>
    private static async Task<(CanvasBitmap? Bitmap, int Width, int Height, int Rotation)> GetThumbnail(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(await file.GetPartialReadStreamAsync());
            // OrientedPixelWidth/Height account for EXIF rotation automatically —
            // no need to read the property bag and swap manually.
            var originalWidth = (int)decoder.OrientedPixelWidth;
            var originalHeight = (int)decoder.OrientedPixelHeight;
            using var preview = await decoder.GetThumbnailAsync();

            var rotation = 0;
            // Nikon NEF decoder doesn't apply exif orientation for Thumbnail. We need to read manually and apply it in rendering.
            // The real extension, not file.Extension: RAW files are never re-typed by the header check.
            if (Path.GetExtension(file.Path).Equals(".nef", StringComparison.OrdinalIgnoreCase) && IsNikonNefCodec(decoder))
                rotation = await GetRotationFromMetaDataNikonNefCodec(decoder.BitmapProperties);

            // A full-size image returned as the thumbnail is scaled down to the preview size; others load as they are.
            var thumbDecoder = await BitmapDecoder.CreateAsync(preview);
            bool tooLarge = CappedDecode.IsTooLargeForThumbnail(thumbDecoder.PixelWidth, thumbDecoder.PixelHeight);
            preview.Seek(0);

            CanvasBitmap canvasBitmap;
            if (tooLarge)
            {
                var (pixels, width, height, _, _) = await CappedDecode.DecodeAsync(preview);
                canvasBitmap = CanvasBitmap.CreateFromBytes(ctrl, pixels, (int)width, (int)height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
            }
            else
            {
                canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
            }
            return (canvasBitmap, originalWidth, originalHeight, rotation);
        }
        catch (Exception)
        {
            return (null, 0, 0, 0);
        }
    }

    /// <summary>
    /// Extracts EXIF rotation definitions from a traditional image system property structure.
    /// Translates numerical property constants (such as 3, 6, 8) into rendering degrees (180, 90, 270).
    /// </summary>
    /// <param name="bmpProps">The property view object containing the Windows Image Metadata dictionary.</param>
    /// <returns>The required clockwise rotation integer in degrees.</returns>
    private static async Task<int> GetRotationFromMetaData(BitmapPropertiesView bmpProps)
    {
        var result = await bmpProps.GetPropertiesAsync(OrientationKey);
        if (!result.TryGetValue(OrientationKey[0], out var orientation)) return 0;
        return (ushort)orientation.Value switch
        {
            6 => 90,
            3 => 180,
            8 => 270,
            _ => 0
        };
    }

    /// <summary>
    /// Specifically decodes the EXIF orientation tag retrieved directly from the Nikon Codec, which deviates 
    /// predictably from the standard EXIF orientation protocol (inverting 90 and 270 degree interpretations).
    /// </summary>
    /// <param name="bmpProps">The property view object containing the Windows Image Metadata dictionary.</param>
    /// <returns>The required clockwise rotation integer in degrees resolving Nikon's deviation.</returns>
    private static async Task<int> GetRotationFromMetaDataNikonNefCodec(BitmapPropertiesView bmpProps)
    {
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