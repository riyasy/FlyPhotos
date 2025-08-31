using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Data;
using ImageMagick;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Readers;
internal class PsdReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Get preview as a CanvasBitmap for WinUI
    public static async Task<(bool, PreviewDisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        if (GetPsdInfo(inputPath, out int width, out var height, out var thumbnailData))
        {
            try
            {
                using var stream = new MemoryStream(thumbnailData);
                CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(ctrl, stream.AsRandomAccessStream());
                var metaData = new ImageMetadata(width, height);
                return (true, new PreviewDisplayItem(bitmap, PreviewSource.FromDisk, metaData));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, PreviewDisplayItem.Empty());
            }
        }
        return (false, PreviewDisplayItem.Empty());
    }

    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl d2dCanvas, string path)
    {
        try
        {
            // Load the PSD file using ImageMagick
            using var image = new MagickImage(path);
            // Convert the image to a format compatible with CanvasBitmap
            using var stream = new MemoryStream();
            image.Format = MagickFormat.Jpeg;
            image.Quality = 80;
            await image.WriteAsync(stream);
            stream.Position = 0;

            // Create a CanvasBitmap from the MemoryStream
            var bitmap = await CanvasBitmap.LoadAsync(d2dCanvas, stream.AsRandomAccessStream());
            return (true, new StaticHqDisplayItem(bitmap));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, HqDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Efficiently extracts the main image dimensions (width, height) and the embedded
    /// JPEG thumbnail from a Photoshop (PSD) file.
    /// </summary>
    /// <param name="inputFilePath">The path to the PSD file.</param>
    /// <param name="width">The width of the main image.</param>
    /// <param name="height">The height of the main image.</param>
    /// <param name="thumbnailData">The byte array of the embedded JPEG thumbnail, or null if not found.</param>
    /// <returns>True if the header was read successfully; false otherwise. The thumbnail may still be null.</returns>
    public static bool GetPsdInfo(string inputFilePath, out int width, out int height, out byte[] thumbnailData)
    {
        // Initialize out parameters
        width = 0;
        height = 0;
        thumbnailData = null;

        try
        {
            using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // The file must be at least 26 bytes to contain the full header
            if (fileStream.Length < 26)
            {
                return false;
            }

            using var reader = new BinaryReader(fileStream);

            // --- Read Header for Signature, Width, and Height ---
            var signature = reader.ReadBytes(4);
            // PSD header must be '8BPS'
            if (signature[0] != '8' || signature[1] != 'B' || signature[2] != 'P' || signature[3] != 'S')
            {
                return false;
            }

            // The header is fixed. We can jump directly to the dimensions.
            // Offset 14 for Height, Offset 18 for Width.
            fileStream.Seek(14, SeekOrigin.Begin);

            // NEW: Read the height and width from the header
            height = (int)ReadBigEndianUInt32(reader);
            width = (int)ReadBigEndianUInt32(reader);


            // --- Efficient Search for Thumbnail Resource ---
            // The rest of your proven thumbnail logic can now run.
            // The resource marker we are looking for is '8BIM' followed by resource ID 1033 or 1036
            byte[] searchPatternV5 = [(byte)'8', (byte)'B', (byte)'I', (byte)'M', 0x04, 0x0C]; // 1036 for PS 5.0+
            byte[] searchPatternV4 = [(byte)'8', (byte)'B', (byte)'I', (byte)'M', 0x04, 0x09]; // 1033 for PS 4.0

            long position = FindBytePattern(fileStream, searchPatternV5);
            if (position == -1)
                position = FindBytePattern(fileStream, searchPatternV4);

            if (position == -1) 
                return false;

            // We found the marker. Position the stream right after it.
            fileStream.Position = position + searchPatternV5.Length;

            // Skip Pascal string for the name (1 byte length + name + padding)
            byte nameLength = reader.ReadByte();
            // Seek past the name string and its padding to make the total length even.
            // (1 byte for length + nameLength + padding) must be an even number.
            int nameBlockLength = 1 + nameLength;
            int namePadding = nameBlockLength % 2 == 0 ? 0 : 1;
            fileStream.Seek(nameLength + namePadding, SeekOrigin.Current);

            // --- Read Thumbnail Data ---
            uint dataSize = ReadBigEndianUInt32(reader);
            if (dataSize <= 28) return false;

            // Skip the 28-byte thumbnail header to get to the raw JPEG data
            fileStream.Seek(28, SeekOrigin.Current);

            // The actual JPEG data size is the total size minus the header
            int jpegDataSize = (int)(dataSize - 28);
            thumbnailData = reader.ReadBytes(jpegDataSize);
            return thumbnailData.Length == jpegDataSize;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }

    // Helper function to efficiently find a byte pattern in a stream
    private static long FindBytePattern(Stream stream, byte[] pattern)
    {
        stream.Position = 0; // Start search from the beginning
        const int bufferSize = 4096;
        var buffer = new byte[bufferSize];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i <= bytesRead - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return stream.Position - bytesRead + i;
                }
            }
            // Important: If the pattern could span across buffer boundaries, more complex logic is needed.
            // For this specific use case, it's highly unlikely, so we keep it simple.
            // We reposition the stream back slightly to handle edge cases.
            stream.Seek(-(pattern.Length - 1), SeekOrigin.Current);
        }
        return -1; // Pattern not found
    }

    // Helper method for reading a Big-Endian 32-bit integer
    private static uint ReadBigEndianUInt32(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

}
