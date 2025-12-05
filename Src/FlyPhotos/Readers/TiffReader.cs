using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;

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
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            stream.Seek(0);

            var firstFrameBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            var metaData = new ImageMetadata(firstFrameBitmap.SizeInPixels.Width, firstFrameBitmap.SizeInPixels.Height);

            return (true, new PreviewDisplayItem(firstFrameBitmap, Origin.Disk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "TiffReader - GetFirstFrameFullSize failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Returns either a StaticHqDisplayItem for single-page TIFFs or a MultiPageHqDisplayItem for multi-page TIFFs.
    /// The MultiPageHqDisplayItem contains the original file bytes so renderers can decode individual pages on demand.
    /// </summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            ulong frameCount = decoder.FrameCount;

            // Rewind and read all bytes
            stream.Seek(0);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);

            if (frameCount <= 1)
            {
                // Single page TIFF: return StaticHqDisplayItem
                using var ms = new InMemoryRandomAccessStream();
                using (var outStream = ms.GetOutputStreamAt(0))
                using (var writer = new DataWriter(outStream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await outStream.FlushAsync();
                }
                ms.Seek(0);
                var bmp = await CanvasBitmap.LoadAsync(ctrl, ms);
                return (true, new StaticHqDisplayItem(bmp, Origin.Disk));
            }
            else
            {
                // Multi-page TIFF: load first page for immediate display and return MultiPageHqDisplayItem with bytes
                using var ms = new InMemoryRandomAccessStream();
                using (var outStream = ms.GetOutputStreamAt(0))
                using (var writer = new DataWriter(outStream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await outStream.FlushAsync();
                }
                ms.Seek(0);
                var firstFrame = await CanvasBitmap.LoadAsync(ctrl, ms);
                return (true, new MultiPageHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "TiffReader - GetHq failed for {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}
