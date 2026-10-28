using System.Buffers.Binary;
using System.Text;
using FlyPhotos.Services;

namespace FlyPhotos.Tests;

/// <summary>
/// Rating a multi-picture JPEG (Ultra HDR gain map, Sony preview, motion photo) embeds the rating and keeps the
/// MPF entries pointing at the extra pictures. The JPEGs are built here: SOI, APP segments, SOS, EOI, then a
/// second picture that only the MP entry table can find.
/// </summary>
[TestClass]
public sealed class RatingMpfTests : TempFolderTest
{
    private const string XmpId = "http://ns.adobe.com/xap/1.0/";

    private const string EmptyXmp =
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"/></rdf:RDF></x:xmpmeta>";

    // Stands in for the gain map or preview after the primary image's EOI.
    private static readonly byte[] SecondPicture = [0xFF, 0xD8, 0xFF, 0xD9, 1, 2, 3, 4, 5, 6, 7, 8];

    [TestMethod]
    public void Xmp_before_mpf_embeds_and_keeps_second_picture()
    {
        var path = Write(xmpFirst: true);
        var before = MpTable(File.ReadAllBytes(path));

        Assert.IsTrue(RatingMetadata.WriteRating(path, 3, embedInFile: true).Success);

        var bytes = File.ReadAllBytes(path);
        var after = MpTable(bytes);
        var growth = bytes.Length - before.FileLength;
        Assert.IsTrue(growth > 0, "the rating should have grown the XMP segment");
        Assert.AreEqual(before.PrimarySize + growth, after.PrimarySize);
        Assert.AreEqual(before.SecondOffset, after.SecondOffset, "header and picture moved together");
        AssertSecondPictureAt(bytes, after);
        Assert.AreEqual(3, RatingMetadata.ReadRating(path));
        Assert.IsFalse(File.Exists(path + ".xmp"));
    }

    [TestMethod]
    public void Mpf_before_xmp_shifts_second_picture_offset()
    {
        var path = Write(xmpFirst: false);
        var before = MpTable(File.ReadAllBytes(path));

        Assert.IsTrue(RatingMetadata.WriteRating(path, 5, embedInFile: true).Success);

        var bytes = File.ReadAllBytes(path);
        var after = MpTable(bytes);
        var growth = bytes.Length - before.FileLength;
        Assert.IsTrue(growth > 0, "the rating should have grown the XMP segment");
        Assert.AreEqual(before.PrimarySize + growth, after.PrimarySize);
        Assert.AreEqual(before.SecondOffset + growth, after.SecondOffset, "picture moved, header did not");
        AssertSecondPictureAt(bytes, after);
        Assert.AreEqual(5, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Rating_changes_back_and_forth_keep_second_picture()
    {
        var path = Write(xmpFirst: true);
        foreach (var rating in new[] { 3, 5, 0, 4 })
            Assert.IsTrue(RatingMetadata.WriteRating(path, rating, embedInFile: true).Success);

        var bytes = File.ReadAllBytes(path);
        AssertSecondPictureAt(bytes, MpTable(bytes));
        Assert.AreEqual(4, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Unreadable_mpf_falls_back_to_sidecar_and_leaves_photo_unchanged()
    {
        var path = Write(xmpFirst: true, brokenMpf: true);
        var original = File.ReadAllBytes(path);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 2, embedInFile: true).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(path + ".xmp"));
        Assert.AreEqual(2, RatingMetadata.ReadRating(path));
    }

    // --- Building and reading the test JPEG ---

    private readonly record struct MpInfo(long FileLength, long HeaderAt, long PrimarySize, long SecondOffset, long SecondSize);

    private string Write(bool xmpFirst, bool brokenMpf = false)
    {
        var xmp = Segment(0xE1, [.. Encoding.ASCII.GetBytes(XmpId + "\0"), .. Encoding.UTF8.GetBytes(EmptyXmp)]);
        var mpf = Segment(0xE2, MpfPayload(brokenMpf));
        byte[] scan = [0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0x33, 0xFF, 0xD9]; // SOS, entropy data, EOI

        var primary = new List<byte> { 0xFF, 0xD8 };
        primary.AddRange(xmpFirst ? xmp : mpf);
        primary.AddRange(xmpFirst ? mpf : xmp);
        primary.AddRange(scan);

        // Fill in the table now that the primary image's size and the MP header's position are known.
        var bytes = primary.Concat(SecondPicture).ToArray();
        if (!brokenMpf)
        {
            var headerAt = MpHeaderAt(bytes);
            var entries = headerAt + EntriesOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)entries + 4), (uint)primary.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)entries + 16 + 4), (uint)SecondPicture.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)entries + 16 + 8), (uint)(primary.Count - headerAt));
        }

        var path = PathOf("photo.jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // MP header (little-endian TIFF): 8-byte header, IFD of one MPEntry (0xB002, UNDEFINED, 32 bytes), next-IFD 0,
    // then two 16-byte entries: attributes, size, offset (relative to the MP header), two dependent entry numbers.
    private const int EntriesOffset = 8 + 2 + 12 + 4;

    private static byte[] MpfPayload(bool broken)
    {
        var header = new byte[EntriesOffset + 32];
        "II*\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 0xB002);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), EntriesOffset);
        if (broken) header[2] = 0; // not TIFF magic 42
        return [.. "MPF\0"u8, .. header];
    }

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    private static long MpHeaderAt(byte[] bytes) => bytes.AsSpan().IndexOf("MPF\0"u8) + 4;

    private static MpInfo MpTable(byte[] bytes)
    {
        var headerAt = MpHeaderAt(bytes);
        var entries = bytes.AsSpan((int)(headerAt + EntriesOffset));
        return new MpInfo(bytes.Length, headerAt,
            BinaryPrimitives.ReadUInt32LittleEndian(entries[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entries[(16 + 8)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entries[(16 + 4)..]));
    }

    private static void AssertSecondPictureAt(byte[] bytes, MpInfo table)
    {
        var start = (int)(table.HeaderAt + table.SecondOffset);
        Assert.AreEqual(bytes.Length - SecondPicture.Length, start, "MP entry must point at the second picture");
        CollectionAssert.AreEqual(SecondPicture, bytes[start..]);
        Assert.AreEqual(start, table.PrimarySize, "primary size must end where the second picture starts");
    }
}
