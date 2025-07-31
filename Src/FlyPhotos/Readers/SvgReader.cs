using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using SkiaSharp;
using Svg.Skia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace FlyPhotos.Readers;

internal class SvgReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
        => LoadSvg(ctrl, inputPath, 800, 800);

    public static Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
        => LoadSvg(ctrl, inputPath, 2000, 2000);

    private static async Task<(bool, DisplayItem)> LoadSvg(CanvasControl ctrl, string inputPath, int width, int height)
    {
        try
        {
            // Load and parse SVG file
            var svg = new SKSvg();
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

            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, null);
        }
    }
}
