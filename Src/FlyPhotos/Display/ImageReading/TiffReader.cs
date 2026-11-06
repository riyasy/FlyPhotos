using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using NLog;
using FlyPhotos.Services;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Reads TIFF files and supports multi-page TIFFs. For single-page TIFFs it returns a static HQ display item.
/// For multi-page TIFFs it returns a MultiPageHqDisplayItem containing the original file bytes and
/// a first-frame CanvasBitmap for immediate display.
/// </summary>
internal static class TiffReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task<(bool, PreviewDisplayItem)> GetFirstFrameFullSize(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
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

    public static async Task<(bool, HqDisplayItem)> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        try
        {
            var stream = await file.GetStreamAsync();

            // One decoder, one stream read.
            // Frame 0 is decoded from the decoder we already have, into a SoftwareBitmap that Win2D
            // uploads directly, with no managed copy of the pixels.
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.TiffDecoderId, stream);
            var frame0 = await decoder.GetFrameAsync(0);
            using var softwareBitmap = await frame0.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var firstFrame = softwareBitmap.ToCanvasBitmap(ctrl);

            if (decoder.FrameCount > 1) // Multi-page TIFF
            {
                // Reading the raw bytes is a simple memcpy of the file, far cheaper
                // than a second pixel decode pass.
                var bytes = await file.GetBytesAsync();
                // Pages are the frames, in file order.
                var pageOrder = Enumerable.Range(0, (int)decoder.FrameCount).ToArray();
                return (true, new MultiPageHqDisplayItem(firstFrame, Origin.Disk, bytes, pageOrder));
            }

            return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "TiffReader - GetHq failed for {0}", file.Path);
            return (false, HqDisplayItem.Empty());
        }
    }
}
