﻿#nullable enable
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using PhotoSauce.MagicScaler;
using System;
using System.Buffers;
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

    public static async Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, inputPath);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    public static async Task<(bool, DisplayItem)> GetHqThruExternalProcess(CanvasControl ctrl, string inputPath)
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
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk, rotation));
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
                MagicImageProcessor.ProcessImage(inputPath, ms, new ProcessImageSettings { Width = 800, HybridMode = HybridScaleMode.Turbo});
                var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, ms.AsRandomAccessStream());
                return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, DisplayItem.Empty());
            }
        }
    }

    public static async Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var preview = await decoder.GetPreviewAsync();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    public static async Task<(bool, DisplayItem)> GetThumbnail(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var preview = await decoder.GetThumbnailAsync();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception)
        {
            //Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    public static async Task<(bool, DisplayItem)> GetFileThumbnail(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.PicturesView);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, thumbnail);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    private static async Task<int> GetRotationFromMetaData(BitmapPropertiesView bmpProps)
    {
        var result = await bmpProps.GetPropertiesAsync(new[] { "System.Photo.Orientation" });
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
        string exePath;
        if (App.Packaged)
        {
            CopyWicReaderExeToLocalStorage().GetAwaiter().GetResult();
            var directory = ApplicationData.Current.LocalFolder.Path;
            exePath = $@"{directory}\WicImageFileReaderNative.exe";
        }
        else
        {
            exePath = Util.ExternalWicReaderPath;
        }

        var command = $"\"{exePath}\" \"{path}\" {mmfName} bgra";
        var p = new Process
        {
            StartInfo = new ProcessStartInfo("cmd", $"/c \"{command}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        p.Start();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static async Task CopyWicReaderExeToLocalStorage()
    {
        try
        {
            await ApplicationData.Current.LocalFolder.GetFileAsync("WicImageFileReaderNative.exe");
            return;
        }
        catch (FileNotFoundException)
        {
        }

        var stopfile =
            await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///WicImageFileReaderNative.exe"));
        await stopfile.CopyAsync(ApplicationData.Current.LocalFolder);
        Logger.Trace("Copied WicImageFileReaderNative.exe to local storage");
    }
}