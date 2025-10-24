using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using FlyPhotos.Readers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Utils;

internal static class ImageUtil
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static CanvasBitmap FileNotFoundIndicator;
    private static CanvasBitmap PreviewFailedIndicator;
    private static CanvasBitmap HqImageFailedIndicator;
    private static CanvasBitmap LoadingIndicator;

    public static async Task Initialize(CanvasControl d2dCanvas)
    {
        FileNotFoundIndicator = await LoadIndicatorAsync(d2dCanvas, "FileNotFound.png");
        PreviewFailedIndicator = await LoadIndicatorAsync(d2dCanvas, "PreviewFailed.png");
        HqImageFailedIndicator = await LoadIndicatorAsync(d2dCanvas, "HQImageFailed.png");
        LoadingIndicator = await LoadIndicatorAsync(d2dCanvas, "Loading.png");
    }

    private static async Task<CanvasBitmap> LoadIndicatorAsync(CanvasControl d2dCanvas, string fileName)
    {
        var path = PathResolver.IsPackagedApp
            ? $"ms-appx:///Assets/Images/{fileName}"
            : Path.Combine(AppContext.BaseDirectory, "Assets", "Images", fileName);

        return PathResolver.IsPackagedApp
            ? await CanvasBitmap.LoadAsync(d2dCanvas, new Uri(path))
            : await CanvasBitmap.LoadAsync(d2dCanvas, path);
    }


    public static async Task<DisplayItem> GetFirstPreviewSpecialHandlingAsync(
        CanvasControl d2dCanvas, string path)
    {
        try
        {
            if (!File.Exists(path))
                return (new StaticHqDisplayItem(FileNotFoundIndicator, Origin.ErrorScreen));


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
                    {
                        if (!AppConfig.Settings.OpenExitZoom)
                        {
                            if (NativeHeifReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                            return (new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen));
                        }
                        else
                        {
                            if (NativeHeifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                            if (HeifCodecResolver.IsHevcDecoderAvailable)
                                if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                            if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                            return (new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen));
                        }
                    }
                case ".PSD":
                {
                    if (await PsdReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen));
                }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen));
                }
                case ".GIF":
                {
                    if (await GifReader.GetFirstFrameFullSize(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen));
                }
                case ".PNG":
                {
                    if (await PngReader.GetFirstFrameFullSize(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen));
                }
                default:
                {
                    if (!AppConfig.Settings.OpenExitZoom)
                    {
                        if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                        return (new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen));
                    }
                    else
                    {
                        if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                        return (new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen));
        }
    }

    public static async Task<PreviewDisplayItem> GetPreview(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) 
            return new PreviewDisplayItem(FileNotFoundIndicator, Origin.ErrorScreen);

        try
        {
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
                {
                    if (NativeHeifReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    //if (HeifCodecResolver.IsHevcDecoderAvailable)
                    //        if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    //if (await MagickNetWrap.GetResized(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                    return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
                }
                case ".PSD":
                {
                    if (await PsdReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
                }
                case ".SVG":
                {
                    if (await SvgReader.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
                }
                case ".GIF":
                {
                    if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
                }
                case ".PNG":
                {
                    if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
                }
                default:
                {
                    if (await WicReader.GetEmbedded(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await MagicScalerWrap.GetResized(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new PreviewDisplayItem(PreviewFailedIndicator, Origin.ErrorScreen);
        }
    }

    public static async Task<HqDisplayItem> GetHqImage(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) 
            return new StaticHqDisplayItem(FileNotFoundIndicator, Origin.ErrorScreen);

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
                    if (HeifCodecResolver.IsHevcDecoderAvailable)
                        if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp3)) return retBmp3;
                    return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
                }
                case ".PSD":
                {
                    if (MagickNetWrap.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
                    }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
                }
                case ".GIF":
                {
                    if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
                }
                case ".PNG":
                {
                    if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
                }
                default:
                {
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new StaticHqDisplayItem(HqImageFailedIndicator, Origin.ErrorScreen);
        }
    }

    public static DisplayItem GetLoadingIndicator()
    {
        return new PreviewDisplayItem(LoadingIndicator, Origin.ErrorScreen);
    }
}