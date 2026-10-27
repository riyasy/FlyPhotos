#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// The formats that get their own decoder chain. Everything else (JPEG, BMP, RAW, the WIC/Magick long tail)
/// shares the generic chain, which is picked by extension.
/// </summary>
internal enum ImageFormat { Other, Jpeg, Png, Gif, Webp, Bmp, Tiff, Ico, Heif, Avif, Psd, Svg }

/// <summary>Maps extensions and file headers to <see cref="ImageFormat"/>. Pure: no I/O.</summary>
internal static class ImageFormats
{
    /// <summary>Extension to format, case-insensitive (no upper-cased copy of the extension per file).</summary>
    private static readonly Dictionary<string, ImageFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = ImageFormat.Jpeg, [".jpeg"] = ImageFormat.Jpeg, [".jpe"] = ImageFormat.Jpeg, [".jfif"] = ImageFormat.Jpeg,
        [".png"] = ImageFormat.Png,
        [".gif"] = ImageFormat.Gif,
        [".webp"] = ImageFormat.Webp,
        [".bmp"] = ImageFormat.Bmp,
        [".tif"] = ImageFormat.Tiff, [".tiff"] = ImageFormat.Tiff,
        [".ico"] = ImageFormat.Ico, [".icon"] = ImageFormat.Ico,
        [".heic"] = ImageFormat.Heif, [".heif"] = ImageFormat.Heif, [".hif"] = ImageFormat.Heif,
        [".avif"] = ImageFormat.Avif,
        [".psd"] = ImageFormat.Psd,
        [".svg"] = ImageFormat.Svg
    };

    /// <summary>The format an extension names; <see cref="ImageFormat.Other"/> when it has no chain of its own.</summary>
    public static ImageFormat FromExtension(string extension) =>
        ByExtension.TryGetValue(extension, out var format) ? format : ImageFormat.Other;

    /// <summary>The extension a file of <paramref name="format"/> normally has, for the extension-keyed codec lookups.</summary>
    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Webp => ".webp",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tif",
        ImageFormat.Ico => ".ico",
        ImageFormat.Heif => ".heic",
        ImageFormat.Avif => ".avif",
        ImageFormat.Psd => ".psd",
        ImageFormat.Svg => ".svg",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Other has no single extension")
    };

    /// <summary>
    /// The format the header bytes show, or null when it's none of the formats with their own decoder chain.
    /// TIFF is left out on purpose: every TIFF-based RAW (NEF, CR2, DNG…) would match it.
    /// </summary>
    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return ImageFormat.Jpeg;
        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageFormat.Png;
        if (header.StartsWith("GIF8"u8)) return ImageFormat.Gif;
        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ImageFormat.Webp;
        if (header.StartsWith("8BPS"u8)) return ImageFormat.Psd;
        // "BM" alone is weak; its two reserved header fields (bytes 6-9) are always zero.
        if (header.Length >= 14 && header.StartsWith("BM"u8) && BinaryPrimitives.ReadUInt32LittleEndian(header[6..]) == 0)
            return ImageFormat.Bmp;
        // Before ICO: an ftyp box of 256 bytes starts with the same 00 00 01 00.
        if (DetectIsoBmff(header) is { } isoBmff) return isoBmff;
        // Reserved 0, type 1 (icon), then a non-zero image count.
        if (header.Length >= 6 && header.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]) &&
            BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) > 0)
            return ImageFormat.Ico;
        return null;
    }

    /// <summary>HEIF and AVIF are ISO-BMFF files that start with an <c>ftyp</c> box listing brands.</summary>
    private static ImageFormat? DetectIsoBmff(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header.Slice(4, 4).SequenceEqual("ftyp"u8)) return null;

        // Major brand at 8, minor version at 12, compatible brands from 16 to the end of the box.
        var boxEnd = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        var isHeif = false;
        for (var offset = 8; offset + 4 <= boxEnd; offset += 4)
        {
            if (offset == 12) continue; // the minor version, not a brand
            var brand = header.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return ImageFormat.Avif;
            isHeif |= brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
                      brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
                      brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
        }
        return isHeif ? ImageFormat.Heif : null;
    }
}
