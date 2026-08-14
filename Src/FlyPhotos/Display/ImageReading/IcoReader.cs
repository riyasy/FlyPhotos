#nullable enable
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
/// A reader specifically for .ICO files to correctly handle their multi-frame nature.
/// A multi-frame icon is presented like a multi-page TIFF, with the frames ordered by decreasing
/// resolution, so the highest-resolution frame is what shows first. Previews stay single-frame.
/// </summary>
internal static class IcoReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets the highest-resolution image from an ICO file for preview purposes.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetPreview(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var order = await GetFramesLargestFirstAsync(decoder);

            var (bitmap, width, height) = await DecodeFrameAsync(ctrl, decoder, order[0]);
            var metadata = new ImageMetadata(width, height);
            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "IcoReader - GetPreview failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Opens an ICO file for high-quality display. A single-frame icon becomes a plain static item;
    /// a multi-frame one becomes a multi-page item whose pages run from the largest frame downwards.
    /// </summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var order = await GetFramesLargestFirstAsync(decoder);

            // Page 1 is the largest frame; it also sizes the initial view via Photo.GetActualSize().
            var (bitmap, _, _) = await DecodeFrameAsync(ctrl, decoder, order[0]);

            if (order.Length <= 1) return (true, new StaticHqDisplayItem(bitmap, Origin.Disk));

            // Seeking back to read the raw bytes is a simple memcpy - the renderer decodes the
            // remaining frames on demand from them.
            stream.Seek(0);
            var bytes = await StorageOps.GetInMemByteArray(stream);
            return (true, new MultiPageHqDisplayItem(bitmap, Origin.Disk, bytes, order));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "IcoReader - GetHq failed for {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Frame indices sorted by decreasing pixel area. Well-formed icons are already stored largest-first,
    /// so this is usually a no-op; it earns its place on icons built by merging two icon groups, whose
    /// directory runs e.g. 48, 32, 16, 256, 48, 32, 16. OrderByDescending is stable, so equal-area
    /// frames (e.g. the same size at different bit depths) keep their file order.
    /// </summary>
    private static async Task<int[]> GetFramesLargestFirstAsync(BitmapDecoder decoder)
    {
        var areas = new uint[decoder.FrameCount];
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            areas[i] = frame.PixelWidth * frame.PixelHeight;
        }
        return Enumerable.Range(0, areas.Length).OrderByDescending(i => areas[i]).ToArray();
    }

    /// <summary>Decodes one frame into a CanvasBitmap, returning it with its pixel dimensions.</summary>
    private static async Task<(CanvasBitmap bitmap, int width, int height)> DecodeFrameAsync(
        ICanvasResourceCreatorWithDpi ctrl, BitmapDecoder decoder, int frameIndex)
    {
        var frame = await decoder.GetFrameAsync((uint)frameIndex);

        var pixelProvider = await frame.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var canvasBitmap = CanvasBitmap.CreateFromBytes(
            ctrl,
            pixelProvider.DetachPixelData(),
            (int)frame.PixelWidth,
            (int)frame.PixelHeight,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

        return (canvasBitmap, (int)frame.PixelWidth, (int)frame.PixelHeight);
    }
}
