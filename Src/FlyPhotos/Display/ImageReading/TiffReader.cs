using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Reads TIFF files and supports multi-page TIFFs. For single-page TIFFs it returns a static HQ display item.
/// For multi-page TIFFs it returns a MultiPageHqDisplayItem containing the original file bytes and
/// a first-frame CanvasBitmap for immediate display. Throws on failure.
/// </summary>
internal static class TiffReader
{
    public static async Task<PreviewDisplayItem> GetFirstFrameFullSize(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, await file.GetStreamAsync());
        var metaData = new ImageMetadata(canvasBitmap.SizeInPixels.Width, canvasBitmap.SizeInPixels.Height);
        return new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData);
    }

    public static async Task<HqDisplayItem> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var stream = await file.GetStreamAsync();

        // One decoder, one stream read.
        // Frame 0 is decoded from the decoder we already have, into a SoftwareBitmap that Win2D
        // uploads directly, with no managed copy of the pixels.
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.TiffDecoderId, stream);
        using var softwareBitmap = await WicFrame.DecodeAsync(await decoder.GetFrameAsync(0));

        // A multi-page file's raw bytes for the page renderer: read after the decoder is done with the shared
        // stream, and before the GPU bitmap, so a failed read leaves none behind.
        var bytes = decoder.FrameCount > 1 ? await file.GetBytesAsync() : null;
        var firstFrame = softwareBitmap.ToCanvasBitmap(ctrl);

        if (bytes == null) return new StaticHqDisplayItem(firstFrame, Origin.Disk);

        // Pages are the frames, in file order.
        var pageOrder = Enumerable.Range(0, (int)decoder.FrameCount).ToArray();
        return new MultiPageHqDisplayItem(firstFrame, Origin.Disk, bytes, pageOrder);
    }
}
