namespace FlyPhotos.Utils;

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
    public static uint Compute(byte[] d)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in d) c = (c >> 8) ^ Table[(c ^ b) & 0xFF];
        return ~c;
    }
}