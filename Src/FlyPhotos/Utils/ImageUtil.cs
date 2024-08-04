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

    public static Photo FileNotFoundIndicator;
    public static Photo PreviewFailedIndicator;
    public static Photo HqImageFailedIndicator;
    public static Photo LoadingIndicator;

    public static async Task Initialize(CanvasControl d2dCanvas)
    {
        if (App.Packaged)
        {
            FileNotFoundIndicator =
                new Photo(await CanvasBitmap.LoadAsync(d2dCanvas,
                    new Uri("ms-appx:///Assets/Images/FileNotFound.png")), Photo.PreviewSource.ErrorScreen);
            PreviewFailedIndicator =
                new Photo(
                    await CanvasBitmap.LoadAsync(d2dCanvas, new Uri("ms-appx:///Assets/Images/PreviewFailed.png")), Photo.PreviewSource.ErrorScreen);
            HqImageFailedIndicator =
                new Photo(
                    await CanvasBitmap.LoadAsync(d2dCanvas, new Uri("ms-appx:///Assets/Images/HQImageFailed.png")), Photo.PreviewSource.ErrorScreen);
            LoadingIndicator =
                new Photo(await CanvasBitmap.LoadAsync(d2dCanvas, new Uri("ms-appx:///Assets/Images/Loading.png")), Photo.PreviewSource.ErrorScreen);
        }
        else
        {
            var folderName = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location), "Assets\\Images");
            FileNotFoundIndicator =
                new Photo(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\FileNotFound.png"), Photo.PreviewSource.ErrorScreen);
            PreviewFailedIndicator =
                new Photo(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\PreviewFailed.png"), Photo.PreviewSource.ErrorScreen);
            HqImageFailedIndicator =
                new Photo(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\HQImageFailed.png"), Photo.PreviewSource.ErrorScreen);
            LoadingIndicator =
                new Photo(await CanvasBitmap.LoadAsync(d2dCanvas, $"{folderName}\\Loading.png"), Photo.PreviewSource.ErrorScreen);
        }
    }

    public static async Task<(Photo, bool)> GetFirstPreviewSpecialHandlingAsync(CanvasControl d2dCanvas,
        string path)
    {
        try
        {
            if (!File.Exists(path)) return (FileNotFoundIndicator, false);
            var cachedBmp = PhotoDiskCacher.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                return (new Photo(cachedBmp, Photo.PreviewSource.FromDiskCache), true);
            }
            var extension = Path.GetExtension(path).ToUpper();
            if (extension == ".HEIC")
            {
                if (LibHeifSharpReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return (retBmp, true);
                if (await WicReader.GetHq(d2dCanvas, path) is (true, { } retBmp2)) return (retBmp2, false);
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

    public static async Task<Photo> GetPreview(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) return FileNotFoundIndicator;
        try
        {
            var cachedBmp = PhotoDiskCacher.Instance.ReturnFromCache(d2dCanvas, path);
            if (null != cachedBmp)
            {
                return new Photo(cachedBmp, Photo.PreviewSource.FromDiskCache);
            }
            var extension = Path.GetExtension(path).ToUpper();
            if (extension == ".HEIC")
            {
                if (LibHeifSharpReader.GetPreview(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
                if (await WicReader.GetHqDownScaled(d2dCanvas, path) is (true, { } retBmp2)) return retBmp2;
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

    public static async Task<Photo> GetHqImage(CanvasControl d2dCanvas, string path)
    {
        if (!File.Exists(path)) return FileNotFoundIndicator;
        try
        {
            if (IsMemoryLeakingFormat(path))
            {
                if (await WicReader.GetHqThruExternalProcess(d2dCanvas, path) is (true, { } retBmp)) return retBmp;
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
}