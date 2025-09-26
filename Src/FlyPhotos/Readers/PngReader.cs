using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;

/// <summary>
/// Reads and decodes PNG/APNG files. For animated PNGs, it passes the raw file
/// data for a specialized control to handle animation. For static PNGs, it provides
/// a standard CanvasBitmap.
/// This reader correctly detects all APNG variants by scanning the entire file for the 'acTL' chunk.
/// NOTE: The consumer of the returned 'DisplayItem' (and its resources)
/// is responsible for disposing those resources to prevent VRAM leaks.
/// </summary>
internal static class PngReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Standard 8-byte PNG file signature.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int PngChunkHeaderSize = 8; // 4-byte length + 4-byte type
    private const int PngChunkCrcSize = 4;

    public static async Task<(bool, PreviewDisplayItem)> GetFirstFrameFullSize(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            var metaData = new ImageMetadata(canvasBitmap.SizeInPixels.Width, canvasBitmap.SizeInPixels.Height);
            return (true, new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);
            var firstFrame = await CanvasBitmap.LoadAsync(ctrl, stream);

            if (await IsAnimatedPngAsync(stream)) // Animated PNG
            {
                stream.Seek(0);
                var bytes = new byte[stream.Size];
                await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
                return (true, new AnimatedHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }
            else
            {
                return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process PNG/APNG file at {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Correctly and robustly checks if a PNG stream is an APNG by searching the entire file 
    /// for the 'acTL' (Animation Control) chunk.
    /// </summary>
    private static async Task<bool> IsAnimatedPngAsync(IRandomAccessStream stream)
    {
        try
        {
            stream.Seek(0);

            // 1. Verify PNG Signature
            var signatureBuffer = new byte[PngSignature.Length];
            await stream.ReadAsync(signatureBuffer.AsBuffer(), (uint)signatureBuffer.Length, InputStreamOptions.None);

            if (!signatureBuffer.SequenceEqual(PngSignature))
            {
                return false; // Not a valid PNG file.
            }

            var chunkHeaderBuffer = new byte[PngChunkHeaderSize];

            // 2. Loop through ALL chunks to find 'acTL'
            while (stream.Position < stream.Size)
            {
                await stream.ReadAsync(chunkHeaderBuffer.AsBuffer(), PngChunkHeaderSize, InputStreamOptions.None);

                // Manually parse chunk header, handling Big-Endian byte order for length.
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(chunkHeaderBuffer, 0, 4); // Convert length to Little-Endian
                }
                uint chunkLength = BitConverter.ToUInt32(chunkHeaderBuffer, 0);
                string chunkType = Encoding.ASCII.GetString(chunkHeaderBuffer, 4, 4);

                if (string.Equals(chunkType, "acTL")) return true; // Animation Control Chunk found.
                if (string.Equals(chunkType, "IEND")) return false; // End of image, not animated.

                // Seek past the chunk's data and its 4-byte CRC.
                stream.Seek(stream.Position + chunkLength + PngChunkCrcSize);
            }
            return false; // Reached end of file without finding IEND (malformed PNG).
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not determine if PNG is animated due to an error. Assuming not.");
            return false;
        }
    }
}