using FlyPhotos.Data;
using LibHeifSharp;
using NLog;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyPhotos.Readers;

internal class LibHeifSharpReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    static LibHeifSharpReader()
    {
        LibHeifSharpDllImportResolver.Register();
    }

    public static bool TryGetEmbeddedPreview(string inputPath, out Photo photo)
    {
        photo = null!;
        var decodingOptions = new HeifDecodingOptions
        {
            ConvertHdrToEightBit = false,
            Strict = false,
            DecoderId = null
        };

        try
        {
            using var context = new HeifContext(inputPath);
            using var primaryImage = context.GetPrimaryImageHandle();

            var previewImageIds = primaryImage.GetThumbnailImageIds();

            if (previewImageIds.Count <= 0)
                return false;

            using var previewImageHandle = primaryImage.GetThumbnailImage(previewImageIds[0]);
            var bmp = CreateBitmapSource(previewImageHandle, decodingOptions);
            photo = new Photo(bmp);
            return bmp.PixelWidth >= 1;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }

    private static BitmapSource CreateBitmapSource(HeifImageHandle imageHandle, HeifDecodingOptions decodingOptions)
    {
        BitmapSource retBs;
        HeifChroma chroma;
        var hasAlpha = imageHandle.HasAlphaChannel;
        var bitDepth = imageHandle.BitDepth;

        if (bitDepth == 8 || decodingOptions.ConvertHdrToEightBit)
        {
            chroma = hasAlpha ? HeifChroma.InterleavedRgba32 : HeifChroma.InterleavedRgb24;
        }
        else
        {
            // Use the native byte order of the operating system.
            if (BitConverter.IsLittleEndian)
                chroma = hasAlpha ? HeifChroma.InterleavedRgba64LE : HeifChroma.InterleavedRgb48LE;
            else
                chroma = hasAlpha ? HeifChroma.InterleavedRgba64BE : HeifChroma.InterleavedRgb48BE;
        }

        using var image = imageHandle.Decode(HeifColorspace.Rgb, chroma, decodingOptions);
        //var decodingWarnings = image.DecodingWarnings;
        //foreach (var item in decodingWarnings) Console.WriteLine("Warning: " + item);

        switch (chroma)
        {
            case HeifChroma.InterleavedRgb24:
                retBs = CreateEightBitImageWithoutAlpha(image);
                break;
            //case HeifChroma.InterleavedRgba32:
            //    outputImage = CreateEightBitImageWithAlpha(image, imageHandle.IsPremultipliedAlpha);
            //    break;
            //case HeifChroma.InterleavedRgb48BE:
            //case HeifChroma.InterleavedRgb48LE:
            //    outputImage = CreateSixteenBitImageWithoutAlpha(image);
            //    break;
            //case HeifChroma.InterleavedRgba64BE:
            //case HeifChroma.InterleavedRgba64LE:
            //    outputImage = CreateSixteenBitImageWithAlpha(image, imageHandle.IsPremultipliedAlpha, imageHandle.BitDepth);
            //    break;
            default:
                throw new InvalidOperationException("Unsupported Heif Chroma value.");
        }

        return retBs;
    }

    private static BitmapSource CreateEightBitImageWithoutAlpha(HeifImage heifImage)
    {
        var w = heifImage.Width;
        var h = heifImage.Height;
        var format = PixelFormats.Rgb24;
        const int channels = 3;

        var heifPlaneData = heifImage.GetPlane(HeifChannel.Interleaved);
        var srcScan0 = heifPlaneData.Scan0;
        var srcStride = heifPlaneData.Stride;

        var size = w * h * channels;
        var managedArray = new byte[w * h * channels];
        Marshal.Copy(srcScan0, managedArray, 0, size);

        var wbm = new WriteableBitmap(w, h, 96, 96, format, null);
        wbm.WritePixels(new Int32Rect(0, 0, w, h), managedArray, channels * w, 0);
        wbm.Freeze();
        return wbm;
    }
}