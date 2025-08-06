using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FlyPhotos.Readers;

/// <summary>
/// Reads and decodes PNG/APNG files. For animated PNGs, it passes the raw file
/// data for a specialized control to handle animation. For static PNGs, it provides
/// a standard CanvasBitmap.
/// This reader correctly detects all APNG variants by scanning the entire file for the 'acTL' chunk.
/// NOTE: The consumer of the returned 'DisplayItem' (and its resources)
/// is responsible for disposing those resources to prevent VRAM leaks.
/// </summary>
internal class PngReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Standard 8-byte PNG file signature.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int PngChunkHeaderSize = 8; // 4-byte length + 4-byte type
    private const int PngChunkCrcSize = 4;

    public static async Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        // For a preview, we don't need to parse the file; just load the first frame.
        return await LoadStaticPngAsync(ctrl, inputPath);
    }

    public static async Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);

            if (await IsAnimatedPngAsync(stream))
            {
                // It's animated. Read the whole file from the stream.
                return await LoadApngFromStreamAsync(stream);
            }
            else
            {
                // It's a static PNG. Load it as a bitmap.
                stream.Seek(0);
                return await LoadStaticPngAsync(ctrl, stream);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process PNG/APNG file at {0}", inputPath);
            return (false, DisplayItem.Empty());
        }
    }

    // Private helper to load a static PNG from a stream
    private static async Task<(bool, DisplayItem)> LoadStaticPngAsync(ICanvasResourceCreator resourceCreator, IRandomAccessStream stream)
    {
        try
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(resourceCreator, stream);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load static PNG from stream.");
            return (false, DisplayItem.Empty());
        }
    }

    // Overload for convenience to load from a path (used by GetPreview)
    private static async Task<(bool, DisplayItem)> LoadStaticPngAsync(ICanvasResourceCreator resourceCreator, string path)
    {
        try
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(resourceCreator, path);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load static PNG from path {0}", path);
            return (false, DisplayItem.Empty());
        }
    }

    // Refactored to read from an existing stream to avoid re-opening the file.
    private static async Task<(bool, DisplayItem)> LoadApngFromStreamAsync(IRandomAccessStream stream)
    {
        try
        {
            // The stream may have been read already, so reset to the beginning.
            stream.Seek(0);

            // Read the entire stream into a byte array.
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);

            return (true, new DisplayItem(bytes, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read byte stream from APNG stream.");
            // Return DisplayItem.Empty() for consistency.
            return (false, DisplayItem.Empty());
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

                if (chunkType == "acTL") return true; // Animation Control Chunk found.
                if (chunkType == "IEND") return false; // End of image, not animated.

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