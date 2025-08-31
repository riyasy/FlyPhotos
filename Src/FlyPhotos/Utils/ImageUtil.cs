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

internal class ImageUtil
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
                return (new StaticHqDisplayItem(FileNotFoundIndicator));


            if (!AppConfig.Settings.OpenExitZoom)
            {
                var cachedBmp = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
                if (null != cachedBmp)
                {
                    return (new PreviewDisplayItem(cachedBmp, PreviewSource.FromDiskCache));
                }
            }

            var extension = Path.GetExtension(path).ToUpper();
            switch (extension)
            {
                case ".HEIC":
                    {
                        if (!AppConfig.Settings.OpenExitZoom)
                        {
                            if (HeifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp))
                                return (retBmp);
                            return (new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen));
                        }
                        else
                        {
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2))
                                return (retBmp2);
                            return (new StaticHqDisplayItem(HqImageFailedIndicator));
                        }
                    }
                case ".PSD":
                {
                    if (await PsdReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    if (await PsdReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator));
                }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator));
                }
                case ".GIF":
                {
                    if (await GifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator));
                }
                case ".PNG":
                {
                    if (await PngReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                    return (new StaticHqDisplayItem(HqImageFailedIndicator));
                }
                default:
                {
                    if (!AppConfig.Settings.OpenExitZoom)
                    {
                        if (await WicReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp);
                        return (new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen));
                    }
                    else
                    {
                        if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2);
                        return (new StaticHqDisplayItem(HqImageFailedIndicator));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen));
        }
    }

    public static async Task<PreviewDisplayItem> GetPreview(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) 
            return new PreviewDisplayItem(FileNotFoundIndicator, PreviewSource.ErrorScreen);

        try
        {
            var cachedBmp = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                return new PreviewDisplayItem(cachedBmp, PreviewSource.FromDiskCache);
            }

            var extension = Path.GetExtension(path).ToUpper();
            switch (extension)
            {
                case ".HEIC":
                {
                    if (HeifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
                }
                case ".PSD":
                {
                    if (await PsdReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
                }
                case ".SVG":
                {
                    if (await SvgReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
                }
                case ".GIF":
                {
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
                }
                case ".PNG":
                {
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
                }
                default:
                {
                    if (await WicReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new PreviewDisplayItem(PreviewFailedIndicator, PreviewSource.ErrorScreen);
        }
    }

    public static async Task<HqDisplayItem> GetHqImage(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) 
            return new StaticHqDisplayItem(FileNotFoundIndicator);

        try
        {
            var extension = Path.GetExtension(path).ToUpper();

            switch (extension)
            {
                case ".HEIC":
                {
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (HeifReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    return new StaticHqDisplayItem(HqImageFailedIndicator);
                }
                case ".PSD":
                {
                    if (await PsdReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator);
                    }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator);
                }
                case ".GIF":
                {
                    if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator);
                }
                case ".PNG":
                {
                    if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator);
                }
                default:
                {
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return new StaticHqDisplayItem(HqImageFailedIndicator);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return new StaticHqDisplayItem(HqImageFailedIndicator);
        }
    }

    public static DisplayItem GetLoadingIndicator()
    {
        return new PreviewDisplayItem(LoadingIndicator, PreviewSource.ErrorScreen);
    }
}