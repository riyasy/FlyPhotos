#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;

internal static class WicReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task<(bool, PreviewDisplayItem)> GetEmbedded(CanvasControl ctrl, string inputPath)
    {
        var (bmp, width, height) = await GetThumbnail(ctrl, inputPath);
        if (bmp == null) return (false, PreviewDisplayItem.Empty());

        var metadata = new ImageMetadata(width, height);
        return (true, new PreviewDisplayItem(bmp, Origin.Disk, metadata));
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            await using var fs = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var stream = fs.AsRandomAccessStream();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, HqDisplayItem.Empty());
        }
    }

    private static async Task<(CanvasBitmap? Bitmap, int Width, int Height)> GetThumbnail(CanvasControl ctrl, string inputPath)
    {
        try
        {
            await using var fs = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var stream = fs.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // Get the full, original dimensions from the decoder

            var rotation = await GetRotationFromMetaData(decoder.BitmapProperties);
            var verticalOrientation = rotation is 90 or 270;
            var originalWidth = verticalOrientation ? (int)decoder.PixelHeight : (int)decoder.PixelWidth;
            var originalHeight = verticalOrientation ? (int)decoder.PixelWidth : (int)decoder.PixelHeight;
            using var preview = await decoder.GetThumbnailAsync();
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, preview);

            // Return the raw parts for the caller to assemble
            return (canvasBitmap, originalWidth, originalHeight);
        }
        catch (Exception)
        {
            //Logger.Error(ex);
            return (null, 0, 0);
        }
    }

    private static async Task<int> GetRotationFromMetaData(BitmapPropertiesView bmpProps)
    {
        var propertiesToRetrieve = new[] { "System.Photo.Orientation" };
        var result = await bmpProps.GetPropertiesAsync(propertiesToRetrieve);

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
}