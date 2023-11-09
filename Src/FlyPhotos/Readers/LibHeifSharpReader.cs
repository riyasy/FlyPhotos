using FlyPhotos.Data;
using LibHeifSharp;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Readers;

internal class LibHeifSharpReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    static LibHeifSharpReader()
    {
        LibHeifSharpDllImportResolver.Register();
    }

    public static (bool, Photo) GetPreview(CanvasControl ctrl, string inputPath)
    {
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
                return (false, null);

            using var previewImageHandle = primaryImage.GetThumbnailImage(previewImageIds[0]);
            var retBmp = CreateBitmapSource(ctrl, previewImageHandle, decodingOptions);
            return (retBmp.Bounds.Width >= 1, new Photo(retBmp));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, null);
        }
    }

    private static CanvasBitmap CreateBitmapSource(CanvasControl ctrl, HeifImageHandle imageHandle,
        HeifDecodingOptions decodingOptions)
    {
        CanvasBitmap retBs;
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
                retBs = CreateEightBitImageWithoutAlpha(ctrl, image);
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

    private static CanvasBitmap CreateEightBitImageWithoutAlpha(ICanvasResourceCreator ctrl, HeifImage heifImage)
    {
        var w = heifImage.Width;
        var h = heifImage.Height;

        var heifPlaneData = heifImage.GetPlane(HeifChannel.Interleaved);
        var srcScan0 = heifPlaneData.Scan0;

        var rgbSize = w * h * 3;
        var rgbaSize = w * h * 4;
        var rgbArray = new byte[rgbSize];
        var rgbAArray = new byte[rgbaSize];
        Marshal.Copy(srcScan0, rgbArray, 0, rgbSize);
        FastConvert(w * h, rgbArray, rgbAArray);
        var canvasBitmap =
            CanvasBitmap.CreateFromBytes(ctrl, rgbAArray, w, h, DirectXPixelFormat.R8G8B8A8UIntNormalized);
        return canvasBitmap;
    }

    // TODO move to CLI Wrappter
    private static unsafe void FastConvert(int pixelCount, byte[] rgbData, byte[] rgbaData)
    {
        fixed (byte* rgbP = &rgbData[0], rgbaP = &rgbaData[0])
        {
            for (long i = 0, offsetRgb = 0; i < pixelCount; i++, offsetRgb += 3)
                ((uint*)rgbaP)[i] = *(uint*)(rgbP + offsetRgb) | 0xff000000;
        }
    }
}