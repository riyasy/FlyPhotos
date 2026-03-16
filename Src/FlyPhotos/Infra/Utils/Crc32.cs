using System;

namespace FlyPhotos.Infra.Utils;

public static class Crc32
{
    private static readonly uint[] Table;

    static Crc32()
    {
        const uint p = 0xEDB88320;
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) c = (c & 1) == 1 ? (c >> 1) ^ p : c >> 1;
            Table[i] = c;
        }
    }

    /// <summary>Computes the CRC-32 checksum of <paramref name="d" />.</summary>
    public static uint Compute(byte[] d)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in d) c = (c >> 8) ^ Table[(c ^ b) & 0xFF];
        return ~c;
    }

    /// <summary>
    ///     Computes the CRC-32 checksum of <paramref name="d" /> without allocating.
    ///     Accepts a <see cref="ReadOnlySpan{T}" /> so callers can pass a slice of a
    ///     reusable scratch buffer (e.g. <c>crcBuf.AsSpan(0, needed)</c>) instead of
    ///     creating a trimmed array copy just to satisfy the <c>byte[]</c> overload.
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> d)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in d) c = (c >> 8) ^ Table[(c ^ b) & 0xFF];
        return ~c;
    }
}