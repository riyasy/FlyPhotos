using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;
internal static class PsdReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Get preview as a CanvasBitmap for WinUI
    public static async Task<(bool, PreviewDisplayItem)> GetEmbedded(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        if (GetPsdInfo(inputPath, out int width, out var height, out var thumbnailData))
        {
            try
            {
                using var stream = new MemoryStream(thumbnailData);
                CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(ctrl, stream.AsRandomAccessStream());
                var metaData = new ImageMetadata(width, height);
                return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metaData));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, PreviewDisplayItem.Empty());
            }
        }
        return (false, PreviewDisplayItem.Empty());
    }

    /// <summary>
    /// Extracts the main image dimensions (width, height) and the embedded JPEG thumbnail from a Photoshop
    /// (PSD/PSB) file. Only the header and the image-resources section are read, never the pixel data.
    /// </summary>
    /// <param name="inputFilePath">The path to the PSD file.</param>
    /// <param name="width">The width of the main image.</param>
    /// <param name="height">The height of the main image.</param>
    /// <param name="thumbnailData">The byte array of the embedded JPEG thumbnail, or null if not found.</param>
    /// <returns>True if a thumbnail was read; false otherwise.</returns>
    private static bool GetPsdInfo(string inputFilePath, out int width, out int height, out byte[] thumbnailData)
    {
        width = 0;
        height = 0;
        thumbnailData = null;

        try
        {
            using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return PsdThumbnail.TryRead(fileStream, out width, out height, out thumbnailData);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }

}
