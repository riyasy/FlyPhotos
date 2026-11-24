#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;
internal static class PsdReader
{
    // Get preview as a CanvasBitmap for WinUI
    // Null when the file has no embedded thumbnail; throws on failure.
    public static async Task<PreviewDisplayItem?> GetEmbedded(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        if (!GetPsdInfo(inputPath, out int width, out var height, out var thumbnailData)) return null;

        using var stream = ClosableStream.Open(thumbnailData!);
        CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(ctrl, stream.Stream);
        var metaData = new ImageMetadata(width, height);
        return new PreviewDisplayItem(bitmap, Origin.Disk, metaData);
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
    private static bool GetPsdInfo(string inputFilePath, out int width, out int height, out byte[]? thumbnailData)
    {
        using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PsdThumbnail.TryRead(fileStream, out width, out height, out thumbnailData);
    }

}
