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
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static async Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, inputPath);
            return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load APNG preview from {0}", inputPath);
            return (false, DisplayItem.Empty());
        }
    }

    public static async Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(inputPath);
            using IRandomAccessStream stream = await storageFile.OpenAsync(FileAccessMode.Read);

            bool isAnimated = await IsAnimatedPngAsync(stream);

            if (isAnimated)
            {
                return await LoadApngAsFile(inputPath);
            }
            else
            {
                stream.Seek(0);
                var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                return (true, new DisplayItem(canvasBitmap, DisplayItem.PreviewSource.FromDisk));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process PNG/APNG file at {0}", inputPath);
            return (false, DisplayItem.Empty());
        }
    }

    /// <summary>
    /// Correctly and robustly checks if a PNG stream is an APNG by searching the entire file 
    /// for the 'acTL' (Animation Control) chunk. This version reads directly from the stream
    /// to avoid state conflicts when seeking.
    /// </summary>
    private static async Task<bool> IsAnimatedPngAsync(IRandomAccessStream stream)
    {
        try
        {
            stream.Seek(0);

            // --- 1. Verify PNG Signature ---
            var signatureBuffer = new byte[PngSignature.Length];
            var read = await stream.ReadAsync(signatureBuffer.AsBuffer(), (uint)signatureBuffer.Length, InputStreamOptions.None);

            if (read.Length != PngSignature.Length || !signatureBuffer.SequenceEqual(PngSignature))
            {
                return false; // Not a valid PNG file or couldn't read signature.
            }

            // This buffer will be reused to read each chunk's header (length and type).
            var chunkHeaderBuffer = new byte[8];

            // --- 2. Loop through ALL chunks to find 'acTL' ---
            while (stream.Position < stream.Size)
            {
                // Read the 8-byte header (4-byte length + 4-byte type)
                await stream.ReadAsync(chunkHeaderBuffer.AsBuffer(), 8, InputStreamOptions.None);

                // Manually parse the chunk header, handling Big-Endian byte order for the length.
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(chunkHeaderBuffer, 0, 4); // Convert length from Big-Endian to Little-Endian
                }
                uint chunkLength = BitConverter.ToUInt32(chunkHeaderBuffer, 0);
                string chunkType = Encoding.ASCII.GetString(chunkHeaderBuffer, 4, 4);

                if (chunkType == "acTL")
                {
                    // Animation Control Chunk found. This IS an APNG.
                    return true;
                }

                if (chunkType == "IEND")
                {
                    // Reached the end of the image. If we haven't found acTL, it's not animated.
                    return false;
                }

                // Seek past the current chunk's data and its 4-byte CRC.
                stream.Seek(stream.Position + chunkLength + 4);
            }

            return false; // Reached end of file without finding IEND (malformed PNG).
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not determine if PNG is animated due to an error. Assuming not.");
            return false;
        }
    }


    private static async Task<(bool, DisplayItem)> LoadApngAsFile(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
        {
            Logger.Warn("Input path is null, empty, or file does not exist: {0}", inputPath);
            return (false, null);
        }

        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(inputPath);
            return (true, new DisplayItem(fileBytes, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read byte stream from APNG file: {0}", inputPath);
            return (false, null);
        }
    }
}