using FlyPhotos.Display.ImageReading;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class ImageFormatTests
{
    /// <summary>An ISO-BMFF header: an ftyp box with the major brand first, then compatible brands, then an mdat.</summary>
    private static byte[] Ftyp(params string[] brands)
    {
        // size, "ftyp", major brand, minor version, compatible brands
        var box = new List<byte>();
        box.AddRange(new byte[4]);
        box.AddRange("ftyp"u8.ToArray());
        box.AddRange(System.Text.Encoding.ASCII.GetBytes(brands[0]));
        box.AddRange(new byte[4]);
        foreach (var b in brands.Skip(1)) box.AddRange(System.Text.Encoding.ASCII.GetBytes(b));
        box[3] = (byte)box.Count;
        box.AddRange("....mdat"u8.ToArray()); // bytes after the box must not be read as brands
        return box.ToArray();
    }

    [TestMethod]
    public void Avif_header_from_issue_252_is_avif() =>
        Assert.AreEqual(ImageFormat.Avif, ImageFormats.Detect(
            [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66, 0, 0, 0, 0]));

    [TestMethod]
    public void Mif1_major_brand_with_avif_compatible_brand_is_avif() =>
        Assert.AreEqual(ImageFormat.Avif, ImageFormats.Detect(Ftyp("mif1", "miaf", "avif")));

    [TestMethod]
    public void Heic_brands_are_heif()
    {
        Assert.AreEqual(ImageFormat.Heif, ImageFormats.Detect(Ftyp("heic", "mif1", "heic")));
        Assert.AreEqual(ImageFormat.Heif, ImageFormats.Detect(Ftyp("mif1", "heic")));
    }

    [TestMethod]
    public void Minor_version_is_not_read_as_a_brand()
    {
        var header = Ftyp("isom");
        "avif"u8.CopyTo(header.AsSpan(12)); // minor version bytes spelling a brand
        Assert.IsNull(ImageFormats.Detect(header));
    }

    [TestMethod]
    public void Video_ftyp_is_not_an_image() => Assert.IsNull(ImageFormats.Detect(Ftyp("isom", "mp41")));

    [TestMethod]
    public void Common_signatures()
    {
        Assert.AreEqual(ImageFormat.Jpeg, ImageFormats.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.AreEqual(ImageFormat.Png, ImageFormats.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.AreEqual(ImageFormat.Gif, ImageFormats.Detect("GIF89a"u8));
        Assert.AreEqual(ImageFormat.Webp, ImageFormats.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
    }

    [TestMethod]
    public void Tiff_header_is_not_detected_so_raw_files_keep_their_raw_chain() =>
        Assert.IsNull(ImageFormats.Detect([0x49, 0x49, 0x2A, 0x00, 0x08, 0, 0, 0]));

    [TestMethod]
    public void Short_or_unknown_headers_are_null()
    {
        Assert.IsNull(ImageFormats.Detect([]));
        Assert.IsNull(ImageFormats.Detect([0xFF, 0xD8]));
        Assert.IsNull(ImageFormats.Detect("<?xml version"u8));
    }

    [TestMethod]
    public void Psd_bmp_and_ico_signatures()
    {
        Assert.AreEqual(ImageFormat.Psd, ImageFormats.Detect("8BPS\0\u0001"u8));
        Assert.AreEqual(ImageFormat.Bmp, ImageFormats.Detect([0x42, 0x4D, 0x36, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0]));
        Assert.AreEqual(ImageFormat.Ico, ImageFormats.Detect([0, 0, 1, 0, 2, 0, 16, 16]));
    }

    [TestMethod]
    public void Weak_bmp_and_ico_lookalikes_are_rejected()
    {
        // "BM" text with non-zero reserved bytes, and an icon directory with no images.
        Assert.IsNull(ImageFormats.Detect("BMP file, honest"u8));
        Assert.IsNull(ImageFormats.Detect([0, 0, 1, 0, 0, 0, 16, 16]));
    }

    [TestMethod]
    public void Ftyp_box_of_256_bytes_is_not_mistaken_for_an_icon()
    {
        var header = Ftyp("avif");
        header[2] = 1; header[3] = 0; // box size 0x100, so the header starts 00 00 01 00 like an icon
        Assert.AreEqual(ImageFormat.Avif, ImageFormats.Detect(header));
    }

    [TestMethod]
    public void Extension_aliases_share_a_format()
    {
        Assert.AreEqual(ImageFormat.Jpeg, ImageFormats.FromExtension(".jpeg"));
        Assert.AreEqual(ImageFormat.Jpeg, ImageFormats.FromExtension(".JFIF"));
        Assert.AreEqual(ImageFormat.Tiff, ImageFormats.FromExtension(".tif"));
        Assert.AreEqual(ImageFormat.Heif, ImageFormats.FromExtension(".hif"));
        Assert.AreEqual(ImageFormat.Other, ImageFormats.FromExtension(".nef"));
        Assert.AreEqual(ImageFormat.Other, ImageFormats.FromExtension(""));
    }

    [TestMethod]
    public void Detected_format_maps_back_to_its_own_extension()
    {
        foreach (var f in Enum.GetValues<ImageFormat>().Where(f => f != ImageFormat.Other))
            Assert.AreEqual(f, ImageFormats.FromExtension(ImageFormats.ExtensionOf(f)));
    }
}
