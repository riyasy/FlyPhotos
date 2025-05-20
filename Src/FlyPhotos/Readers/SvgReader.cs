using FlyPhotos.Data;
using ImageMagick;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
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

    // Load preview as a rasterized CanvasBitmap
    private static async Task<(bool, DisplayItem)> LoadSvg(CanvasControl ctrl, string inputPath, int width, int height)
    {
        try
        {
            var settings = new MagickReadSettings
            {
                Width = width,
                Height = height,
            };

            using var image = new MagickImage(inputPath, settings);
            using var stream = new MemoryStream();
            image.Format = MagickFormat.Jpeg;
            image.Quality = 80;
            await image.WriteAsync(stream);
            stream.Position = 0;

            var bitmap = await CanvasBitmap.LoadAsync(ctrl, stream.AsRandomAccessStream());
            return (true, new DisplayItem(bitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, null);
        }
    }

}
