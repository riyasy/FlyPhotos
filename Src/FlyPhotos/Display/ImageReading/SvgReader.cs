#nullable enable
using System;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using SkiaSharp;
using Svg.Skia;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Display.ImageReading;

internal static class SvgReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static (bool, PreviewDisplayItem) GetResized(CanvasControl ctrl, string inputPath)
    {
        var (bmp, width, height) = LoadSvgViaSkia(ctrl, inputPath, 800);
        if (bmp == null) return (false, PreviewDisplayItem.Empty());

        // Scale the rendered 800px dimensions up to what the HQ render would be,
        // so the metadata reflects the full-resolution SVG size.
        int maxCurrent = Math.Max(width, height);
        double scale = 2000.0 / maxCurrent;
        int metaWidth = (int)Math.Round(width * scale);
        int metaHeight = (int)Math.Round(height * scale);
        var metadata = new ImageMetadata(metaWidth, metaHeight);

        return (true, new PreviewDisplayItem(bmp, Origin.Disk, metadata));
    }

    public static (bool, HqDisplayItem) GetHq(CanvasControl ctrl, string inputPath)
    {
        var (bmp, _, _) = LoadSvgViaSkia(ctrl, inputPath, 2000);
        if (bmp == null) return (false, HqDisplayItem.Empty());
        return (true, new StaticHqDisplayItem(bmp, Origin.Disk));
    }

    private static (CanvasBitmap? Bitmap, int Width, int Height) LoadSvgViaSkia(
        CanvasControl ctrl, string inputPath, int maxDimension)
    {
        try
        {
            // Load and parse SVG file
            using var svg = new SKSvg();
            svg.Load(inputPath);

            // Get the original dimensions from the SVG's viewbox/content
            if (svg.Picture == null)
            {
                Logger.Warn($"Failed to load SVG (no picture): {inputPath}");
                return (null, 0, 0);
            }

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

            // Render SVG directly into an SKBitmap (Bgra8888 premultiplied).
            // This avoids the previous SKSurface → Snapshot → PNG encode → MemoryStream → PNG decode
            // round-trip: two full-frame codec passes and a MemoryStream allocation are eliminated.
            // SKBitmap.Bytes in Bgra8888/Premul maps exactly to DirectX B8G8R8A8UIntNormalized.
            var imageInfo = new SKImageInfo(renderWidth, renderHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(imageInfo);
            using var skCanvas = new SKCanvas(bitmap);

            skCanvas.Clear(SKColors.Transparent);
            skCanvas.Scale(renderWidth / svgWidth);
            skCanvas.DrawPicture(svg.Picture);
            skCanvas.Flush();

            var canvasBitmap = CanvasBitmap.CreateFromBytes(
                ctrl,
                bitmap.Bytes,
                renderWidth,
                renderHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);

            return (canvasBitmap, renderWidth, renderHeight);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load SVG: {inputPath}");
            return (null, 0, 0);
        }
    }
}
