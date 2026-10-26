#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static class ImageFormatSniffer
{
    private const int MaxHeaderBytes = 256;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static async Task<string?> GetMismatchedExtensionAsync(string path, string currentExtension)
    {
        try
        {
            var header = new byte[MaxHeaderBytes];
            var bytesRead = 0;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 64,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (bytesRead < header.Length)
                {
                    var read = await stream.ReadAsync(header.AsMemory(bytesRead));
                    if (read == 0) break;
                    bytesRead += read;
                }
            }

            var detectedExtension = DetectExtension(header.AsSpan(0, bytesRead));
            return string.Equals(detectedExtension, currentExtension, StringComparison.OrdinalIgnoreCase)
                ? null
                : detectedExtension;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Logger.Warn(ex, "Could not inspect image header for {0}", path);
            return null;
        }
    }

    private static string? DetectExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 &&
            header[2] == 0x4E && header[3] == 0x47 && header[4] == 0x0D &&
            header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return ".PNG";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ".JPG";
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
            return ".GIF";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) &&
            header.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ".WEBP";
        if (header.StartsWith("BM"u8))
            return ".BMP";
        if (header.Length >= 4 && header[0] == 0x00 && header[1] == 0x00 &&
            (header[2] == 0x01 || header[2] == 0x02) && header[3] == 0x00)
            return ".ICO";
        if (header.Length >= 4 &&
            ((header[0] == 0x49 && header[1] == 0x49 &&
              (header[2] == 0x2A || header[2] == 0x2B) && header[3] == 0x00) ||
             (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 &&
              (header[3] == 0x2A || header[3] == 0x2B))))
            return ".TIFF";

        return DetectIsoBmffExtension(header);
    }

    private static string? DetectIsoBmffExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header.Slice(4, 4).SequenceEqual("ftyp"u8))
            return null;

        var boxLength = (int)Math.Min(
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[..4]),
            (uint)header.Length);
        if (boxLength < 16)
            return null;

        var avif = IsBrand(header.Slice(8, 4), "avif"u8) || IsBrand(header.Slice(8, 4), "avis"u8);
        var heif = IsHeifBrand(header.Slice(8, 4));

        for (var offset = 16; offset + 4 <= boxLength; offset += 4)
        {
            var brand = header.Slice(offset, 4);
            avif |= IsBrand(brand, "avif"u8) || IsBrand(brand, "avis"u8);
            heif |= IsHeifBrand(brand);
        }

        if (avif) return ".AVIF";
        if (heif) return ".HEIF";
        return null;
    }

    private static bool IsHeifBrand(ReadOnlySpan<byte> brand) =>
        IsBrand(brand, "heif"u8) || IsBrand(brand, "heic"u8) || IsBrand(brand, "heix"u8) ||
        IsBrand(brand, "hevc"u8) || IsBrand(brand, "hevx"u8) ||
        IsBrand(brand, "heim"u8) || IsBrand(brand, "heis"u8) ||
        IsBrand(brand, "mif1"u8) || IsBrand(brand, "msf1"u8);

    private static bool IsBrand(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
        actual.SequenceEqual(expected);
}
