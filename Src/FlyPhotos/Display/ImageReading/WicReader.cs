#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
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
    /// <param name="ctrl">The Win2D CanvasControl used for creating the CanvasBitmap.</param>
    /// <param name="inputPath">The absolute path to the image file.</param>
    /// <returns>A tuple indicating success and the resulting PreviewDisplayItem.</returns>
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

    /// <summary>
    /// Retrieves the highest quality representation of an image. For standard images, this directly loads the full pixel data.
    /// For RAW images (.nef, .cr2, etc.), it implements a complex control flow: 
    /// First, it attempts to load the embedded preview if enabled in settings.
    /// If that fails or raw ingestion is configured, it falls back to decoding and extracting the raw sensor data natively using WIC,
    /// while intercepting and manually correcting specific EXIF orientation constraints mandated by Microsoft or Nikon Decoders.
    /// </summary>
    /// <param name="ctrl">The Win2D CanvasControl used for creating the CanvasBitmap.</param>
    /// <param name="inputPath">The absolute path to the image file.</param>
    /// <returns>A tuple indicating success and the resulting high-quality HqDisplayItem.</returns>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var ext = Path.GetExtension(inputPath);
            var isRawFile = CheckRawCodecSupport(ext);

            // If the extension is not present in MICROSOFT RAW DECODER OR NIKON NEF DECODER
            if (!isRawFile)
            {
                using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
                var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, 0));
            }
            else
            {
                var isNefExt = ext.Equals(".nef", StringComparison.OrdinalIgnoreCase);
                using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
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
    /// A cached set of supported RAW extensions discovered initially from the system's WIC decoders.
    /// </summary>
    private static HashSet<string>? _rawExtensions;

    /// <summary>
    /// Determines whether the given file extension is a RAW image format supported by either the
    /// Microsoft RAW Image Decoder or the Nikon .NEF Raw File Decoder. 
    /// Extensively iterations over all WIC extensions the first time it is triggered to construct an O(1) hashed validation pass.
    /// </summary>
    /// <param name="ext">The file extension (e.g., ".nef") to check.</param>
    /// <returns>True if the extension is associated with a supported RAW decoder; otherwise, false.</returns>
    private static bool CheckRawCodecSupport(string ext)
    {
        if (_rawExtensions == null)
        {
            var rawExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var codec in CodecDiscovery.GetAllCodecs())
            {
                var isMsRaw = codec.FriendlyName.Equals(MS_RAW_DECODER, StringComparison.OrdinalIgnoreCase);
                var isNikonNef = codec.FriendlyName.Equals(NIKON_NEF_DECODER, StringComparison.OrdinalIgnoreCase);
                if (isMsRaw || isNikonNef)
                {
                    rawExts.UnionWith(codec.FileExtensions);
                }
            }
            _rawExtensions = rawExts;
        }
        return _rawExtensions.Contains(ext);
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
    /// <param name="ctrl">The Win2D CanvasControl to bind initialization to.</param>
    /// <param name="inputPath">The absolute path to the local source image file.</param>
    /// <returns>A tuple containing the Win2D bitmap, its dimensions, and unapplied rotation (in degrees).</returns>
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

            var rotation = 0;
            // Nikon NEF decoder doesn't apply exif orientation for Thumbnail. We need to read manually and apply it in rendering.
            if (Path.GetExtension(inputPath).Equals(".nef", StringComparison.OrdinalIgnoreCase) && IsNikonNefCodec(decoder))
                rotation = await GetRotationFromMetaDataNikonNefCodec(decoder.BitmapProperties);

            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
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