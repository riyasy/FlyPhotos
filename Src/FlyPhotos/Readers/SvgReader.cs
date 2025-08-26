using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;



namespace FlyPhotos.Readers;

internal class SvgReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
        => LoadSvgViaSkia(ctrl, inputPath, 800, 800);

    public static Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
        => LoadSvgViaSkia(ctrl, inputPath, 2000, 2000);

    private static async Task<(bool, DisplayItem)> LoadSvgViaSkia(CanvasControl ctrl, string inputPath, int width, int height)
    {
        try
        {
            // Load and parse SVG file
            using var svg = new SKSvg();
            svg.Load(inputPath);

            // Create a SkiaSharp bitmap to render into
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);

            // Calculate scaling
            var scaleX = width / svg.Picture.CullRect.Width;
            var scaleY = height / svg.Picture.CullRect.Height;
            var scale = Math.Min(scaleX, scaleY);

            canvas.Scale(scale);
            canvas.DrawPicture(svg.Picture);
            canvas.Flush();

            // Get image as PNG stream
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;

            // Load into CanvasBitmap
            var rasStream = ms.AsRandomAccessStream();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, rasStream);

            return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, null);
        }
    }



    //private static async Task<(bool, DisplayItem)> LoadSvgRawPixel(CanvasControl ctrl, string inputPath, int width, int height)
    //{
    //    try
    //    {
    //        var stopwatch = Stopwatch.StartNew();

    //        var svg = new SKSvg();
    //        svg.Load(inputPath);

    //        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
    //        using var surface = SKSurface.Create(imageInfo);
    //        var canvas = surface.Canvas;

    //        canvas.Clear(SKColors.Transparent);

    //        var scaleX = (float)width / svg.Picture.CullRect.Width;
    //        var scaleY = (float)height / svg.Picture.CullRect.Height;
    //        var scale = Math.Min(scaleX, scaleY);

    //        canvas.Scale(scale);
    //        canvas.DrawPicture(svg.Picture);
    //        canvas.Flush();

    //        // --- START OF THE CORRECTED FIX ---

    //        byte[] pixelBytes;
    //        // Get a pixmap which is a wrapper around the raw pixel data of the surface.
    //        // For a raster surface like this, it's a fast, zero-copy operation to get the pixmap.
    //        using (var pixmap = surface.PeekPixels())
    //        {
    //            // Get the pixel data as a read-only span and then copy it to a new byte array.
    //            // This is the one memory copy we need to make to move the data from Skia's
    //            // memory into a managed array that Win2D can consume.
    //            pixelBytes = pixmap.GetPixelSpan().ToArray();
    //        }

    //        // Create a CanvasBitmap directly from the raw byte array. This is a very fast memory copy.
    //        var canvasBitmap = CanvasBitmap.CreateFromBytes(
    //            ctrl,
    //            pixelBytes,
    //            width,
    //            height,
    //            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, // Must match SKColorType.Bgra8888
    //            96); // DPI

    //        // --- END OF THE CORRECTED FIX ---

    //        stopwatch.Stop();
    //        Debug.WriteLine($"[PERF] LoadSvgRawPixel took: {stopwatch.ElapsedMilliseconds} ms for '{Path.GetFileName(inputPath)}'");

    //        return (true, new DisplayItem(canvasBitmap, PreviewSource.FromDisk));
    //    }
    //    catch (Exception ex)
    //    {
    //        Logger.Error(ex);
    //        return (false, null);
    //    }
    //}
}
