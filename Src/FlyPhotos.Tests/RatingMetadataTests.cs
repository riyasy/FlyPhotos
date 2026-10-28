using System.Buffers.Binary;
using System.Text;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;

namespace FlyPhotos.Tests;

/// <summary>
/// Where RatingMetadata saves a rating and what it reads back, per format, on small files built here.
/// Multi-picture JPEGs are covered by <see cref="RatingMpfTests"/>.
/// </summary>
[TestClass]
public sealed class RatingMetadataTests : TempFolderTest
{
    // ---------- JPEG ----------

    [TestMethod]
    public void Unrated_jpeg_gets_the_rating_inside_the_file()
    {
        var path = WriteFile("photo.jpg", Jpeg());

        Assert.IsTrue(RatingMetadata.WriteRating(path, 4, embedInFile: true).Success);

        Assert.AreEqual(4, RatingMetadata.ReadRating(path));
        Assert.IsFalse(File.Exists(path + ".xmp"));
        CollectionAssert.AreEqual(JpegScan, File.ReadAllBytes(path)[^JpegScan.Length..], "image data is copied as is");
    }

    [TestMethod]
    public void Only_the_rating_changes_in_existing_xmp()
    {
        var path = WriteFile("photo.jpg", Jpeg(XmpSegment(Packet("xmp:Rating=\"1\" xmp:CreatorTool=\"Camera 1.0\""))));

        RatingMetadata.WriteRating(path, 2, embedInFile: true);

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        StringAssert.Contains(text, "xmp:Rating=\"2\" xmp:CreatorTool=\"Camera 1.0\"");
        Assert.AreEqual(2, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Microsoft_photo_rating_follows_the_xmp_rating()
    {
        var path = WriteFile("photo.jpg", Jpeg(XmpSegment(Packet(
            "xmlns:MicrosoftPhoto=\"http://ns.microsoft.com/photo/1.0/\" xmp:Rating=\"2\" MicrosoftPhoto:Rating=\"25\""))));

        RatingMetadata.WriteRating(path, 4, embedInFile: true);

        StringAssert.Contains(Encoding.UTF8.GetString(File.ReadAllBytes(path)), "MicrosoftPhoto:Rating=\"75\"");
    }

    [TestMethod]
    public void Exif_rating_tag_follows_the_xmp_rating()
    {
        var path = WriteFile("photo.jpg", Jpeg(ExifSegment(exifRating: 2), XmpSegment(Packet("xmp:Rating=\"2\""))));

        RatingMetadata.WriteRating(path, 5, embedInFile: true);

        var bytes = File.ReadAllBytes(path);
        var value = bytes.AsSpan().IndexOf("Exif\0\0"u8) + 6 + 8 + 2 + 8; // TIFF header, entry count, tag/type/count
        Assert.AreEqual(5, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(value)));
    }

    [TestMethod]
    public void Embedding_off_writes_a_sidecar_and_leaves_the_jpeg_untouched()
    {
        var original = Jpeg();
        var path = WriteFile("photo.jpg", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 3, embedInFile: false).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(path + ".xmp"));
        Assert.AreEqual(3, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Clearing_an_unrated_jpeg_changes_nothing()
    {
        var original = Jpeg();
        var path = WriteFile("photo.jpg", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 0, embedInFile: true).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsFalse(File.Exists(path + ".xmp"));
    }

    [TestMethod]
    public void Clearing_a_rating_stored_in_the_file_writes_an_explicit_zero()
    {
        var embedded = WriteFile("embedded.jpg", Jpeg(XmpSegment(Packet("xmp:Rating=\"3\""))));
        var sidecar = WriteFile("sidecar.jpg", Jpeg(XmpSegment(Packet("xmp:Rating=\"3\""))));

        RatingMetadata.WriteRating(embedded, 0, embedInFile: true);
        RatingMetadata.WriteRating(sidecar, 0, embedInFile: false);

        Assert.AreEqual(0, RatingMetadata.ReadRating(embedded));
        Assert.IsTrue(File.Exists(sidecar + ".xmp"), "a 0 sidecar is needed to hide the rating in the file");
        Assert.AreEqual(0, RatingMetadata.ReadRating(sidecar));
    }

    [TestMethod]
    public void Rating_is_clamped_to_five()
    {
        var path = WriteFile("photo.jpg", Jpeg());

        Assert.AreEqual(5, RatingMetadata.WriteRating(path, 9, embedInFile: true).Rating);
        Assert.AreEqual(5, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Xmp_over_64_kb_falls_back_to_a_sidecar()
    {
        // Packet at the JPEG segment limit, with no padding to give back, so adding the rating cannot fit.
        var filler = new string('a', 65504 - Encoding.UTF8.GetByteCount(Packet("xmp:CreatorTool=\"\"")));
        var original = Jpeg(XmpSegment(Packet("xmp:CreatorTool=\"" + filler + "\"")));
        var path = WriteFile("photo.jpg", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 4, embedInFile: true).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(path + ".xmp"));
        Assert.AreEqual(4, RatingMetadata.ReadRating(path));
    }

    // ---------- sidecars next to embeddable files ----------

    [TestMethod]
    public void Sidecar_wins_over_the_rating_in_the_file()
    {
        var path = WriteFile("photo.jpg", Jpeg(XmpSegment(Packet("xmp:Rating=\"4\""))));
        File.WriteAllText(path + ".xmp", Packet("xmp:Rating=\"1\""));

        Assert.AreEqual(1, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Embedding_updates_an_existing_sidecar_too()
    {
        var path = WriteFile("photo.jpg", Jpeg());
        File.WriteAllText(path + ".xmp", Packet("xmp:Rating=\"1\""));

        RatingMetadata.WriteRating(path, 3, embedInFile: true);

        StringAssert.Contains(File.ReadAllText(path + ".xmp"), "xmp:Rating=\"3\"");
        Assert.AreEqual(3, RatingMetadata.ReadRating(path));
    }

    // ---------- PNG ----------

    [TestMethod]
    public void Png_gets_an_xmp_chunk_before_the_image_data()
    {
        var path = WriteFile("photo.png", Png(complete: true));

        Assert.IsTrue(RatingMetadata.WriteRating(path, 2, embedInFile: true).Success);

        var bytes = File.ReadAllBytes(path);
        Assert.IsTrue(bytes.AsSpan().IndexOf("iTXt"u8) < bytes.AsSpan().IndexOf("IDAT"u8));
        Assert.AreEqual(2, RatingMetadata.ReadRating(path));
        Assert.IsFalse(File.Exists(path + ".xmp"));
    }

    [TestMethod]
    public void Broken_png_falls_back_to_a_sidecar()
    {
        var original = Png(complete: false);
        var path = WriteFile("photo.png", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 2, embedInFile: true).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.AreEqual(2, RatingMetadata.ReadRating(path));
    }

    // ---------- TIFF ----------

    [TestMethod]
    public void Tiff_gets_the_rating_without_moving_existing_bytes()
    {
        var original = Tiff(magic: 42);
        var path = WriteFile("photo.tif", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 3, embedInFile: true).Success);

        var bytes = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(original[8..], bytes[8..original.Length], "only the IFD0 offset in the header changes");
        Assert.AreEqual(3, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Second_tiff_edit_fits_in_the_padding()
    {
        var path = WriteFile("photo.tif", Tiff(magic: 42));
        RatingMetadata.WriteRating(path, 3, embedInFile: true);
        var length = new FileInfo(path).Length;

        RatingMetadata.WriteRating(path, 5, embedInFile: true);

        Assert.AreEqual(length, new FileInfo(path).Length);
        Assert.AreEqual(5, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Dng_embeds_like_tiff()
    {
        var path = WriteFile("photo.dng", Tiff(magic: 42));

        RatingMetadata.WriteRating(path, 4, embedInFile: true);

        Assert.AreEqual(4, RatingMetadata.ReadRating(path));
        Assert.IsFalse(File.Exists(path + ".xmp"));
    }

    [TestMethod]
    public void BigTiff_falls_back_to_a_sidecar()
    {
        var original = Tiff(magic: 43);
        var path = WriteFile("photo.tif", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 1, embedInFile: true).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(path + ".xmp"));
    }

    // ---------- RAW ----------

    [TestMethod]
    public void Raw_gets_the_adobe_sidecar_and_is_never_changed()
    {
        var original = Encoding.ASCII.GetBytes("not really a CR2");
        var path = WriteFile("IMG_1.CR2", original);

        Assert.IsTrue(RatingMetadata.WriteRating(path, 4, embedInFile: true).Success);

        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(PathOf("IMG_1.xmp")));
        Assert.IsFalse(File.Exists(path + ".xmp"), "IMG_1.CR2.xmp is only kept in step, never created");
        Assert.AreEqual(4, RatingMetadata.ReadRating(path));
    }

    [TestMethod]
    public void Raw_keeps_an_existing_darktable_sidecar_in_step()
    {
        var path = WriteFile("IMG_1.CR2", [1, 2, 3]);
        File.WriteAllText(path + ".xmp", Packet("xmp:Rating=\"1\""));

        RatingMetadata.WriteRating(path, 4, embedInFile: true);

        StringAssert.Contains(File.ReadAllText(path + ".xmp"), "xmp:Rating=\"4\"");
        StringAssert.Contains(File.ReadAllText(PathOf("IMG_1.xmp")), "xmp:Rating=\"4\"");
    }

    [TestMethod]
    public void Raw_reads_the_adobe_sidecar_first_and_skips_a_corrupt_one()
    {
        var path = WriteFile("IMG_1.NEF", [1, 2, 3]);
        File.WriteAllText(PathOf("IMG_1.xmp"), Packet("xmp:Rating=\"2\""));
        File.WriteAllText(path + ".xmp", Packet("xmp:Rating=\"5\""));
        Assert.AreEqual(2, RatingMetadata.ReadRating(path));

        File.WriteAllText(PathOf("IMG_1.xmp"), "not xml <");
        Assert.AreEqual(5, RatingMetadata.ReadRating(path));
    }

    // ---------- formats Fly does not write into ----------

    [TestMethod]
    public void Heic_without_a_sidecar_is_unsupported_and_nothing_is_written()
    {
        var path = WriteFile("photo.heic", [1, 2, 3]);

        var result = RatingMetadata.WriteRating(path, 3, embedInFile: true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Unsupported);
        Assert.AreEqual(1, Directory.GetFiles(Folder).Length);
    }

    [TestMethod]
    public void Heic_sidecar_made_by_another_app_is_updated()
    {
        var path = WriteFile("photo.heic", [1, 2, 3]);
        File.WriteAllText(path + ".xmp", Packet("xmp:Rating=\"1\""));

        Assert.IsTrue(RatingMetadata.WriteRating(path, 3, embedInFile: true).Success);

        Assert.AreEqual(3, RatingMetadata.ReadRating(path));
    }

    // ---------- rename / delete housekeeping ----------

    [TestMethod]
    public void Renamed_sidecar_keeps_its_naming_style()
    {
        Assert.AreEqual(@"C:\b.jpg.xmp", RatingMetadata.RenamedSidecar(@"C:\a.jpg.xmp", @"C:\a.jpg", @"C:\b.jpg"));
        Assert.AreEqual(@"C:\B.xmp", RatingMetadata.RenamedSidecar(@"C:\A.xmp", @"C:\A.CR2", @"C:\B.CR2"));
    }

    [TestMethod]
    public void Shared_raw_sidecar_is_not_claimed_by_one_raw()
    {
        var cr2 = WriteFile("IMG_1.CR2", [1]);
        File.WriteAllText(PathOf("IMG_1.xmp"), Packet("xmp:Rating=\"1\""));
        CollectionAssert.AreEqual(new[] { PathOf("IMG_1.xmp") }, RatingMetadata.ExistingSidecars(cr2));

        WriteFile("IMG_1.NEF", [1]); // IMG_1.xmp now belongs to both RAWs
        Assert.AreEqual(0, RatingMetadata.ExistingSidecars(cr2).Count);
    }

    [TestMethod]
    public void Stars_are_clamped_text_stars()
    {
        Assert.AreEqual("★★★", RatingMetadata.Stars(3));
        Assert.AreEqual("★★★★★", RatingMetadata.Stars(9));
        Assert.AreEqual("", RatingMetadata.Stars(0));
    }

    // ---------- building test files ----------

    private string WriteFile(string name, byte[] bytes)
    {
        var path = PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Packet(string attributes) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        $"<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" {attributes}/></rdf:RDF></x:xmpmeta>";

    private static readonly byte[] JpegScan = [0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0x33, 0xFF, 0xD9]; // SOS, data, EOI

    private static byte[] Jpeg(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), .. JpegScan];

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    private static byte[] XmpSegment(string xml) =>
        Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.UTF8.GetBytes(xml)]);

    /// <summary>EXIF APP1 whose IFD0 holds one entry: Rating (0x4746), SHORT, inline value.</summary>
    private static byte[] ExifSegment(int exifRating)
    {
        var tiff = new byte[8 + 2 + 12 + 4];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x4746);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), (ushort)exifRating);
        return Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
    }

    /// <summary>Little-endian TIFF: header, IFD0 with ImageWidth = 1, then a few pixel bytes.</summary>
    private static byte[] Tiff(ushort magic)
    {
        var tiff = new byte[8 + 2 + 12 + 4 + 8];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), magic);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 256);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), 1);
        return tiff;
    }

    /// <summary>Signature, IHDR, IDAT and (when complete) IEND, with real CRCs.</summary>
    private static byte[] Png(bool complete)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        bytes.AddRange(PngChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]));
        bytes.AddRange(PngChunk("IDAT", [1, 2, 3, 4]));
        if (complete) bytes.AddRange(PngChunk("IEND", []));
        return bytes.ToArray();
    }

    private static byte[] PngChunk(string type, byte[] data)
    {
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        var crc = Crc32.Compute(typeAndData);
        return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
            .. typeAndData, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }
}
