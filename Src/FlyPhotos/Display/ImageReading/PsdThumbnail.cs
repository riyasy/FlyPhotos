#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Reads a PSD/PSB file's size and its embedded JPEG thumbnail by walking the file structure, so only
/// the header and the image-resources section are read, never the pixel data.
/// </summary>
internal static class PsdThumbnail
{
    private const uint Signature8Bps = 0x38425053; // "8BPS"
    private const uint Signature8Bim = 0x3842494D; // "8BIM"
    private const ushort ThumbnailV5 = 1036;       // Photoshop 5.0+ thumbnail (RGB JPEG)
    private const ushort ThumbnailV4 = 1033;       // Photoshop 4.0 thumbnail (BGR JPEG)
    private const int ThumbnailHeaderSize = 28;    // format, size, stride, ... before the JPEG bytes

    /// <summary>
    /// Reads the image size from the header and the JPEG thumbnail from resource 1036, else 1033.
    /// </summary>
    /// <returns>True when a thumbnail was read. <paramref name="width"/> and <paramref name="height"/> are
    /// set whenever the header is valid.</returns>
    /// <exception cref="EndOfStreamException">The file ends inside a structure.</exception>
    public static bool TryRead(Stream stream, out int width, out int height, out byte[]? jpeg)
    {
        width = height = 0;
        jpeg = null;

        // Header: signature, version, 6 reserved, channels, then height and width at 14 and 18; 26 bytes in all.
        if (stream.Length < 26 || ReadUInt32(stream) != Signature8Bps) return false;
        stream.Position = 14;
        height = (int)ReadUInt32(stream);
        width = (int)ReadUInt32(stream);

        // Colour-mode data, then image resources. Both have 4-byte lengths in PSD and PSB alike.
        stream.Position = 26;
        stream.Seek(ReadUInt32(stream), SeekOrigin.Current);
        uint resourcesLength = ReadUInt32(stream);
        long end = Math.Min(stream.Position + resourcesLength, stream.Length);

        // Each block: signature, ID, Pascal name padded to even, data size, data padded to even.
        long v4Offset = -1;
        uint v4Size = 0;
        while (stream.Position + 12 <= end)
        {
            uint signature = ReadUInt32(stream);
            ushort id = ReadUInt16(stream);
            int nameLength = ReadByte(stream);
            stream.Seek(nameLength + ((nameLength + 1) & 1), SeekOrigin.Current);
            uint size = ReadUInt32(stream);
            long dataOffset = stream.Position;

            if (signature == Signature8Bim && id == ThumbnailV5) return ReadJpeg(stream, dataOffset, size, end, out jpeg);
            if (signature == Signature8Bim && id == ThumbnailV4 && v4Offset < 0) (v4Offset, v4Size) = (dataOffset, size);

            stream.Position = dataOffset + size + (size & 1);
        }
        return v4Offset >= 0 && ReadJpeg(stream, v4Offset, v4Size, end, out jpeg);
    }

    /// <summary>Reads the JPEG of a thumbnail resource; false when the block is too small or runs past the section.</summary>
    private static bool ReadJpeg(Stream stream, long dataOffset, uint size, long end, out byte[]? jpeg)
    {
        jpeg = null;
        // A corrupt size must not allocate up to 4 GB before the read fails.
        if (size <= ThumbnailHeaderSize || dataOffset + size > end) return false;
        stream.Position = dataOffset + ThumbnailHeaderSize;
        jpeg = new byte[size - ThumbnailHeaderSize];
        stream.ReadExactly(jpeg);
        return true;
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buf = stackalloc byte[4];
        stream.ReadExactly(buf);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> buf = stackalloc byte[2];
        stream.ReadExactly(buf);
        return BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    private static int ReadByte(Stream stream) =>
        stream.ReadByte() is var b and >= 0 ? b : throw new EndOfStreamException();
}
