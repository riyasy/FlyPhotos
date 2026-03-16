using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using FlyPhotos.Core.Model;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
///     Handles reading and decoding of PNG image files (both static and animated APNG) into
///     display items suitable for rendering via Win2D.
/// </summary>
/// <remarks>
///     For static PNG, a <see cref="StaticHqDisplayItem" /> is returned containing a single
///     <see cref="CanvasBitmap" /> decoded by Win2D's built-in PNG loader.
///     For animated PNG (APNG), an <see cref="AnimatedHqDisplayItem" /> is returned, carrying
///     both the first decoded frame (for immediate display before animation begins) and the raw
///     file bytes (consumed by <see cref="FlyPhotos.Display.Animators.PngAnimator" /> for
///     frame-accurate playback).
///     Animation detection is performed by <see cref="IsAnimatedPngAsync" />, which scans only
///     the first 4 KB of the file for the APNG <c>acTL</c> chunk rather than performing a
///     second full decode pass.
/// </remarks>
internal static class PngReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Standard 8-byte PNG file signature, per PNG spec §5.2.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Size of a PNG chunk header: 4-byte data length + 4-byte type tag.</summary>
    private const int PngChunkHeaderSize = 8;

    /// <summary>Size of the CRC field that follows every PNG chunk's data.</summary>
    private const int PngChunkCrcSize = 4;

    // u8 literals produce stack-allocated ReadOnlySpan<byte> backed by PE read-only data —
    // zero heap allocation per access, replacing the previous byte[] fields that allocated
    // on class initialisation.

    /// <summary>APNG animation control chunk type tag. Its presence before any IDAT chunk identifies an APNG file.</summary>
    private static ReadOnlySpan<byte> ChunkAcTL => "acTL"u8;

    /// <summary>PNG image data chunk type tag. Its presence before acTL identifies a static PNG.</summary>
    private static ReadOnlySpan<byte> ChunkIDAT => "IDAT"u8;

    /// <summary>PNG end-of-file chunk type tag.</summary>
    private static ReadOnlySpan<byte> ChunkIEND => "IEND"u8;

    /// <summary>
    ///     Loads the first frame of the PNG file at full resolution as a lightweight preview.
    ///     Used during initial thumbnail/preview loading before the high-quality decode is ready.
    /// </summary>
    /// <param name="ctrl">The Win2D <see cref="CanvasControl" /> that owns the GPU device.</param>
    /// <param name="inputPath">Absolute path to the .png file on disk.</param>
    /// <returns>
    ///     A tuple of (<c>success</c>, <see cref="PreviewDisplayItem" />).
    ///     On failure, returns <c>(false, PreviewDisplayItem.Empty())</c> and logs the error.
    /// </returns>
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

    /// <summary>
    ///     Decodes the PNG file at full quality and returns either a static or animated display item.
    /// </summary>
    /// <param name="ctrl">The Win2D <see cref="CanvasControl" /> that owns the GPU device.</param>
    /// <param name="inputPath">Absolute path to the .png file on disk.</param>
    /// <returns>
    ///     A tuple of (<c>success</c>, <see cref="HqDisplayItem" />):
    ///     <list type="bullet">
    ///         <item><see cref="StaticHqDisplayItem" /> — for single-frame PNG files.</item>
    ///         <item>
    ///             <see cref="AnimatedHqDisplayItem" /> — for APNG files, carrying the first decoded
    ///             frame and the raw file bytes for the animator.
    ///         </item>
    ///     </list>
    ///     On failure, returns <c>(false, HqDisplayItem.Empty())</c> and logs the error.
    /// </returns>
    /// <remarks>
    ///     <c>CanvasBitmap.LoadAsync</c> decodes the full first frame in a single pass; the stream
    ///     is then scanned for the <c>acTL</c> chunk to determine whether the file is animated.
    ///     For animated files the stream is rewound and re-read as a raw byte array for the animator;
    ///     this is cheaper than a second full pixel decode pass.
    /// </remarks>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
            var firstFrame = await CanvasBitmap.LoadAsync(ctrl, stream);
            if (await IsAnimatedPngAsync(stream)) // Animated PNG
            {
                // IsAnimatedPngAsync leaves the stream at a mid-file position after scanning.
                // Seek to 0 before reading the full byte array for the animator.
                stream.Seek(0);
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
    ///     Detects whether the PNG stream contains an APNG animation control chunk (<c>acTL</c>),
    ///     indicating the file is an animated PNG.
    /// </summary>
    /// <param name="stream">
    ///     A seekable stream positioned anywhere within the file. The method resets it to
    ///     position 0 on entry and leaves the stream at whatever position the scan reached.
    /// </param>
    /// <returns>
    ///     <c>true</c> if an <c>acTL</c> chunk is found before any <c>IDAT</c> or <c>IEND</c>
    ///     chunk; <c>false</c> otherwise. Returns <c>false</c> on any read or parse error.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         <b>Why 4 KB?</b>
    ///         Per the APNG spec, the <c>acTL</c> chunk must appear before the first <c>IDAT</c>
    ///         chunk. In practice all pre-IDAT chunks (IHDR, cHRM, gAMA, iCCP, sRGB, bKGD, pHYs,
    ///         sPLT, tIME, acTL) fit comfortably within 4 KB. A single 4 KB read replaces the
    ///         previous per-chunk <c>ReadAsync</c> loop that incurred COM marshalling overhead on
    ///         every iteration.
    ///     </para>
    ///     <para>
    ///         <b>APNG spec reference:</b> The <c>acTL</c> chunk is defined in the APNG specification
    ///         §2.1 as a PNG ancillary chunk that appears before the first <c>IDAT</c> in an animated
    ///         file. A PNG file without an <c>acTL</c> chunk before its first <c>IDAT</c> is a
    ///         standard static PNG and must be treated as such by decoders.
    ///     </para>
    /// </remarks>
    private static async Task<bool> IsAnimatedPngAsync(IRandomAccessStream stream)
    {
        try
        {
            stream.Seek(0);

            // Single read covers the PNG signature + all pre-IDAT chunk headers,
            // which are always well under 4 KB in practice.
            const uint readSize = 4096;
            var buffer = new byte[(uint)Math.Min(readSize, stream.Size)];
            var read = await stream.ReadAsync(buffer.AsBuffer(), (uint)buffer.Length, InputStreamOptions.None);
            if (read.Length < 8) return false;

            var data = buffer.AsSpan(0, (int)read.Length);

            // Verify PNG signature.
            if (!data[..8].SequenceEqual(PngSignature)) return false;

            int offset = 8;
            while (offset + PngChunkHeaderSize <= data.Length)
            {
                uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
                var chunkType = data.Slice(offset + 4, 4);

                if (chunkType.SequenceEqual(ChunkAcTL)) return true; // Animation control → APNG
                if (chunkType.SequenceEqual(ChunkIDAT)) return false; // Image data before acTL → static
                if (chunkType.SequenceEqual(ChunkIEND)) return false; // End of file

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