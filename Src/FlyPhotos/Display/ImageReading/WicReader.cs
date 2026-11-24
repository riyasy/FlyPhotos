#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

internal static class WicReader
{
    private const string MS_RAW_DECODER = "Microsoft Raw Image Decoder";
    private const string NIKON_NEF_DECODER = "Nikon .NEF Raw File Decoder";

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
    /// <returns>The preview, or null when the file has no embedded thumbnail. Throws on failure.</returns>
    public static async Task<PreviewDisplayItem?> GetEmbedded(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var (bmp, width, height, rotation) = await GetThumbnail(ctrl, file);
        if (bmp == null) return null;

        var metadata = new ImageMetadata(width, height);
        return new PreviewDisplayItem(bmp, Origin.Disk, metadata, rotation);
    }

    /// <summary>
    /// Decodes and resizes any WIC-supported image to a preview, capped at
    /// <paramref name="maxDimension"/> on the longest side. Uses a single BitmapDecoder
    /// pass — WIC applies the resize natively inside the decode.
    /// Works for JPEG, PNG, GIF (frame 0), TIFF (frame 0), BMP, and any other
    /// format WIC can auto-detect.
    /// </summary>
    public static async Task<PreviewDisplayItem> GetResized(ICanvasResourceCreatorWithDpi ctrl, ImageFile file,
        uint maxDimension = CappedDecode.PreviewMaxSize)
    {
        var (preview, fullWidth, fullHeight) =
            await CappedDecode.DecodeAsync(await file.GetPartialReadStreamAsync(), maxDimension);
        using (preview)
            return new PreviewDisplayItem(preview.ToCanvasBitmap(ctrl), Origin.Disk,
                new ImageMetadata(fullWidth, fullHeight));
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
    /// <returns>The HQ item. Throws on failure.</returns>
    public static async Task<HqDisplayItem> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file, bool isRaw = false)
    {
        // If the extension is not present in MICROSOFT RAW DECODER OR NIKON NEF DECODER
        if (!isRaw)
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, await file.GetStreamAsync());
            return new StaticHqDisplayItem(canvasBitmap, Origin.Disk, 0);
        }

        // The real extension, not file.Extension: it picks a camera codec quirk, and RAW files are never re-typed.
        var isNefExt = Path.GetExtension(file.Path).Equals(".nef", StringComparison.OrdinalIgnoreCase);
        var stream = await file.GetStreamAsync();
        BitmapDecoder? decoder = null;

        if (!AppConfig.Settings.DecodeRawData) // LOAD EMBEDDED PREVIEW FROM RAW FILE
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
                return new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation);
            }
            catch (Exception)
            {
                // The embedded preview failed: fall back to the RAW data.
                stream.Seek(0);
            }
        }

        // LOAD THE RAW DATA ITSELF
        var rawRotation = 0;
        // NIKON NEF decoder doesn't apply EXIF orientation to RAW decode.
        // Also 90/270 seems inverted for them, so we save rotation to apply during rendering.
        if (isNefExt)
        {
            decoder ??= await BitmapDecoder.CreateAsync(stream);
            if (IsNikonNefCodec(decoder))
                rawRotation = await GetRotationFromMetaDataNikonNefCodec(decoder.BitmapProperties);
            stream.Seek(0);
        }

        var rawBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
        return new StaticHqDisplayItem(rawBitmap, Origin.Disk, rawRotation);
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
    /// <returns>
    /// The Win2D bitmap, its dimensions, and unapplied rotation (in degrees); a null bitmap when the file has no
    /// embedded thumbnail. Throws on any other failure.
    /// </returns>
    private static async Task<(CanvasBitmap? Bitmap, int Width, int Height, int Rotation)> GetThumbnail(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var decoder = await BitmapDecoder.CreateAsync(await file.GetPartialReadStreamAsync());
        // OrientedPixelWidth/Height account for EXIF rotation automatically —
        // no need to read the property bag and swap manually.
        var originalWidth = (int)decoder.OrientedPixelWidth;
        var originalHeight = (int)decoder.OrientedPixelHeight;

        ImageStream preview;
        try
        {
            preview = await decoder.GetThumbnailAsync();
        }
        catch (Exception ex) when (ex.HResult is WINCODEC_ERR_CODECNOTHUMBNAIL or E_INVALIDARG)
        {
            return (null, 0, 0, 0); // No embedded thumbnail: routine, not logged.
        }

        using (preview)
        {
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
                using var scaled = (await CappedDecode.DecodeAsync(preview)).Bitmap;
                canvasBitmap = scaled.ToCanvasBitmap(ctrl);
            }
            else
            {
                canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
            }
            return (canvasBitmap, originalWidth, originalHeight, rotation);
        }
    }

    // GetThumbnailAsync on a file with no embedded thumbnail; the CR3 codec answers E_INVALIDARG instead (measured).
    private const int WINCODEC_ERR_CODECNOTHUMBNAIL = unchecked((int)0x88982F44);
    private const int E_INVALIDARG = unchecked((int)0x80070057);

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