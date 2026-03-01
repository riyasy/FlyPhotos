using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using FlyPhotos.Services;

namespace FlyPhotos.Display.ImageReading;

internal static class PngReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int PngChunkHeaderSize = 8; // 4-byte Length + 4-byte Type
    private const int PngChunkCrcSize = 4;

    // Pre-calculated byte arrays for chunk type comparisons
    private static readonly byte[] ChunkAcTL = "acTL"u8.ToArray();
    private static readonly byte[] ChunkIDAT = "IDAT"u8.ToArray();
    private static readonly byte[] ChunkIEND = "IEND"u8.ToArray();

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
            Logger.Error(ex, "PngReader - GetFirstFrameFullSize failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
            var firstFrame = await CanvasBitmap.LoadAsync(ctrl, stream);
            if (await IsAnimatedPngAsync(stream)) // Animated PNG
            {
                var bytes = await StorageOps.GetInMemByteArray(stream);
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
    /// Detects an animated PNG (APNG) by reading the first 4 KB of the file in a single call
    /// and scanning chunk headers in memory. The 'acTL' animation-control chunk always appears
    /// before the first 'IDAT' chunk in a valid APNG, so 4 KB covers the entire searchable area.
    /// This replaces the previous per-chunk ReadAsync loop that incurred COM marshaling overhead
    /// on every chunk.
    /// </summary>
    private static async Task<bool> IsAnimatedPngAsync(IRandomAccessStream stream)
    {
        try
        {
            stream.Seek(0);

            // Single read covers the PNG signature + all pre-IDAT chunk headers,
            // which are always well under 4 KB in practice.
            const uint readSize = 4096;
            var buffer = new byte[Math.Min(readSize, stream.Size)];
            var read = await stream.ReadAsync(buffer.AsBuffer(), (uint)buffer.Length, InputStreamOptions.None);
            if (read.Length < 8) return false;

            var data = buffer.AsSpan(0, (int)read.Length);

            // Verify PNG signature
            if (!data[..8].SequenceEqual(PngSignature)) return false;

            int offset = 8;
            while (offset + PngChunkHeaderSize <= data.Length)
            {
                uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
                var chunkType = data.Slice(offset + 4, 4);

                if (chunkType.SequenceEqual(ChunkAcTL)) return true;  // Animation control → APNG
                if (chunkType.SequenceEqual(ChunkIDAT)) return false;  // Image data before acTL → static
                if (chunkType.SequenceEqual(ChunkIEND)) return false;  // End of file

                // Advance past: length(4) + type(4) + data(chunkLength) + CRC(4)
                offset += PngChunkHeaderSize + (int)chunkLength + PngChunkCrcSize;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not determine if PNG is animated. Assuming not.");
            return false;
        }
    }
}