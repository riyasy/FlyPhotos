using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class ImageReader
{
    /// <summary>Logger instance for ImageReader.</summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Provides pre-rendered indicator bitmaps (loading, error, file-not-found) used as fallback display items.</summary>
    private static IndicatorFactory _indicators;

    /// <summary>
    /// Initializes shared resources that depend on the Win2D canvas, specifically the <see cref="IndicatorFactory"/>.
    /// Must be called once before any other image loading methods.
    /// </summary>
    public static void Initialize(ICanvasResourceCreatorWithDpi d2dCanvas)
    {
        _indicators = new IndicatorFactory(d2dCanvas);
    }

    /// <summary>
    /// Loads the best available display item for the first frame of an image, using format-specific
    /// fast paths (embedded thumbnails, native decoders) before falling back to heavier decoders.
    /// </summary>
    public static async Task<DisplayItem> GetFirstPreviewSpecialHandlingAsync(
        ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        var item = await GetFirstPreviewCoreAsync(d2dCanvas, path);

        // For PNG, GIF, TIFF, etc. the fast path goes straight to the normal HQ readers, which all fail on an
        // image over the GPU's maximum bitmap size. Return a preview instead, so the caller's HQ load
        // (GetHqImage) runs next and takes the large-image path while the preview is on screen.
        if (await FailedBecauseOversized(d2dCanvas, item, path) &&
            await GetPreview(d2dCanvas, path) is { } preview && !preview.IsErrorOrUndefined())
            return preview;
        return item;
    }

    private static async Task<DisplayItem> GetFirstPreviewCoreAsync(ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        try
        {
            if (!File.Exists(path))
                return new StaticHqDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen);

            if (!AppConfig.Settings.OpenExitZoom)
            {
                var (cachedBmp, actualWidth, actualHeight) = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
                if (null != cachedBmp)
                {
                    var metadata = new ImageMetadata(actualWidth, actualHeight);
                    return new PreviewDisplayItem(cachedBmp, Origin.DiskCache, metadata);
                }
            }

            var extension = Path.GetExtension(path).ToUpperInvariant();
            switch (extension)
            {
                case ".HEIC":
                case ".HEIF":
                case ".HIF":
                    {
                        if (!AppConfig.Settings.OpenExitZoom)
                            if (NativeHeifReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (NativeHeifReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        if (CodecDiscovery.IsWicSupported(extension))
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                        if (CodecDiscovery.IsMagickSupported(extension))
                            if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp4)) return retBmp4;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".AVIF":
                    {
                        if (!AppConfig.Settings.OpenExitZoom)
                            if (NativeHeifReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (await NativeAvifReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        if (CodecDiscovery.IsWicSupported(extension))
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                        if (CodecDiscovery.IsMagickSupported(extension))
                            if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp4)) return retBmp4;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".PSD":
                    {
                        if (await PsdReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".SVG":
                    {
                        if (ResvgWrap.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".GIF":
                    {
                        if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".WEBP":
                    {
                        if (await WebpReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".PNG":
                    {
                        if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".ICO":
                case ".ICON":
                    {
                        if (await IcoReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".TIF":
                case ".TIFF":
                    {
                        if (await TiffReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                default:
                    {
                        if (!AppConfig.Settings.OpenExitZoom)
                        {
                            if (CodecDiscovery.IsWicSupported(extension))
                                if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                            if (CodecDiscovery.IsMagickRaw(extension))
                                if (await MagickNetWrap.GetEmbeddedForRawFile(d2dCanvas, path) is (true, { } retBmp1)) return retBmp1;
                        }

                        if (await GetHqFromRawDecoderPipeLine(d2dCanvas, path, extension) is (true, { } rawResult)) return rawResult;
                        // Non-RAW fallback
                        if (CodecDiscovery.IsWicNonRaw(extension))
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmpW)) return retBmpW;
                        if (CodecDiscovery.IsMagickNonRaw(extension))
                            if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmpM)) return retBmpM;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
        }
    }

    /// <summary>
    /// Loads a low-resolution preview of an image suitable for the thumbnail ribbon or preview.
    /// Tries cheap embedded previews first (EXIF thumbnail, embedded JPEG), then resizes via WIC or ImageMagick.
    /// Format-specific decoders are used.
    /// </summary>
    public static async Task<PreviewDisplayItem> GetPreview(ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        if (!File.Exists(path))
            return new PreviewDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen);

        try
        {
            var (cachedBmp, actualWidth, actualHeight) = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                var metadata = new ImageMetadata(actualWidth, actualHeight);
                return new PreviewDisplayItem(cachedBmp, Origin.DiskCache, metadata);
            }

            var extension = Path.GetExtension(path).ToUpperInvariant();
            switch (extension)
            {
                case ".HEIC":
                case ".HEIF":
                case ".HIF":
                case ".AVIF":
                    {
                        if (NativeHeifReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (CodecDiscovery.IsMagickSupported(extension))
                            if (await MagickNetWrap.GetResized(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                        return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                    }
                case ".PSD":
                    {
                        if (await PsdReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                    }
                case ".SVG":
                    {
                        if (ResvgWrap.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                    }
                case ".GIF":
                case ".PNG":
                case ".BMP":
                case ".WEBP":
                    {
                        if (await WicReader.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                    }
                case ".ICO":
                case ".ICON":
                    {
                        if (await IcoReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new PreviewDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".TIF":
                case ".TIFF":
                    {
                        if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (await WicReader.GetResized(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                    }
                default:
                    {
                        if (CodecDiscovery.IsWicSupported(extension))
                            if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (CodecDiscovery.IsMagickRaw(extension))
                            if (await MagickNetWrap.GetEmbeddedForRawFile(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                        if (CodecDiscovery.IsWicSupported(extension))
                            if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        if (CodecDiscovery.IsMagickSupported(extension))
                            if (await MagickNetWrap.GetResized(d2dCanvas, path) is (true, { } retBmp4)) return retBmp4;
                        return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                    }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
        }
    }

    /// <summary>
    /// Loads the full high-quality image for display in the main viewer. Images over the GPU's maximum bitmap
    /// size fail every normal decoder, so they fall through to <see cref="LargeImageReader"/> as the last step.
    /// </summary>
    /// <param name="knownOversized">
    /// The caller already knows the image is too big (preview metadata, or an earlier call's
    /// <c>Oversized</c>), so the doomed normal decode is skipped.
    /// </param>
    /// <param name="allowLarge">
    /// False when prefetching a neighbour: an oversized image's tiles could take gigabytes, so it is not
    /// decoded yet.
    /// </param>
    /// <param name="ct">Cancels a large decode (the user moved on). Normal decodes don't observe it.</param>
    /// <returns>
    /// <c>Item</c>: the image, or the error screen when nothing could read it; null when an oversized image
    /// was not allowed here or its decode was cancelled, so the caller retries later. <c>Oversized</c>: the
    /// image is too big for a single GPU bitmap; pass it back as <paramref name="knownOversized"/>.
    /// </returns>
    public static async Task<(HqDisplayItem Item, bool Oversized)> GetHqImage(ICanvasResourceCreatorWithDpi d2dCanvas,
        string path, bool knownOversized = false, bool allowLarge = true, CancellationToken ct = default)
    {
        HqDisplayItem failed = null;
        if (!knownOversized)
        {
            var hq = await GetHqCoreAsync(d2dCanvas, path);
            // Preview metadata can't always tell up front (missing, or cached by an older build), so a failure
            // may mean "too big". The header probe keeps a genuinely broken file on the old error path.
            if (!await FailedBecauseOversized(d2dCanvas, hq, path)) return (hq, false);
            failed = hq;
        }

        if (!allowLarge) return (null, true);
        try
        {
            if (await LargeImageReader.GetHq(d2dCanvas, path, ct) is { } large) return (large, true);
        }
        catch (OperationCanceledException)
        {
            return (null, true);
        }

        // No decoder could read it at any size: show the normal error screen.
        return (failed ?? await GetHqCoreAsync(d2dCanvas, path), true);
    }

    /// <summary>
    /// Whether <paramref name="item"/> is a failure caused by the image being over the GPU's maximum bitmap
    /// size rather than by a broken file. Only failures pay for the header probe.
    /// </summary>
    private static async Task<bool> FailedBecauseOversized(ICanvasResourceCreatorWithDpi d2dCanvas,
        DisplayItem item, string path) =>
        item is HqDisplayItem { Origin: Origin.ErrorScreen } && File.Exists(path) &&
        await LargeImageReader.IsOversizedAsync(path, d2dCanvas.Device.MaximumBitmapSizeInPixels);

    /// <summary>
    /// The normal HQ decode: format-specific decoders in priority order, each producing one CanvasBitmap.
    /// </summary>
    private static async Task<HqDisplayItem> GetHqCoreAsync(ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        if (!File.Exists(path))
            return new StaticHqDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen);

        //var sw = Stopwatch.StartNew();

        try
        {
            var extension = Path.GetExtension(path).ToUpperInvariant();

            switch (extension)
            {
                case ".HEIC":
                case ".HEIF":
                case ".HIF":
                    {
                        if (NativeHeifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (CodecDiscovery.IsWicSupported(extension))
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        if (CodecDiscovery.IsMagickSupported(extension))
                            if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".AVIF":
                    {
                        if (await NativeAvifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        if (CodecDiscovery.IsWicSupported(extension))
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                        if (CodecDiscovery.IsMagickSupported(extension))
                            if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".PSD":
                    {
                        if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".SVG":
                    {
                        if (ResvgWrap.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".GIF":
                    {
                        if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".WEBP":
                    {
                        if (await WebpReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".PNG":
                    {
                        if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".ICO":
                case ".ICON":
                    {
                        if (await IcoReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".TIF":
                case ".TIFF":
                    {
                        if (await TiffReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                default:
                    {
                        if (await GetHqFromRawDecoderPipeLine(d2dCanvas, path, extension) is (true, { } rawResult)) return rawResult;
                        // Non-RAW fallback
                        if (CodecDiscovery.IsWicNonRaw(extension))
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmpW)) return retBmpW;
                        if (CodecDiscovery.IsMagickNonRaw(extension))
                            if (await MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmpM)) return retBmpM;
                        return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
        }
        //finally
        //{
        //    sw.Stop();
        //    try { Logger.Debug("GetHqImage total time for {0}: {1} ms", path, sw.ElapsedMilliseconds); }
        //    catch { }
        //}
    }

    /// <summary>Returns a loading indicator display item to show while an image is being asynchronously decoded.</summary>
    public static DisplayItem GetLoadingIndicator() => new PreviewDisplayItem(_indicators.Loading, Origin.ErrorScreen);

    /// <summary>
    /// Tries each RAW decoder in the configured priority order (Rawler → WIC → Magick).
    /// Returns empty if no decoder succeeds.
    /// </summary>
    private static async Task<(bool, HqDisplayItem)> GetHqFromRawDecoderPipeLine(
        ICanvasResourceCreatorWithDpi d2dCanvas, string path, string extension)
    {
        foreach (var decoder in AppConfig.Settings.RawDecoderPriority)
        {
            switch (decoder)
            {
                case RawDecoder.Rawler:
                    if (CodecDiscovery.IsRawlerRaw(extension))
                        if (RawlerWrapper.GetHq(d2dCanvas, path) is (true, { } bmp1)) return (true, bmp1);
                    break;
                case RawDecoder.WIC:
                    if (CodecDiscovery.IsWicRaw(extension))
                        if (await WicReader.GetHq(d2dCanvas, path, true) is (true, { } bmp2)) return (true, bmp2);
                    break;
                case RawDecoder.ImageMagick:
                    if (CodecDiscovery.IsMagickRaw(extension))
                        if (await MagickNetWrap.GetHq(d2dCanvas, path, true) is (true, { } bmp3)) return (true, bmp3);
                    break;
            }
        }
        return (false, HqDisplayItem.Empty());
    }
}