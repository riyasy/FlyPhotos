using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Readers;

internal static class GifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task<(bool, PreviewDisplayItem)> GetFirstFrameFullSize(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await ReaderUtil.GetWin2DPerformantStream(inputPath);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            var metaData = new ImageMetadata(canvasBitmap.SizeInPixels.Width, canvasBitmap.SizeInPixels.Height);
            return (true, new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "GifReader - GetFirstFrameFullSize failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await ReaderUtil.GetWin2DPerformantStream(inputPath);
            var firstFrame = await CanvasBitmap.LoadAsync(ctrl, stream);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);

            if (decoder.FrameCount > 1) // Animated GIF
            {
                var bytes = await ReaderUtil.GetInMemByteArray(stream);
                return (true, new AnimatedHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }
            else
            {
                return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process GIF file at {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}