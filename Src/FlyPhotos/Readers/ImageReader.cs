using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FlyPhotos.Readers;

internal static class ImageReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static IndicatorFactory _indicators;

    public static async Task Initialize(CanvasControl d2dCanvas)
    {
        _indicators = new IndicatorFactory(d2dCanvas);
    }

    public static async Task<DisplayItem> GetFirstPreviewSpecialHandlingAsync(
        CanvasControl d2dCanvas, string path)
    {
        try
        {
            if (!File.Exists(path))
                return (new StaticHqDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen));


            if (!AppConfig.Settings.OpenExitZoom)
            {
                var (cachedBmp, actualWidth, actualHeight) = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
                if (null != cachedBmp)
                {
                    var metadata = new ImageMetadata(actualWidth, actualHeight);
                    return (new PreviewDisplayItem(cachedBmp, Origin.DiskCache, metadata));
                }
            }

            var extension = Path.GetExtension(path).ToUpperInvariant();
            switch (extension)
            {
                case ".HEIC":
                case ".HEIF":
                case ".HIF":
                case ".AVIF":
                {
                    if (!AppConfig.Settings.OpenExitZoom)
                        if (NativeHeifReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    if (NativeHeifReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return (retBmp3);
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp4)) return retBmp4;
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".PSD":
                {
                    if (await PsdReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".GIF":
                {
                    if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".PNG":
                {
                    if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".ICO":
                case ".ICON":
                    {
                    if (await IcoReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".TIF":
                case ".TIFF":
                {
                    if (await TiffReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                default:
                {
                    if (!AppConfig.Settings.OpenExitZoom)
                        if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen));
        }
    }

    public static async Task<PreviewDisplayItem> GetPreview(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) 
            return new PreviewDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen);

        try
        {
            Thread.Sleep(1000);
            var (cachedBmp, actualWidth, actualHeight) = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                var metadata = new ImageMetadata(actualWidth, actualHeight);
                return (new PreviewDisplayItem(cachedBmp, Origin.DiskCache, metadata));
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
                    if (await SvgReader.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                }
                case ".GIF":
                case ".PNG":
                {
                    if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                }
                case ".ICO":
                case ".ICON":
                {
                    if (await IcoReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new PreviewDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".TIF":
                case ".TIFF":
                {
                    if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    if (await TiffReader.GetFirstFrameFullSize(d2dCanvas, path) is (true, { } retBmp3)) return (retBmp3);
                    return new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
                }
                default:
                {
                    if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    if (await MagickNetWrap.GetResized(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
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

    public static async Task<HqDisplayItem> GetHqImage(CanvasControl d2dCanvas, string path)
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
                case ".AVIF":
                    {
                    if (NativeHeifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                    return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                }
                case ".PSD":
                {
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                    }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                }
                case ".GIF":
                {
                    if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
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
                    if (await IcoReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen));
                }
                case ".TIF":
                case ".TIFF":
                {
                    if (await TiffReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(_indicators.HqFailed, Origin.ErrorScreen);
                }
                default:
                {
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
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

    public static DisplayItem GetLoadingIndicator()
    {
        return new PreviewDisplayItem(_indicators.Loading, Origin.ErrorScreen);
    }
}