using FlyPhotos.Data;
using FlyPhotos.Utils;
using NLog;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyPhotos.Readers;

internal class WpfWicReader
{
    private const string OrientationQuery = "System.Photo.Orientation";
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public static bool TryGetEmbeddedPreview(string path, int size, out Photo retBs)
    {
        retBs = null!;
        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            var bitmapFrame =
                BitmapFrame.Create(fileStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            bitmapFrame.Freeze();
            var bitmapMetadata = bitmapFrame.Metadata as BitmapMetadata;
            var angle = GetRotationAngleFromMetadata(bitmapMetadata);
            var bs = bitmapFrame.Thumbnail;
            if (bs == null) return false;

            if (size < bs.PixelWidth)
            {
                var scale = (double)size / bs.PixelWidth;
                bs = new TransformedBitmap(bs, new ScaleTransform(scale, scale));
            }

            if (angle != 0) bs = new TransformedBitmap(bs, new RotateTransform(angle));
            bs.Freeze();
            retBs = new Photo(bs);
            return retBs.Bitmap.PixelWidth >= 1;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }

    public static bool TryGetHqImageThruBitmapFrame(string path, out Photo photo)
    {
        photo = null!;
        try
        {
            BitmapFrame bitmapFrame;
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                bitmapFrame = BitmapFrame.Create(fileStream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                bitmapFrame.Freeze();
                fileStream.Close();
            }

            var bitmapMetadata = bitmapFrame.Metadata as BitmapMetadata;
            var angle = GetRotationAngleFromMetadata(bitmapMetadata);
            if (angle == 0)
            {
                photo = new Photo(bitmapFrame);
            }
            else
            {
                var rotatedBitmap = new TransformedBitmap(bitmapFrame, new RotateTransform(angle));
                rotatedBitmap.Freeze();
                photo = new Photo(rotatedBitmap);
            }

            return photo.Bitmap.PixelWidth >= 1;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return TryGetImageThruBmi(path, int.MaxValue, out photo);
        }
    }

    public static unsafe bool TryGetHqImageThruExternalDecoder(string path, out Photo photo)
    {
        photo = null!;
        try
        {
            var inFile = new FileStream(path, FileMode.Open, FileAccess.Read);
            var frame = BitmapFrame.Create(inFile, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            frame.Freeze();

            var stride = frame.PixelWidth * (frame.Format.BitsPerPixel / 8);
            var pixelsLength = frame.PixelHeight * stride;
            var pixels = new byte[pixelsLength];
            inFile.Close();
            inFile.Dispose();

            var bitmapMetadata = frame.Metadata as BitmapMetadata;
            var angle = GetRotationAngleFromMetadata(bitmapMetadata);

            var mmfName = Util.RandomString(10);
            using var mmf = MemoryMappedFile.CreateNew(mmfName, pixelsLength);

            if (!AskExternalWicReaderExeToCopyPixelsToMemoryMap(path, mmfName))
            {
                Logger.Error("External Wic Reader exe failed");
                return false;
            }

            //ExecFunction.Run(
            //    (string[] args) => WriteFramePixelsToMemoryMappedFile(args[0], args[1]),
            //    new string[] { path, mmfName });

            //WriteFramePixelsToMemoryMappedFile(path, mmfName);

            //ExecFunction.Run(
            //    (string[] args) => WriteFramePixelsToMemoryMappedFile2(args[0], args[1]),
            //    new string[] { path, mmfName });
            //WriteFramePixelsToMemoryMappedFile2(path, mmfName, false);

            const int offset = 0;
            using var accessor = mmf.CreateViewAccessor(offset, pixels.Length);
            var ptr = (byte*)0;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            Marshal.Copy(IntPtr.Add(new IntPtr(ptr), offset), pixels, 0, pixelsLength);
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();

            var bmp = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight,
                frame.DpiX, frame.DpiY, frame.Format, frame.Palette, pixels, stride);
            bmp.Freeze();

            if (angle != 0)
            {
                bmp = new TransformedBitmap(bmp, new RotateTransform(angle));
                bmp.Freeze();
            }

            photo = new Photo(bmp);
            return photo.Bitmap.PixelWidth >= 1;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }

    public static bool TryGetImageThruBmi(string path, int size, out Photo photo)
    {
        photo = null!;
        var rotation = Rotation.Rotate0;
        try
        {
            var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            var bitmapFrame = BitmapFrame.Create(fileStream, BitmapCreateOptions.None, BitmapCacheOption.None);

            if (bitmapFrame.Metadata is BitmapMetadata bitmapMetadata && bitmapMetadata.ContainsQuery(OrientationQuery))
            {
                var o = bitmapMetadata.GetQuery(OrientationQuery);
                if (o != null)
                    rotation = (ushort)o switch
                    {
                        6 => Rotation.Rotate90,
                        3 => Rotation.Rotate180,
                        8 => Rotation.Rotate270,
                        _ => rotation
                    };
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fileStream;
            if (size < bitmapFrame.PixelWidth) bmp.DecodePixelWidth = size;
            bmp.Rotation = rotation;
            bmp.EndInit();
            bmp.Freeze();
            fileStream.Close();
            fileStream.Dispose();

            photo = new Photo(bmp);
            return photo.Bitmap.PixelWidth >= 1;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }

    //private static void WriteFramePixelsToMemoryMappedFile2(string path, string mmfName)
    //{
    //    var msu = new ManagedShellUtility();
    //    msu.CopyImagePixelsToMemoryMap(path, mmfName, false);
    //}

    //private static unsafe void WriteFramePixelsToMemoryMappedFile(string path, string mmfName)
    //{
    //    var inFile = File.OpenRead(path);
    //    var decoder = BitmapDecoder.Create(inFile, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    //    var frame = decoder.Frames[0];
    //    frame.Freeze();

    //    var stride = frame.PixelWidth * (frame.Format.BitsPerPixel / 8);
    //    var pixelsLength = frame.PixelHeight * stride;
    //    var pixels = new byte[pixelsLength];
    //    frame.CopyPixels(pixels, stride, 0);
    //    inFile.Close();
    //    inFile.Dispose();

    //    // Create the memory-mapped file.
    //    const int offset = 0;
    //    using var mmf = MemoryMappedFile.OpenExisting(mmfName);
    //    using var accessor = mmf.CreateViewAccessor(offset, pixels.Length);
    //    var ptr = (byte*)0;
    //    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
    //    Marshal.Copy(pixels, 0, IntPtr.Add(new IntPtr(ptr), offset), pixels.Length);
    //    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
    //}

    private static bool AskExternalWicReaderExeToCopyPixelsToMemoryMap(string path, string mmfName)
    {
        var command = $"\"{Util.ExternalWicReaderPath}\" \"{path}\" {mmfName} bgr";
        var p = new Process();
        p.StartInfo = new ProcessStartInfo("cmd", $"/c \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        p.Start();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    public static BitmapSource ConvertBitmapToBitmapSource(Bitmap bmp)
    {
        var hbmp = bmp.GetHbitmap();
        var source = Imaging.CreateBitmapSourceFromHBitmap(
            hbmp,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(hbmp);
        source.Freeze();
        return source;
    }

    private static double GetRotationAngleFromMetadata(BitmapMetadata? bitmapMetadata)
    {
        double angle = 0;
        if (bitmapMetadata == null || !bitmapMetadata.ContainsQuery(OrientationQuery)) return angle;
        var o = bitmapMetadata.GetQuery(OrientationQuery);
        if (o == null) return angle;
        angle = (ushort)o switch
        {
            6 => 90,
            3 => 180,
            8 => 270,
            _ => angle
        };
        return angle;
    }
}