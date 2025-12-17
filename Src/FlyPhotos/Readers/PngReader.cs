using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace FlyPhotos.Readers;

internal static class PngReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int PngChunkHeaderSize = 8; // 4 byte Length + 4 byte Type
    private const int PngChunkCrcSize = 4;

    // Pre-calculated byte arrays for chunk types to avoid string allocation
    private static readonly byte[] ChunkAcTL = [(byte)'a', (byte)'c', (byte)'T', (byte)'L'];
    private static readonly byte[] ChunkIDAT = [(byte)'I', (byte)'D', (byte)'A', (byte)'T'];
    private static readonly byte[] ChunkIEND = [(byte)'I', (byte)'E', (byte)'N', (byte)'D'];

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
            Logger.Error(ex, "PngReader - GetFirstFrameFullSize failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await ReaderUtil.GetWin2DPerformantStream(inputPath);
            var firstFrame = await CanvasBitmap.LoadAsync(ctrl, stream);
            if (await IsAnimatedPngAsync(stream)) // Animated PNG
            {
                var bytes = await ReaderUtil.GetInMemByteArray(stream);
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



    private static async Task<bool> IsAnimatedPngAsync(IRandomAccessStream stream)
    {
        try
        {
            stream.Seek(0);

            var signatureBuffer = new byte[8];
            // ReadAsync returns IBuffer, we check Length property of result or rely on await behavior
            var readBuffer = await stream.ReadAsync(signatureBuffer.AsBuffer(), 8, InputStreamOptions.None);
            if (readBuffer.Length < 8) return false;

            // 1. Verify PNG Signature
            // Note: WindowsRuntimeBuffer extensions (AsBuffer) write directly to the underlying byte[]
            if (!SpanEquals(signatureBuffer, PngSignature)) return false;

            var chunkHeaderBuffer = new byte[PngChunkHeaderSize];

            // 2. Loop through chunks
            while (stream.Position < stream.Size)
            {
                var chunkRead = await stream.ReadAsync(chunkHeaderBuffer.AsBuffer(), (uint)PngChunkHeaderSize, InputStreamOptions.None);
                if (chunkRead.Length < PngChunkHeaderSize) break;

                // Parse Length (Bytes 0-3) - Handle Endianness
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(chunkHeaderBuffer, 0, 4);

                var chunkLength = BitConverter.ToUInt32(chunkHeaderBuffer, 0);

                // Check Type (Bytes 4-7)
                ReadOnlySpan<byte> typeSpan = chunkHeaderBuffer.AsSpan(4, 4);

                if (typeSpan.SequenceEqual(ChunkAcTL)) return true; // Found Animation Control
                // If we hit 'IDAT' (Image Data) and haven't found 'acTL', it's not animated.
                if (typeSpan.SequenceEqual(ChunkIDAT)) return false;
                if (typeSpan.SequenceEqual(ChunkIEND)) return false;

                // Seek past the chunk's data and CRC (4 bytes)
                // IRandomAccessStream.Seek takes a ulong absolute position
                var nextPosition = stream.Position + chunkLength + PngChunkCrcSize;
                if (nextPosition > stream.Size) break;
                stream.Seek(nextPosition);
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not determine if PNG is animated. Assuming not.");
            return false;
        }
    }

    private static bool SpanEquals(byte[] buffer, byte[] target)
    {
        return buffer.AsSpan().SequenceEqual(target);
    }
}