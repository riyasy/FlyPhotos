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
                return (true, new DisplayItem(bitmap, DisplayItem.PreviewSource.FromDisk));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, null);
            }
        }
        return (false, null);
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
            var device = CanvasDevice.GetSharedDevice();
            var bitmap = await CanvasBitmap.LoadAsync(device, stream.AsRandomAccessStream());

            return (true, new DisplayItem(bitmap, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, null);
        }
    }

    // Save thumbnail to a file
    private static bool SaveThumbNail(string inputFilePath, string outputFilePath)
    {
        if (GetThumbNailByteArray(inputFilePath, out byte[] thumbnailData))
        {
            try
            {
                File.WriteAllBytes(outputFilePath, thumbnailData);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }
        }
        return false;
    }

    // Get thumbnail as a byte array
    private static bool GetThumbNailByteArray(string inputFilePath, out byte[] thumbnailData)
    {
        thumbnailData = null;

        try
        {
            using var fileStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read);
            using var binaryReader = new BinaryReader(fileStream);
            // Read and validate the PSD header
            byte[] header = binaryReader.ReadBytes(4);
            // PSD header should be '8BPS'
            if (header[0] != '8' || header[1] != 'B' || header[2] != 'P' || header[3] != 'S')
            {
                return false;
            }

            long fileLength = fileStream.Length;
            bool found8BIM = false;

            // Search for the 8BIM resource section
            while (fileStream.Position < fileLength - 6)
            {
                byte[] chunk = binaryReader.ReadBytes(6);
                fileStream.Seek(-6, SeekOrigin.Current); // Move back to the start of the chunk

                // Check if this chunk is the 8BIM marker
                if (chunk[0] == '8' && chunk[1] == 'B' && chunk[2] == 'I' && chunk[3] == 'M')
                {
                    // Check if the resource type is Thumbnail (4,9) or (4,12)
                    if ((chunk[4] == 4 && chunk[5] == 9) || (chunk[4] == 4 && chunk[5] == 12))
                    {
                        found8BIM = true;
                        // Skip past the 8BIM marker and resource ID (6 bytes) + 2 bytes for additional length field
                        fileStream.Seek(6 + 2, SeekOrigin.Current);
                        break;
                    }
                }
                // Move forward 1 byte and continue searching
                fileStream.Seek(1, SeekOrigin.Current);
            }

            // If the 8BIM marker was not found, return false
            if (!found8BIM)
            {
                return false;
            }

            // Read the thumbnail size (4 bytes) from the file
            long thumbnailSize = 0;
            byte[] sizeBytes = binaryReader.ReadBytes(4);

            // Calculate the size of the thumbnail data
            for (int i = 0; i < 4; i++)
            {
                thumbnailSize += sizeBytes[i] * (long)Math.Pow(256, 3 - i);
            }

            // Skip 28 bytes 
            fileStream.Seek(28, SeekOrigin.Current);

            // Adjust the size by subtracting the 28 bytes skipped
            thumbnailSize -= 28;

            // If the calculated thumbnail size is invalid, return false
            if (thumbnailSize <= 0)
            {
                return false;
            }

            // Read the thumbnail data from the file
            thumbnailData = new byte[thumbnailSize];
            int bytesRead = fileStream.Read(thumbnailData, 0, thumbnailData.Length);

            // Verify that we read the expected number of bytes
            return bytesRead == thumbnailSize;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }
}
