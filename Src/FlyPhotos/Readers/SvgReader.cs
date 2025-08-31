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

    public static async Task<(bool, PreviewDisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        var(bmp, width, height) = await LoadSvgViaSkia(ctrl, inputPath, 800);
        if (bmp == null) return (false, PreviewDisplayItem.Empty());
        var metadata = new ImageMetadata(width, height);
        return (true, new PreviewDisplayItem(bmp, PreviewSource.FromDisk, metadata));
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        var (bmp, _, _) = await LoadSvgViaSkia(ctrl, inputPath, 2000);
        if (bmp == null) return (false, HqDisplayItem.Empty());
        return (true, new StaticHqDisplayItem(bmp));
    }

    private static async Task<(CanvasBitmap Bitmap, int Width, int Height)> LoadSvgViaSkia(
        CanvasControl ctrl, string inputPath, int maxDimension)
    {
        try
        {
            // Load and parse SVG file
            using var svg = new SKSvg();
            svg.Load(inputPath);

            // Get the original dimensions from the SVG's viewbox/content
            float svgWidth = svg.Picture.CullRect.Width;
            float svgHeight = svg.Picture.CullRect.Height;

            // Prevent division by zero for invalid or empty SVGs
            if (svgWidth <= 0 || svgHeight <= 0)
            {
                Logger.Warn($"Invalid SVG dimensions for {inputPath}: {svgWidth}x{svgHeight}");
                return (null, 0, 0);
            }

            // --- ASPECT RATIO CALCULATION ---
            // Calculate the dimensions of the output bitmap while preserving aspect ratio.
            // The longest side (either width or height) will be set to maxDimension.
            int renderWidth;
            int renderHeight;

            if (svgWidth >= svgHeight)
            {
                // If the SVG is wider or square, the new width is the max dimension.
                renderWidth = maxDimension;
                // Calculate the new height to maintain the aspect ratio.
                renderHeight = (int)Math.Round(maxDimension * (svgHeight / svgWidth));
            }
            else
            {
                // If the SVG is taller, the new height is the max dimension.
                renderHeight = maxDimension;
                // Calculate the new width to maintain the aspect ratio.
                renderWidth = (int)Math.Round(maxDimension * (svgWidth / svgHeight));
            }

            // Create a SkiaSharp bitmap with the newly calculated dimensions
            using var surface = SKSurface.Create(new SKImageInfo(renderWidth, renderHeight));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);

            // --- SCALING CALCULATION ---
            // The canvas now has the same aspect ratio as the SVG, so we can calculate
            // a single scale factor to make the SVG fill the canvas perfectly.
            var scale = renderWidth / svgWidth;
            canvas.Scale(scale);

            // Draw the SVG picture onto the canvas
            canvas.DrawPicture(svg.Picture);
            canvas.Flush();

            // Get image as PNG stream
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;

            // Load into CanvasBitmap
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, ms.AsRandomAccessStream());

            // Return the final bitmap and its actual dimensions
            return (canvasBitmap, renderWidth, renderHeight);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load SVG: {inputPath}"); // Added context to logger
            return (null, 0, 0);
        }
    }
}
