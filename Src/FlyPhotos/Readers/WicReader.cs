#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using PhotoSauce.MagicScaler;
using Path = System.IO.Path;

namespace FlyPhotos.Readers;

internal class WicReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    static WicReader()
    {
        CodecManager.Configure(codecs =>
        {
            codecs.Clear();
            codecs.UseWicCodecs(WicCodecPolicy.All);
        });
    }

    public static async Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        return await GetThumbnail(ctrl, inputPath);
    }

    public static async Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        if (IsMemoryLeakingFormat(inputPath))
            return await GetHqThruExternalProcess(ctrl, inputPath);

        try
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, inputPath);
            return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    public static async Task<(bool, DisplayItem)> GetHqDownScaled(CanvasControl ctrl, string inputPath)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return await Task.Run(Action);
        return await Action();

        async Task<(bool, DisplayItem)> Action()
        {
            try
            {
                using var ms = new MemoryStream();
                MagicImageProcessor.ProcessImage(inputPath, ms, new ProcessImageSettings { Width = 800, HybridMode = HybridScaleMode.Turbo });
                var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, ms.AsRandomAccessStream());
                return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, DisplayItem.Empty());
            }
        }
    }

    private static async Task<(bool, DisplayItem)> GetHqThruExternalProcess(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var rotation = await GetRotationFromMetaData(decoder.BitmapProperties);
            var w = (int)decoder.PixelWidth;
            var h = (int)decoder.PixelHeight;
            if (!CreateMemoryMapAndGetDataFromExternalProcess(inputPath, w, h, out var rgbAArray))
                return (false, DisplayItem.Empty());
            var canvasBitmap =
                CanvasBitmap.CreateFromBytes(ctrl, rgbAArray, w, h,
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk, rotation));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    private static async Task<(bool, DisplayItem)> GetThumbnail(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var preview = await decoder.GetThumbnailAsync();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
            return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
        }
        catch (Exception)
        {
            //Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    private static async Task<(bool, DisplayItem)> GetFilePreview(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var preview = await decoder.GetPreviewAsync();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
            return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    private static async Task<(bool, DisplayItem)> GetFileThumbnail(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.PicturesView);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, thumbnail);
            return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    private static async Task<int> GetRotationFromMetaData(BitmapPropertiesView bmpProps)
    {
        var propertiesToRetrieve = new[] { "System.Photo.Orientation" };
        var result = await bmpProps.GetPropertiesAsync(propertiesToRetrieve);

        if (result.Count <= 0) return 0;
        var orientation = result.Values.First();
        var rotation = (ushort)orientation.Value switch
        {
            6 => 90,
            3 => 180,
            8 => 270,
            _ => 0
        };
        return rotation;
    }

    private static unsafe bool CreateMemoryMapAndGetDataFromExternalProcess(string inputPath, int w, int h,
        out byte[] rgbAArray)
    {
        const int channels = 4;
        var rgbaSize = w * h * channels;
        rgbAArray = new byte[rgbaSize];
        var mmfName = Util.RandomString(10);
        using var mmf = MemoryMappedFile.CreateNew(mmfName, rgbaSize);
        if (!AskExternalWicReaderExeToCopyPixelsToMemoryMap(inputPath, mmfName))
        {
            Logger.Error("External Wic Reader exe failed");
            return false;
        }

        const int offset = 0;
        using var accessor = mmf.CreateViewAccessor(offset, rgbaSize);
        var ptr = (byte*)0;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        Marshal.Copy(IntPtr.Add(new IntPtr(ptr), offset), rgbAArray, 0, rgbaSize);
        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        return true;
    }

    private static bool AskExternalWicReaderExeToCopyPixelsToMemoryMap(string path, string mmfName)
    {
        var exePath = PathResolver.GetExternalWicReaderExePath();
        if (App.Packaged && !File.Exists(exePath))
        {
            CopyWicReaderExeToLocalStorageOnFirstUse().GetAwaiter().GetResult();
        }

        // A better way to create a Process object
        var processStartInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"\"{path}\" {mmfName} bgra",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = new Process();
        p.StartInfo = processStartInfo;
        p.Start();
        p.WaitForExit(); // You might want to add a timeout here for robustness
        return p.ExitCode == 0;
    }

    private static async Task CopyWicReaderExeToLocalStorageOnFirstUse()
    {
        //try
        //{
        //    await ApplicationData.Current.LocalFolder.GetFileAsync("WicImageFileReaderNative.exe");
        //    return;
        //}
        //catch (FileNotFoundException)
        //{
        //}
        var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///WicImageFileReaderNative.exe"));
        await file.CopyAsync(PathResolver.GetExternalWicReaderExeCopyFolderForPackagedApp());
        Logger.Trace("Copied WicImageFileReaderNative.exe to local storage");
    }

    private static bool IsMemoryLeakingFormat(string path)
    {
        var fileExt = Path.GetExtension(path).ToUpperInvariant();
        return Util.MemoryLeakingExtensions.Contains(fileExt);
    }
}