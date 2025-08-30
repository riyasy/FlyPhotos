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
    public static async Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
    {
        if (GetThumbNailByteArray(inputPath, out byte[] thumbnailData))
        {
            try
            {
                using var stream = new MemoryStream(thumbnailData);
                CanvasBitmap bitmap = await CanvasBitmap.LoadAsync(ctrl, stream.AsRandomAccessStream());
                return (true, new DisplayItem(bitmap, PreviewSource.FromDisk));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, DisplayItem.Empty());
            }
        }
        return (false, DisplayItem.Empty());
    }

    public static async Task<(bool, DisplayItem)> GetHq(CanvasControl d2dCanvas, string path)
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
            return (true, new DisplayItem(bitmap, PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, DisplayItem.Empty());
        }
    }

    private static bool GetThumbNailByteArray(string inputFilePath, out byte[] thumbnailData)
    {
        thumbnailData = null;

        try
        {
            using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // --- Header Validation ---
            var header = new byte[4];
            fileStream.ReadExactly(header, 0, header.Length);
            // PSD header must be '8BPS'
            if (header[0] != '8' || header[1] != 'B' || header[2] != 'P' || header[3] != 'S')
            {
                return false;
            }

            // --- Efficient Search for Thumbnail Resource ---
            // The resource marker we are looking for is '8BIM' followed by resource ID 1033 or 1036
            byte[] searchPatternV5 = [(byte)'8', (byte)'B', (byte)'I', (byte)'M', 0x04, 0x0C]; // 1036 for PS 5.0+
            byte[] searchPatternV4 = [(byte)'8', (byte)'B', (byte)'I', (byte)'M', 0x04, 0x09]; // 1033 for PS 4.0

            long position = FindBytePattern(fileStream, searchPatternV5);
            if (position == -1)
            {
                position = FindBytePattern(fileStream, searchPatternV4);
            }

            if (position == -1)
            {
                return false; // Thumbnail resource not found
            }

            // We found the marker. Position the stream right after it.
            fileStream.Position = position + searchPatternV5.Length;

            // The stream is now at the resource name. We can skip it.
            // A full parser would read this properly, but for just getting the data,
            // we can read the size of the data block that follows.
            using var reader = new BinaryReader(fileStream);

            // Skip Pascal string for the name (1 byte length + name + padding)
            byte nameLength = reader.ReadByte();
            // Seek past the name string and its padding to make the total length even.
            // (1 byte for length + nameLength + padding) must be an even number.
            int nameBlockLength = 1 + nameLength;
            int namePadding = nameBlockLength % 2 == 0 ? 0 : 1;
            fileStream.Seek(nameLength + namePadding, SeekOrigin.Current);

            // --- Read Thumbnail Data ---
            // Read the size of the resource data block (big-endian)
            uint dataSize = ReadBigEndianUInt32(reader);
            if (dataSize <= 28)
            {
                // The first 28 bytes are thumbnail metadata, not image data.
                return false;
            }

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
