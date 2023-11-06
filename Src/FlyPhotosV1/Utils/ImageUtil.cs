using System;
using System.IO;
using System.Windows.Media.Imaging;
using FlyPhotosV1.Data;
using FlyPhotosV1.Readers;
using NLog;
using Path = System.IO.Path;

namespace FlyPhotosV1.Utils;

internal class ImageUtil
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static Photo FileNotFoundIndicator;
    public static Photo PreviewFailedIndicator;
    public static Photo HqImageFailedIndicator;
    public static Photo LoadingIndicator;

    static ImageUtil()
    {
        FileNotFoundIndicator =
            new Photo(new BitmapImage(new Uri("pack://application:,,,/Resources/FileNotFound.png")));
        FileNotFoundIndicator.Bitmap.Freeze();
        PreviewFailedIndicator =
            new Photo(new BitmapImage(new Uri("pack://application:,,,/Resources/PreviewFailed.png")));
        PreviewFailedIndicator.Bitmap.Freeze();
        HqImageFailedIndicator =
            new Photo(new BitmapImage(new Uri("pack://application:,,,/Resources/HQImageFailed.png")));
        HqImageFailedIndicator.Bitmap.Freeze();
        LoadingIndicator = new Photo(new BitmapImage(new Uri("pack://application:,,,/Resources/Loading.png")));
        LoadingIndicator.Bitmap.Freeze();
    }

    public static Photo GetFirstPreviewSpecialHandling(string path, out bool continueLoadingHq)
    {
        continueLoadingHq = true;
        try
        {
            if (!File.Exists(path))
            {
                continueLoadingHq = false;
                return FileNotFoundIndicator;
            }

            var extension = Path.GetExtension(path).ToUpper();
            if (extension == ".HEIC")
            {
                if (LibHeifSharpReader.TryGetEmbeddedPreview(path, out var photo))
                    return photo;
                if (WpfWicReader.TryGetImageThruBmi(path, int.MaxValue, out photo))
                {
                    continueLoadingHq = false;
                    return photo;
                }

                return PreviewFailedIndicator;
            }
            else
            {
                if (WpfWicReader.TryGetEmbeddedPreview(path, int.MaxValue, out var photo))
                    return photo;
                if (WpfWicReader.TryGetHqImageThruBitmapFrame(path, out photo))
                {
                    continueLoadingHq = false;
                    return photo;
                }

                return PreviewFailedIndicator;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return PreviewFailedIndicator;
        }
    }

    public static Photo GetPreview(string path)
    {
        if (!File.Exists(path)) return FileNotFoundIndicator;
        try
        {
            var extension = Path.GetExtension(path).ToUpper();
            if (extension == ".HEIC")
            {
                if (LibHeifSharpReader.TryGetEmbeddedPreview(path, out var photo))
                    return photo;
                if (WpfWicReader.TryGetImageThruBmi(path, 300, out photo))
                    return photo;
                return PreviewFailedIndicator;
            }
            else
            {
                if (WpfWicReader.TryGetEmbeddedPreview(path, 300, out var photo))
                    return photo;
                if (WpfWicReader.TryGetImageThruBmi(path, 300, out photo))
                    return photo;
                return PreviewFailedIndicator;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return PreviewFailedIndicator;
        }
    }

    public static Photo GetHqImage(string path)
    {
        if (!File.Exists(path)) return FileNotFoundIndicator;
        try
        {
            if (IsMemoryLeakingFormat(path))
            {
                if (WpfWicReader.TryGetHqImageThruExternalDecoder(path, out var photo)) return photo;
                return HqImageFailedIndicator;
            }
            else
            {
                var extension = Path.GetExtension(path).ToUpper();
                if (extension == ".HEIC")
                {
                    if (WpfWicReader.TryGetImageThruBmi(path, int.MaxValue, out var photo)) return photo;
                }
                else
                {
                    if (WpfWicReader.TryGetHqImageThruBitmapFrame(path, out var photo)) return photo;
                }

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