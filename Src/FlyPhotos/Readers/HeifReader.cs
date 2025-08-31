using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using FlyPhotos.Data;
using LibHeifSharp;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;

internal class HeifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    static HeifReader()
    {
        LibHeifSharpDllImportResolver.Register();
    }

    public static (bool, PreviewDisplayItem) GetPreview(CanvasControl ctrl, string inputPath)
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

            if (primaryImage == null)
            {
                Logger.Warn($"No primary image found to get a preview from in: {inputPath}");
                return (false, PreviewDisplayItem.Empty());
            }

            var previewImageIds = primaryImage.GetThumbnailImageIds();

            if (previewImageIds.Count <= 0)
                return (false, PreviewDisplayItem.Empty());

            using var previewImageHandle = primaryImage.GetThumbnailImage(previewImageIds[0]);
            var retBmp = CreateBitmapSource(ctrl, previewImageHandle, decodingOptions);
            var metaData = new ImageMetadata(primaryImage.Width, primaryImage.Height);
            return (retBmp.Bounds.Width >= 1, new PreviewDisplayItem(retBmp, PreviewSource.FromDisk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to decode preview from: {inputPath}");
            return (false, PreviewDisplayItem.Empty());
        }
    }

    // New GetHq method
    public static (bool, HqDisplayItem) GetHq(CanvasControl ctrl, string inputPath)
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
            using var primaryImageHandle = context.GetPrimaryImageHandle();

            // Check if a primary image was found.
            if (primaryImageHandle == null)
            {
                Logger.Warn($"No primary image found in HEIF file: {inputPath}");
                return (false, HqDisplayItem.Empty());
            }

            // Directly decode the primary image handle, not a thumbnail.
            var retBmp = CreateBitmapSource(ctrl, primaryImageHandle, decodingOptions);
            return (retBmp.Bounds.Width >= 1, new StaticHqDisplayItem(retBmp));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to decode HQ image from: {inputPath}");
            return (false, HqDisplayItem.Empty());
        }
    }


    private static CanvasBitmap CreateBitmapSource(ICanvasResourceCreator ctrl, HeifImageHandle imageHandle,
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

        switch (chroma)
        {
            case HeifChroma.InterleavedRgb24:
                retBs = CreateEightBitImageWithoutAlpha(ctrl, image);
                break;
            case HeifChroma.InterleavedRgba32:
                // Note: The sample file you provided has logic to handle premultiplied alpha.
                // For simplicity, this implementation assumes non-premultiplied.
                // You can add the de-multiplication logic if you encounter visual artifacts with transparent HEIFs.
                retBs = CreateEightBitImageWithAlpha(ctrl, image);
                break;
            //case HeifChroma.InterleavedRgb48BE:
            //case HeifChroma.InterleavedRgb48LE:
            //    outputImage = CreateSixteenBitImageWithoutAlpha(image);
            //    break;
            //case HeifChroma.InterleavedRgba64BE:
            //case HeifChroma.InterleavedRgba64LE:
            //    outputImage = CreateSixteenBitImageWithAlpha(image, imageHandle.IsPremultipliedAlpha, imageHandle.BitDepth);
            //    break;
            default:
                throw new InvalidOperationException($"Unsupported Heif Chroma value: {chroma}");
        }

        return retBs;
    }

    // New helper method for RGBA images
    private static CanvasBitmap CreateEightBitImageWithAlpha(ICanvasResourceCreator ctrl, HeifImage heifImage)
    {
        var w = heifImage.Width;
        var h = heifImage.Height;

        var heifPlaneData = heifImage.GetPlane(HeifChannel.Interleaved);
        var srcScan0 = heifPlaneData.Scan0;
        var stride = heifPlaneData.Stride;
        var size = h * stride;

        var rgbaArray = ArrayPool<byte>.Shared.Rent(size);

        CanvasBitmap canvasBitmap;
        try
        {
            Marshal.Copy(srcScan0, rgbaArray, 0, size);

            // CanvasBitmap expects BGRA, but LibHeifSharp gives RGBA.
            // We need to swap the R and B channels.
            SwapRedAndBlue(rgbaArray, w, h, stride);

            canvasBitmap = CanvasBitmap.CreateFromBytes(ctrl, rgbaArray, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgbaArray, clearArray: false);
        }
        return canvasBitmap;
    }

    private static CanvasBitmap CreateEightBitImageWithoutAlpha(ICanvasResourceCreator ctrl, HeifImage heifImage)
    {
        var w = heifImage.Width;
        var h = heifImage.Height;

        var heifPlaneData = heifImage.GetPlane(HeifChannel.Interleaved);
        var srcScan0 = heifPlaneData.Scan0;

        var rgbSize = w * h * 3;
        var rgbaSize = w * h * 4;

        var rgbArray = ArrayPool<byte>.Shared.Rent(rgbSize);
        var bgraArray = ArrayPool<byte>.Shared.Rent(rgbaSize); // Changed name to reflect content

        CanvasBitmap canvasBitmap;
        try
        {
            // LibHeifSharp gives interleaved RGB data.
            Marshal.Copy(srcScan0, rgbArray, 0, rgbSize);
            // Convert to BGRA for CanvasBitmap.
            FastConvertRgbToBgra(w * h, rgbArray, bgraArray);
            canvasBitmap = CanvasBitmap.CreateFromBytes(ctrl, bgraArray, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgbArray, clearArray: false);
            ArrayPool<byte>.Shared.Return(bgraArray, clearArray: false);
        }
        return canvasBitmap;
    }

    private static void SwapRedAndBlue(byte[] data, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int colStart = x * 4;
                int R = rowStart + colStart + 0;
                int B = rowStart + colStart + 2;

                // Swap R and B
                (data[R], data[B]) = (data[B], data[R]);
            }
        }
    }

    // Updated to convert from RGB to BGRA for Win2D
    private static unsafe void FastConvertRgbToBgra(int pixelCount, byte[] rgbData, byte[] bgraData)
    {
        fixed (byte* rgbP = &rgbData[0], bgraP = &bgraData[0])
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int rgbOffset = i * 3;
                int bgraOffset = i * 4;

                bgraP[bgraOffset + 0] = rgbP[rgbOffset + 2]; // B
                bgraP[bgraOffset + 1] = rgbP[rgbOffset + 1]; // G
                bgraP[bgraOffset + 2] = rgbP[rgbOffset + 0]; // R
                bgraP[bgraOffset + 3] = 255;                 // A
            }
        }
    }
}