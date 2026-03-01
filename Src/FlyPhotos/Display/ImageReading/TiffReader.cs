using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using FlyPhotos.Services;
using Windows.Graphics.DirectX;

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

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);

            // One decoder, one stream read.
            // GetPixelDataAsync extracts frame 0 pixels directly from the decoder we already
            // have — no second BitmapDecoder or second CanvasBitmap.LoadAsync needed.
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.TiffDecoderId, stream);
            var frame0 = await decoder.GetFrameAsync(0);
            var pixelProvider = await frame0.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var pixels = pixelProvider.DetachPixelData();

            // OrientedPixelWidth/Height account for any EXIF rotation applied above.
            var firstFrame = CanvasBitmap.CreateFromBytes(
                ctrl, pixels,
                (int)decoder.OrientedPixelWidth,
                (int)decoder.OrientedPixelHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);

            if (decoder.FrameCount > 1) // Multi-page TIFF
            {
                // Seeking back to read the raw bytes is a simple memcpy — far cheaper
                // than a second pixel decode pass.
                stream.Seek(0);
                var bytes = await StorageOps.GetInMemByteArray(stream);
                return (true, new MultiPageHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }

            return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "TiffReader - GetHq failed for {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}
