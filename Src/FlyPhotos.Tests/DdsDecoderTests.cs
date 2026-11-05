using FlyPhotos.Infra.Interop;

namespace FlyPhotos.Tests;

/// <summary>
/// Runs FlyNativeLibDds.dll on tiny DDS files built here, byte by byte, so no test images live in the repo.
/// Output is premultiplied BGRA8.
/// </summary>
[TestClass]
public sealed class DdsDecoderTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "FlyPhotosDdsTests", Guid.NewGuid().ToString("N"));

    [ClassInitialize]
    public static void CreateDir(TestContext _) => Directory.CreateDirectory(Dir);

    [ClassCleanup]
    public static void DeleteDir() => Directory.Delete(Dir, recursive: true);

    /// <summary>A legacy DDS pixel format: DDS_PIXELFORMAT's flags, FourCC, bit count and channel masks.</summary>
    private sealed record PixelFormat(uint Flags, uint FourCC, uint Bits, uint R, uint G, uint B, uint A);

    private const uint AlphaPixels = 0x1, FourCC = 0x4, Rgb = 0x40;
    private static readonly PixelFormat Rgba8 = new(Rgb | AlphaPixels, 0, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
    private static readonly PixelFormat Bgra8 = new(Rgb | AlphaPixels, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
    private static readonly PixelFormat Dxt1 = new(FourCC, 0x31545844 /* "DXT1" */, 0, 0, 0, 0, 0);

    /// <summary>The "DDS " magic, a 124-byte DDS_HEADER, then <paramref name="pixels"/> as given.</summary>
    private static byte[] Dds(int width, int height, int mips, PixelFormat pf, byte[] pixels)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("DDS "u8);
        w.Write(124u);                                                  // dwSize
        w.Write(0x1u | 0x2 | 0x4 | 0x1000 | (mips > 1 ? 0x20000u : 0)); // CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT
        w.Write((uint)height);
        w.Write((uint)width);
        w.Write(0u);                                                    // pitch / linear size: DirectXTex computes it
        w.Write(0u);                                                    // depth
        w.Write((uint)mips);
        for (int i = 0; i < 11; i++) w.Write(0u);                       // dwReserved1
        w.Write(32u);                                                   // DDS_PIXELFORMAT.dwSize
        w.Write(pf.Flags); w.Write(pf.FourCC); w.Write(pf.Bits);
        w.Write(pf.R); w.Write(pf.G); w.Write(pf.B); w.Write(pf.A);
        w.Write(0x1000u | (mips > 1 ? 0x400008u : 0));                  // TEXTURE | MIPMAP | COMPLEX
        for (int i = 0; i < 4; i++) w.Write(0u);                        // caps2-4, dwReserved2
        w.Write(pixels);
        w.Flush();
        return ms.ToArray();
    }

    private static string Save(byte[] bytes, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        string path = Path.Combine(Dir, name + ".dds");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary><paramref name="count"/> copies of one 4-byte pixel.</summary>
    private static byte[] Fill(int count, params byte[] pixel) =>
        Enumerable.Repeat(pixel, count).SelectMany(p => p).ToArray();

    private static NativeDdsWrapper.DdsImage Decode(byte[] dds, int maxDimension = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        NativeDdsWrapper.Decode(Save(dds, name), maxDimension);

    private static DdsError Error(byte[] bytes, string name) =>
        NativeDdsBridge.ExtractDdsImage(Save(bytes, name), 0, out _);

    // ---- 1. Exact pixels ----------------------------------------------------------------------------------

    [TestMethod]
    public void Rgba_pixels_come_back_as_bgra()
    {
        // The #273 regression: red and blue must not swap.
        byte[] rgba = [255, 0, 0, 255, /**/ 0, 255, 0, 255, /**/ 0, 0, 255, 255];
        var img = Decode(Dds(3, 1, 1, Rgba8, rgba));

        Assert.AreEqual(3, img.Width);
        Assert.AreEqual(1, img.Height);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 255, 255, /**/ 0, 255, 0, 255, /**/ 255, 0, 0, 255 }, img.Pixels);
    }

    [TestMethod]
    public void Bgra_pixels_keep_their_channels()
    {
        byte[] bgra = [0, 0, 255, 255, /**/ 0, 255, 0, 255, /**/ 255, 0, 0, 255];
        CollectionAssert.AreEqual(bgra, Decode(Dds(3, 1, 1, Bgra8, bgra)).Pixels);
    }

    [TestMethod]
    public void Bc1_block_decodes_to_its_endpoint_colours()
    {
        // One 4x4 block: color0 = red (RGB565 0xF800) > color1 = blue (0x001F), so 4-colour mode.
        // Index rows, 2 bits per pixel: row 1 all index 1 (blue), the other rows index 0 (red).
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, /* indices */ 0x00, 0x55, 0x00, 0x00];
        var img = Decode(Dds(4, 4, 1, Dxt1, block));

        byte[] red = [0, 0, 255, 255], blue = [255, 0, 0, 255];
        byte[] expected = [.. Fill(4, red), .. Fill(4, blue), .. Fill(8, red)];
        CollectionAssert.AreEqual(expected, img.Pixels);
    }

    [TestMethod]
    public void Straight_alpha_is_premultiplied()
    {
        // Half-transparent red, fully transparent white, opaque green.
        byte[] rgba = [255, 0, 0, 128, /**/ 255, 255, 255, 0, /**/ 0, 255, 0, 255];
        var img = Decode(Dds(3, 1, 1, Rgba8, rgba));

        CollectionAssert.AreEqual(new byte[] { 0, 0, 128, 128, /**/ 0, 0, 0, 0, /**/ 0, 255, 0, 255 }, img.Pixels);
    }

    // ---- 2. Mip selection and sizing ----------------------------------------------------------------------

    private const int MipW = 256, MipH = 128, MipLevels = 9; // 256x128 down to 1x1

    /// <summary>Mip level <paramref name="i"/> is filled with RGBA (i * 25, 100, 200, 255), so a pixel names its level.</summary>
    private static byte[] MipChain()
    {
        var bytes = new List<byte>();
        for (int i = 0; i < MipLevels; i++)
            bytes.AddRange(Fill(Math.Max(1, MipW >> i) * Math.Max(1, MipH >> i), (byte)(i * 25), 100, 200, 255));
        return Dds(MipW, MipH, MipLevels, Rgba8, [.. bytes]);
    }

    private static byte[] MipColourBgra(int level) => [200, 100, (byte)(level * 25), 255];

    [TestMethod]
    public void Zero_max_dimension_gives_the_full_top_mip()
    {
        var img = Decode(MipChain(), maxDimension: 0);

        Assert.AreEqual((MipW, MipH), (img.Width, img.Height));
        Assert.AreEqual((MipW, MipH), (img.PrimaryImageWidth, img.PrimaryImageHeight));
        Assert.AreEqual(MipW * MipH * 4, img.Pixels.Length);
        CollectionAssert.AreEqual(MipColourBgra(0), img.Pixels[..4]);
    }

    [TestMethod]
    public void Preview_uses_the_stored_mip_that_exactly_fits()
    {
        // Mip 2 is 64x32: the smallest level whose longer side is still >= 64.
        var img = Decode(MipChain(), maxDimension: 64);

        Assert.AreEqual((64, 32), (img.Width, img.Height));
        Assert.AreEqual((MipW, MipH), (img.PrimaryImageWidth, img.PrimaryImageHeight));
        CollectionAssert.AreEqual(MipColourBgra(2), img.Pixels[..4]);
    }

    [TestMethod]
    public void Preview_between_mips_resizes_the_next_larger_mip()
    {
        // No level is exactly 100 wide: mip 1 (128x64) is picked and scaled down to 100x50.
        var img = Decode(MipChain(), maxDimension: 100);

        Assert.AreEqual((100, 50), (img.Width, img.Height));
        CollectionAssert.AreEqual(MipColourBgra(1), img.Pixels[..4]);
    }

    [TestMethod]
    public void Preview_without_mips_is_resized()
    {
        byte[] dds = Dds(MipW, MipH, 1, Rgba8, Fill(MipW * MipH, 10, 20, 30, 255));
        var img = Decode(dds, maxDimension: 64);

        Assert.AreEqual((64, 32), (img.Width, img.Height));
        Assert.AreEqual((MipW, MipH), (img.PrimaryImageWidth, img.PrimaryImageHeight));
        CollectionAssert.AreEqual(new byte[] { 30, 20, 10, 255 }, img.Pixels[..4]);
    }

    [TestMethod]
    public void Preview_larger_than_the_image_keeps_it_full_size()
    {
        var img = Decode(MipChain(), maxDimension: 4096);
        Assert.AreEqual((MipW, MipH), (img.Width, img.Height));
    }

    // ---- 3. Bad input returns an error, never crashes the process -----------------------------------------

    [TestMethod]
    public void Missing_file_is_a_read_error() =>
        Assert.AreEqual(DdsError.FileReadError,
            NativeDdsBridge.ExtractDdsImage(Path.Combine(Dir, "does_not_exist.dds"), 0, out _));

    [TestMethod]
    public void Malformed_files_return_an_error()
    {
        byte[] valid = Dds(64, 64, 1, Rgba8, Fill(64 * 64, 1, 2, 3, 255));
        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["magic_only"] = "DDS "u8.ToArray(),
            ["random_bytes"] = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray(),
            ["header_cut_short"] = valid[..60],
            ["pixels_cut_short"] = valid[..(128 + 100)],
            ["zero_width"] = Dds(0, 64, 1, Rgba8, Fill(64, 1, 2, 3, 255)),
            ["huge_bc1_with_no_data"] = Dds(16384, 16384, 1, Dxt1, []),
            ["unknown_fourcc"] = Dds(4, 4, 1, Dxt1 with { FourCC = 0x5A5A5A5A }, new byte[64]),
        };

        foreach (var (name, bytes) in cases)
            Assert.AreNotEqual(DdsError.Ok, Error(bytes, name), name);
    }

    [TestMethod]
    public void Wrapper_throws_on_a_decoder_error() =>
        Assert.ThrowsExactly<Exception>(() => Decode("not a dds file"u8.ToArray()));
}
