using FlyPhotos.Data;
using FlyPhotos.Readers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.IO;
using System.Threading.Tasks;

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
        if (App.Packaged)
        {
            FileNotFoundIndicator =
                new DisplayItem(await CanvasBitmap.LoadAsync(d2dCanvas,
                    new Uri("ms-appx:///Assets/Images/FileNotFound.png")), DisplayItem.PreviewSource.ErrorScreen);
            PreviewFailedIndicator =
                new DisplayItem(
                    await CanvasBitmap.LoadAsync(d2dCanvas, new Uri("ms-appx:///Assets/Images/PreviewFailed.png")), DisplayItem.PreviewSource.ErrorScreen);
            HqImageFailedIndicator =
                new DisplayItem(
                    await CanvasBitmap.LoadAsync(d2dCanvas, new Uri("ms-appx:///Assets/Images/HQImageFailed.png")), DisplayItem.PreviewSource.ErrorScreen);
            LoadingIndicator =
                new DisplayItem(await CanvasBitmap.LoadAsync(d2dCanvas, new Uri("ms-appx:///Assets/Images/Loading.png")), DisplayItem.PreviewSource.ErrorScreen);
        }
        else
        {
            var folderName = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location), "Assets\\Images");
            FileNotFoundIndicator =
                new DisplayItem(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\FileNotFound.png"), DisplayItem.PreviewSource.ErrorScreen);
            PreviewFailedIndicator =
                new DisplayItem(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\PreviewFailed.png"), DisplayItem.PreviewSource.ErrorScreen);
            HqImageFailedIndicator =
                new DisplayItem(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\HQImageFailed.png"), DisplayItem.PreviewSource.ErrorScreen);
            LoadingIndicator =
                new DisplayItem(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\Loading.png"), DisplayItem.PreviewSource.ErrorScreen);
        }
    }

    public static async Task<(DisplayItem, bool)> GetFirstPreviewSpecialHandlingAsync(CanvasControl d2dCanvas,
        string path)
    {
        try
        {
            if (!File.Exists(path)) return (FileNotFoundIndicator, false);
            var cachedBmp = await PhotoDiskCacher.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                return (new DisplayItem(cachedBmp, DisplayItem.PreviewSource.FromDiskCache), true);
            }
            var extension = Path.GetExtension(path).ToUpper();
            if (extension == ".HEIC")
            {
                if (HeifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                return (PreviewFailedIndicator, false);
            }
            else if (extension == ".PSD")
            {
                if (await PsdReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                if (await PsdReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                return (PreviewFailedIndicator, false);
            }
            else if (extension == ".SVG")
            {
                if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                return (PreviewFailedIndicator, false);
            }
            else if (extension == ".GIF")
            {
                if (await GifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                return (PreviewFailedIndicator, false);
            }
            else if (extension == ".PNG")
            {
                if (await PngReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                return (PreviewFailedIndicator, false);
            }
            else
            {
                if (await WicReader.GetThumbnail(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
                return (PreviewFailedIndicator, false);
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
            var cachedBmp = await PhotoDiskCacher.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                return new DisplayItem(cachedBmp, DisplayItem.PreviewSource.FromDiskCache);
            }
            var extension = Path.GetExtension(path).ToUpper();
            if (extension == ".HEIC")
            {
                if (HeifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                return PreviewFailedIndicator;
            }
            else if (extension == ".PSD")
            {
                if (await PsdReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return PreviewFailedIndicator;
            }
            else if (extension == ".SVG")
            {
                if (await SvgReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return PreviewFailedIndicator;
            }
            else if (extension == ".GIF")
            {
                if (await GifReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return PreviewFailedIndicator;
            }
            else if (extension == ".PNG")
            {
                if (await PngReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return PreviewFailedIndicator;
            }
            else
            {
                if (await WicReader.GetThumbnail(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
                return PreviewFailedIndicator;
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
            if (IsMemoryLeakingFormat(path))
            {
                if (await WicReader.GetHqThruExternalProcess(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return HqImageFailedIndicator;
            }
            else if (Path.GetExtension(path).ToUpper() == ".PSD")
            {
                if (await PsdReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return HqImageFailedIndicator;
            }
            else if (Path.GetExtension(path).ToUpper() == ".SVG")
            {
                if (await SvgReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return HqImageFailedIndicator;
            }
            else if (Path.GetExtension(path).ToUpper() == ".GIF")
            {
                if (await GifReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return HqImageFailedIndicator;
            }
            else if (Path.GetExtension(path).ToUpper() == ".PNG")
            {
                if (await PngReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return HqImageFailedIndicator;
            }
            else
            {
                if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                return HqImageFailedIndicator;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return HqImageFailedIndicator;
        }
    }

    public static bool IsMemoryLeakingFormat(string path)
    {
        var fileExt = Path.GetExtension(path).ToUpperInvariant();
        return Util.MemoryLeakingExtensions.Contains(fileExt);
    }

    public static DisplayItem GetLoadingIndicator()
    {
        return LoadingIndicator;
    }
}