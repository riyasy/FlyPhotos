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
    private static DisplayItem FileNotFoundIndicator;
    private static DisplayItem PreviewFailedIndicator;
    private static DisplayItem HqImageFailedIndicator;
    private static DisplayItem LoadingIndicator;

    public static async Task Initialize(CanvasControl d2dCanvas)
    {
        FileNotFoundIndicator = await LoadIndicatorAsync(d2dCanvas, "FileNotFound.png");
        PreviewFailedIndicator = await LoadIndicatorAsync(d2dCanvas, "PreviewFailed.png");
        HqImageFailedIndicator = await LoadIndicatorAsync(d2dCanvas, "HQImageFailed.png");
        LoadingIndicator = await LoadIndicatorAsync(d2dCanvas, "Loading.png");
    }

    private static async Task<DisplayItem> LoadIndicatorAsync(CanvasControl d2dCanvas, string fileName)
    {
        var path = PathResolver.IsPackagedApp
            ? $"ms-appx:///Assets/Images/{fileName}"
            : Path.Combine(AppContext.BaseDirectory, "Assets", "Images", fileName);

        var bitmap = PathResolver.IsPackagedApp
            ? await CanvasBitmap.LoadAsync(d2dCanvas, new Uri(path))
            : await CanvasBitmap.LoadAsync(d2dCanvas, path);

        return new DisplayItem(bitmap, PreviewSource.ErrorScreen);
    }


    public static async Task<(DisplayItem displayItem, bool continueLoadingHq)> GetFirstPreviewSpecialHandlingAsync(
        CanvasControl d2dCanvas,
        string path)
    {
        try
        {
            if (!File.Exists(path)) return (FileNotFoundIndicator, false);

            if (!AppConfig.Settings.OpenExitZoom)
            {
                var cachedBmp = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
                if (null != cachedBmp)
                {
                    return (new DisplayItem(cachedBmp, PreviewSource.FromDiskCache), true);
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
                                return (retBmp, true);
                        }
                        else
                        {
                            if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2))
                                return (retBmp2, false);
                        }
                        return (PreviewFailedIndicator, false);
                    }
                case ".PSD":
                {
                    if (await PsdReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                    if (await PsdReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                    return (PreviewFailedIndicator, false);
                }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                    return (PreviewFailedIndicator, false);
                }
                case ".GIF":
                {
                    if (await GifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                    return (PreviewFailedIndicator, false);
                }
                case ".PNG":
                {
                    if (await PngReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                    return (PreviewFailedIndicator, false);
                }
                default:
                {
                    if (!AppConfig.Settings.OpenExitZoom)
                    {
                        if (await WicReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                        return (PreviewFailedIndicator, true);
                    }
                    else
                    {
                        if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                        return (PreviewFailedIndicator, false);
                    }
                    
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (PreviewFailedIndicator, false);
        }
    }

    public static async Task<DisplayItem> GetPreview(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) return FileNotFoundIndicator;
        try
        {
            var cachedBmp = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                return new DisplayItem(cachedBmp, PreviewSource.FromDiskCache);
            }

            var extension = Path.GetExtension(path).ToUpper();
            switch (extension)
            {
                case ".HEIC":
                {
                    if (HeifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    return PreviewFailedIndicator;
                }
                case ".PSD":
                {
                    if (await PsdReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return PreviewFailedIndicator;
                }
                case ".SVG":
                {
                    if (await SvgReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return PreviewFailedIndicator;
                }
                case ".GIF":
                {
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return PreviewFailedIndicator;
                }
                case ".PNG":
                {
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return PreviewFailedIndicator;
                }
                default:
                {
                    if (await WicReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                    return PreviewFailedIndicator;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return PreviewFailedIndicator;
        }
    }

    public static async Task<DisplayItem> GetHqImage(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) return FileNotFoundIndicator;
        try
        {
            var extension = Path.GetExtension(path).ToUpper();

            switch (extension)
            {
                case ".PSD":
                {
                    if (await PsdReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return HqImageFailedIndicator;
                }
                case ".SVG":
                {
                    if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return HqImageFailedIndicator;
                }
                case ".GIF":
                {
                    if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return HqImageFailedIndicator;
                }
                case ".PNG":
                {
                    if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return HqImageFailedIndicator;
                }
                default:
                {
                    if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                    return HqImageFailedIndicator;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return HqImageFailedIndicator;
        }
    }

    public static DisplayItem GetLoadingIndicator()
    {
        return LoadingIndicator;
    }
}