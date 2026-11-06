using System.Buffers.Binary;
using FlyPhotos.Display.ImageReading;

namespace FlyPhotos.Tests;

/// <summary>
/// PsdThumbnail on synthetic PSDs: header, colour-mode data, image resources, then "pixel data".
/// </summary>
[TestClass]
public sealed class PsdThumbnailTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 1, 2, 3, 4, 5, 0xFF, 0xD9];

    /// <summary>One image-resource block: 8BIM, ID, Pascal name padded to even, size, data padded to even.</summary>
    private static byte[] Block(ushort id, byte[] data, string name = "")
    {
        var b = new List<byte>("8BIM"u8.ToArray());
        b.AddRange(BigEndian16(id));
        b.Add((byte)name.Length);
        b.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
        if ((name.Length + 1) % 2 == 1) b.Add(0);
        b.AddRange(BigEndian32((uint)data.Length));
        b.AddRange(data);
        if (data.Length % 2 == 1) b.Add(0);
        return [.. b];
    }

    /// <summary>A thumbnail resource's data: the 28-byte thumbnail header, then the JPEG.</summary>
    private static byte[] Thumb(byte[] jpeg) => [.. new byte[28], .. jpeg];

    private static MemoryStream Psd(int width, int height, byte[] colourMode, byte[][] blocks, byte[] pixels)
    {
        var b = new List<byte>("8BPS"u8.ToArray());
        b.AddRange(BigEndian16(1));     // version
        b.AddRange(new byte[6]);        // reserved
        b.AddRange(BigEndian16(3));     // channels
        b.AddRange(BigEndian32((uint)height));
        b.AddRange(BigEndian32((uint)width));
        b.AddRange(BigEndian16(8));     // depth
        b.AddRange(BigEndian16(3));     // RGB
        b.AddRange(BigEndian32((uint)colourMode.Length));
        b.AddRange(colourMode);
        byte[] resources = [.. blocks.SelectMany(x => x)];
        b.AddRange(BigEndian32((uint)resources.Length));
        b.AddRange(resources);
        b.AddRange(pixels);
        return new MemoryStream([.. b]);
    }

    private static byte[] BigEndian16(ushort v) { var a = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(a, v); return a; }
    private static byte[] BigEndian32(uint v) { var a = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(a, v); return a; }

    [TestMethod]
    public void Reads_size_and_thumbnail_after_other_blocks()
    {
        // An odd-length name and odd-sized data before the thumbnail exercise both paddings.
        using var s = Psd(4000, 3000, new byte[768],
            [Block(1005, [1, 2, 3], "ab"), Block(1036, Thumb(Jpeg))], new byte[1000]);

        Assert.IsTrue(PsdThumbnail.TryRead(s, out int w, out int h, out byte[]? jpeg));
        Assert.AreEqual((4000, 3000), (w, h));
        CollectionAssert.AreEqual(Jpeg, jpeg);
    }

    [TestMethod]
    public void Prefers_the_v5_thumbnail_over_an_earlier_v4_one()
    {
        byte[] v4 = [0xFF, 0xD8, 9, 0xFF, 0xD9];
        using var s = Psd(10, 10, [], [Block(1033, Thumb(v4)), Block(1036, Thumb(Jpeg))], []);

        Assert.IsTrue(PsdThumbnail.TryRead(s, out _, out _, out byte[]? jpeg));
        CollectionAssert.AreEqual(Jpeg, jpeg);
    }

    [TestMethod]
    public void Falls_back_to_the_v4_thumbnail()
    {
        using var s = Psd(10, 10, [], [Block(1005, [0, 0]), Block(1033, Thumb(Jpeg))], []);

        Assert.IsTrue(PsdThumbnail.TryRead(s, out _, out _, out byte[]? jpeg));
        CollectionAssert.AreEqual(Jpeg, jpeg);
    }

    [TestMethod]
    public void A_marker_in_the_pixel_data_is_ignored()
    {
        // The old byte scan took this as the thumbnail; only the resources section is searched now.
        byte[] pixels = [.. new byte[100], .. Block(1036, Thumb(Jpeg)), .. new byte[100]];
        using var s = Psd(800, 600, [], [Block(1005, [0, 0])], pixels);

        Assert.IsFalse(PsdThumbnail.TryRead(s, out int w, out int h, out byte[]? jpeg));
        Assert.AreEqual((800, 600), (w, h));
        Assert.IsNull(jpeg);
    }

    [TestMethod]
    public void Too_small_a_thumbnail_resource_returns_false()
    {
        using var s = Psd(10, 10, [], [Block(1036, new byte[28])], []);
        Assert.IsFalse(PsdThumbnail.TryRead(s, out _, out _, out _));
    }

    [TestMethod]
    public void A_thumbnail_size_past_the_resources_section_returns_false()
    {
        // The thumbnail block claims ~4 GB: rejected before anything is allocated.
        byte[] block = Block(1036, Thumb(Jpeg));
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8, 4), 0xFFFFFFF0); // size field: after 8BIM, ID, empty name
        using var s = Psd(10, 10, [], [block], new byte[100]);

        Assert.IsFalse(PsdThumbnail.TryRead(s, out _, out _, out byte[]? jpeg));
        Assert.IsNull(jpeg);
    }

    [TestMethod]
    public void Not_a_psd_returns_false()
    {
        using var s = new MemoryStream(new byte[100]);
        Assert.IsFalse(PsdThumbnail.TryRead(s, out _, out _, out _));
    }

    [TestMethod]
    public void A_truncated_thumbnail_returns_false()
    {
        using var full = Psd(10, 10, [], [Block(1036, Thumb(Jpeg))], []);
        using var s = new MemoryStream(full.ToArray()[..^3]);
        Assert.IsFalse(PsdThumbnail.TryRead(s, out _, out _, out _));
    }

    [TestMethod]
    public void A_file_truncated_inside_the_header_sections_throws()
    {
        // 26-byte header but no colour-mode length: the caller (PsdReader) catches and logs.
        using var full = Psd(10, 10, [], [], []);
        using var s = new MemoryStream(full.ToArray()[..28]);
        Assert.ThrowsExactly<EndOfStreamException>(() => PsdThumbnail.TryRead(s, out _, out _, out _));
    }
}
