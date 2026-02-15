using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Reads TIFF files and supports multi-page TIFFs. For single-page TIFFs it returns a static HQ display item.
/// For multi-page TIFFs it returns a MultiPageHqDisplayItem containing the original file bytes and
/// a first-frame CanvasBitmap for immediate display.
/// </summary>
internal static class TiffReader
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
            Logger.Error(ex, "TiffReader - GetFirstFrameFullSize failed for {0}", inputPath);
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
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.TiffDecoderId, stream);

            if (decoder.FrameCount > 1) // Multi-page TIFF
            {
                var bytes = await ReaderUtil.GetInMemByteArray(stream);
                return (true, new MultiPageHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }
            else
            {
                return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "TiffReader - GetHq failed for {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}
