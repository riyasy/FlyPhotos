using System.Runtime.InteropServices;
using FlyPhotos.Infra.Interop;

namespace FlyPhotos.Tests;

/// <summary>
/// Runs FlyNativeLibHeif.dll (libheif + libde265 for HEIC, dav1d for AVIF) on the small fixtures in
/// TestData\Heif, made by make_fixtures.py there. Output is straight (not premultiplied) RGBA8.
/// The fixtures are lossy-coded flat colours, so colours are compared with a tolerance.
/// </summary>
[TestClass]
public sealed class HeifDecoderTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "FlyPhotosHeifTests", Guid.NewGuid().ToString("N"));

    [ClassInitialize]
    public static void CreateDir(TestContext _) => Directory.CreateDirectory(Dir);

    [ClassCleanup]
    public static void DeleteDir() => Directory.Delete(Dir, recursive: true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Heif", name);

    private static readonly byte[] Red = [255, 0, 0], Green = [0, 255, 0], Blue = [0, 0, 255], White = [255, 255, 255];
    private const int Tolerance = 12;

    /// <summary>Asserts the RGBA pixel at (x, y) is <paramref name="rgb"/> within the tolerance, with alpha <paramref name="alpha"/>.</summary>
    private static void AssertPixel(byte[] rgba, int width, int x, int y, byte[] rgb, int alpha = 255)
    {
        int i = (y * width + x) * 4;
        byte[] actual = rgba[i..(i + 4)];
        byte[] expected = [.. rgb, (byte)alpha];
        for (int c = 0; c < 4; c++)
            Assert.IsTrue(Math.Abs(actual[c] - expected[c]) <= Tolerance,
                $"pixel ({x},{y}): expected {string.Join(",", expected)}, got {string.Join(",", actual)}");
    }

    private static void AssertQuadrants(NativeHeifWrapper.HeifImage img)
    {
        Assert.AreEqual((64, 64), (img.Width, img.Height));
        Assert.AreEqual((64, 64), (img.PrimaryImageWidth, img.PrimaryImageHeight));
        Assert.AreEqual(64 * 64 * 4, img.Pixels.Length);
        AssertPixel(img.Pixels, 64, 16, 16, Red);
        AssertPixel(img.Pixels, 64, 48, 16, Green);
        AssertPixel(img.Pixels, 64, 16, 48, Blue);
        AssertPixel(img.Pixels, 64, 48, 48, White);
    }

    private static string Save(byte[] bytes, string name)
    {
        string path = Path.Combine(Dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- Still images -------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("quadrants.heic")]
    [DataRow("quadrants.avif")]
    public void Still_image_decodes_to_rgba_in_the_right_places(string file)
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(Fixture(file), out bool animated);
        Assert.IsFalse(animated);
        AssertQuadrants(img);
    }

    [TestMethod]
    [DataRow("alpha.heic")]
    [DataRow("alpha.avif")]
    public void Alpha_comes_back_straight_not_premultiplied(string file)
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(Fixture(file));
        AssertPixel(img.Pixels, 32, 8, 16, Red, alpha: 255);
        AssertPixel(img.Pixels, 32, 24, 16, Blue, alpha: 128); // premultiplied would be ~0,0,128
    }

    [TestMethod]
    public void Odd_size_with_420_chroma_keeps_its_exact_size()
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(Fixture("odd_size_420.heic"));
        Assert.AreEqual((33, 17), (img.Width, img.Height));
        Assert.AreEqual(33 * 17 * 4, img.Pixels.Length);
        AssertPixel(img.Pixels, 33, 0, 0, Green);
        AssertPixel(img.Pixels, 33, 32, 16, Green); // last pixel: chroma planes are rounded up, the output is not
    }

    [TestMethod]
    public void Monochrome_decodes_to_grey_rgba() =>
        AssertPixel(NativeHeifWrapper.DecodePrimaryImage(Fixture("monochrome.heic")).Pixels, 16, 8, 8, [128, 128, 128]);

    [TestMethod]
    public void Ten_bit_decodes_to_eight_bit_rgba()
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(Fixture("10bit_red.heic"));
        Assert.AreEqual(16 * 16 * 4, img.Pixels.Length);
        AssertPixel(img.Pixels, 16, 8, 8, Red);
    }

    [TestMethod]
    public void Memory_decode_matches_file_decode()
    {
        string file = Fixture("quadrants.heic");
        var fromFile = NativeHeifWrapper.DecodePrimaryImage(file);
        var fromMemory = NativeHeifWrapper.DecodePrimaryImageFromMemory(File.ReadAllBytes(file), out bool animated);

        Assert.IsFalse(animated);
        Assert.AreEqual((fromFile.Width, fromFile.Height), (fromMemory.Width, fromMemory.Height));
        CollectionAssert.AreEqual(fromFile.Pixels, fromMemory.Pixels);
    }

    [TestMethod]
    public void Non_ascii_path_decodes()
    {
        // The DLL converts the UTF-16 path to UTF-8 for libheif.
        string path = Path.Combine(Dir, "Fotos_Café_日本_😀.heic");
        File.Copy(Fixture("quadrants.heic"), path);
        AssertQuadrants(NativeHeifWrapper.DecodePrimaryImage(path));
    }

    // ---- Thumbnails ---------------------------------------------------------------------------------------

    [TestMethod]
    public void Embedded_thumbnail_is_used()
    {
        var img = NativeHeifWrapper.DecodeThumbnail(Fixture("with_thumbnail.heic"));
        Assert.AreEqual((64, 32), (img.Width, img.Height));
        Assert.AreEqual((256, 128), (img.PrimaryImageWidth, img.PrimaryImageHeight));
        AssertPixel(img.Pixels, 64, 32, 16, Blue);
    }

    [TestMethod]
    public void Small_image_without_thumbnail_returns_the_primary_image() =>
        AssertQuadrants(NativeHeifWrapper.DecodeThumbnail(Fixture("quadrants.heic")));

    [TestMethod]
    public void Large_image_without_thumbnail_is_scaled_to_800()
    {
        var img = NativeHeifWrapper.DecodeThumbnail(Fixture("large_no_thumbnail.heic"));
        Assert.AreEqual((800, 400), (img.Width, img.Height));
        Assert.AreEqual((1000, 500), (img.PrimaryImageWidth, img.PrimaryImageHeight));
        Assert.AreEqual(800 * 400 * 4, img.Pixels.Length);
        AssertPixel(img.Pixels, 800, 400, 200, Red);
    }

    [TestMethod]
    public void Embedded_only_returns_the_stored_thumbnail()
    {
        var img = NativeHeifWrapper.DecodeThumbnail(Fixture("with_thumbnail.heic"), embeddedOnly: true);
        Assert.AreEqual((64, 32), (img.Width, img.Height));
        Assert.AreEqual((256, 128), (img.PrimaryImageWidth, img.PrimaryImageHeight));
    }

    [TestMethod]
    [DataRow("large_no_thumbnail.heic")]
    [DataRow("quadrants.heic")]
    [DataRow("quadrants.avif")]
    public void Embedded_only_without_a_thumbnail_returns_null(string file)
    {
        Assert.IsNull(NativeHeifWrapper.DecodeThumbnail(Fixture(file), embeddedOnly: true));
        Assert.AreEqual(HeifError.NoThumbnailFound, NativeHeifBridge.ExtractEmbeddedThumbnail(Fixture(file), out _));
    }

    // ---- Animated AVIF ------------------------------------------------------------------------------------

    /// <summary>Owns a pinned copy of the file and an <c>OpenAvifAnimation</c> handle, like AvifAnimator does.</summary>
    private sealed class Animation : IDisposable
    {
        private GCHandle _pin;
        public IntPtr Handle { get; }

        public Animation(byte[] bytes)
        {
            _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            Handle = NativeAvifBridge.OpenAvifAnimation(_pin.AddrOfPinnedObject(), (nuint)bytes.Length);
        }

        public (int durationMs, byte[] rgba) Next()
        {
            var frame = new byte[NativeAvifBridge.GetAvifCanvasWidth(Handle) * NativeAvifBridge.GetAvifCanvasHeight(Handle) * 4];
            var pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try { return (NativeAvifBridge.DecodeNextAvifFrame(Handle, pin.AddrOfPinnedObject()), frame); }
            finally { pin.Free(); }
        }

        public void Dispose()
        {
            NativeAvifBridge.CloseAvifAnimation(Handle);
            _pin.Free();
        }
    }

    [TestMethod]
    public void Animated_avif_is_reported_as_animated_and_its_first_frame_decodes()
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(Fixture("animated_rgb.avif"), out bool animated);
        Assert.IsTrue(animated);
        Assert.AreEqual((32, 32), (img.Width, img.Height));
        AssertPixel(img.Pixels, 32, 16, 16, Red);

        NativeHeifWrapper.DecodePrimaryImageFromMemory(File.ReadAllBytes(Fixture("animated_rgb.avif")), out bool fromMemory);
        Assert.IsTrue(fromMemory);
    }

    // animated_rgb_once.avif plays once, so the sequence ends; animated_rgb.avif loops forever, and libheif
    // follows its edit list and wraps around instead of ending.

    [TestMethod]
    public void Animation_frames_come_in_order_then_end()
    {
        using var anim = new Animation(File.ReadAllBytes(Fixture("animated_rgb_once.avif")));
        Assert.AreNotEqual(IntPtr.Zero, anim.Handle);
        Assert.IsTrue(NativeAvifBridge.IsAvifAnimated(anim.Handle));
        Assert.AreEqual(32, NativeAvifBridge.GetAvifCanvasWidth(anim.Handle));
        Assert.AreEqual(32, NativeAvifBridge.GetAvifCanvasHeight(anim.Handle));

        foreach (var colour in new[] { Red, Green, Blue })
        {
            var (duration, rgba) = anim.Next();
            Assert.IsTrue(duration > 0);
            AssertPixel(rgba, 32, 16, 16, colour);
        }

        Assert.AreEqual(0, anim.Next().durationMs, "past the last frame");
        Assert.AreEqual(0, anim.Next().durationMs, "still at the end");
    }

    [TestMethod]
    [Ignore("libheif 1.23.0 bug: heif_image_get_duration() on a decoded sequence frame always returns the first " +
            "sample's duration (100 ms here), while heif_raw_sequence_sample_get_duration() gives the right 100/200/300.")]
    public void Animation_frames_report_their_own_durations()
    {
        using var anim = new Animation(File.ReadAllBytes(Fixture("animated_rgb_once.avif")));
        CollectionAssert.AreEqual(new[] { 100, 200, 300 }, new[] { anim.Next().durationMs, anim.Next().durationMs, anim.Next().durationMs });
    }

    [TestMethod]
    public void Reset_after_the_end_restarts_at_the_first_frame()
    {
        using var anim = new Animation(File.ReadAllBytes(Fixture("animated_rgb_once.avif")));
        for (int i = 0; i < 3; i++) anim.Next();
        Assert.AreEqual(0, anim.Next().durationMs);

        NativeAvifBridge.ResetAvifAnimation(anim.Handle);
        var (duration, rgba) = anim.Next();
        Assert.IsTrue(duration > 0);
        AssertPixel(rgba, 32, 16, 16, Red);
    }

    [TestMethod]
    public void Reset_mid_sequence_restarts_at_the_first_frame()
    {
        using var anim = new Animation(File.ReadAllBytes(Fixture("animated_rgb_once.avif")));
        anim.Next();
        anim.Next();
        NativeAvifBridge.ResetAvifAnimation(anim.Handle);
        AssertPixel(anim.Next().rgba, 32, 16, 16, Red);
    }

    [TestMethod]
    public void Infinitely_looping_animation_wraps_around()
    {
        using var anim = new Animation(File.ReadAllBytes(Fixture("animated_rgb.avif")));
        byte[][] expected = [Red, Green, Blue, Red, Green, Blue, Red];
        foreach (var colour in expected)
        {
            var (duration, rgba) = anim.Next();
            Assert.IsTrue(duration > 0, "a looping animation never ends");
            AssertPixel(rgba, 32, 16, 16, colour);
        }
    }

    [TestMethod]
    public void Still_avif_opened_as_an_animation_has_no_frames()
    {
        using var anim = new Animation(File.ReadAllBytes(Fixture("quadrants.avif")));
        Assert.AreNotEqual(IntPtr.Zero, anim.Handle);
        Assert.IsFalse(NativeAvifBridge.IsAvifAnimated(anim.Handle));
        Assert.AreEqual(64, NativeAvifBridge.GetAvifCanvasWidth(anim.Handle));
        Assert.AreEqual(0, anim.Next().durationMs);
    }

    [TestMethod]
    public void Animation_calls_with_a_null_handle_are_harmless()
    {
        Assert.IsFalse(NativeAvifBridge.IsAvifAnimated(IntPtr.Zero));
        Assert.AreEqual(0, NativeAvifBridge.GetAvifCanvasWidth(IntPtr.Zero));
        Assert.AreEqual(0, NativeAvifBridge.GetAvifCanvasHeight(IntPtr.Zero));
        Assert.AreEqual(0, NativeAvifBridge.DecodeNextAvifFrame(IntPtr.Zero, IntPtr.Zero));
        NativeAvifBridge.ResetAvifAnimation(IntPtr.Zero);
        NativeAvifBridge.CloseAvifAnimation(IntPtr.Zero);
    }

    [TestMethod]
    public void Opening_garbage_as_an_animation_returns_a_null_handle()
    {
        using (var anim = new Animation(Enumerable.Range(0, 2048).Select(i => (byte)(i * 31 + 7)).ToArray()))
            Assert.AreEqual(IntPtr.Zero, anim.Handle);
        Assert.AreEqual(IntPtr.Zero, NativeAvifBridge.OpenAvifAnimation(IntPtr.Zero, 0));
    }

    // ---- Bad input returns an error, never crashes the process --------------------------------------------

    [TestMethod]
    public void Missing_file_is_a_read_error()
    {
        string missing = Path.Combine(Dir, "does_not_exist.heic");
        Assert.AreEqual(HeifError.FileReadError, NativeHeifBridge.ExtractPrimaryImageAndAnimationStatus(missing, out _, out _));
        Assert.AreEqual(HeifError.FileReadError, NativeHeifBridge.ExtractThumbnail(missing, out _));
        Assert.AreEqual(HeifError.FileReadError, NativeHeifBridge.ExtractEmbeddedThumbnail(missing, out _));
    }

    /// <summary>Broken inputs: empty, random bytes, a bare ftyp box, and valid files cut short.</summary>
    private static Dictionary<string, byte[]> Malformed()
    {
        byte[] heic = File.ReadAllBytes(Fixture("quadrants.heic"));
        byte[] avif = File.ReadAllBytes(Fixture("animated_rgb.avif"));
        return new()
        {
            ["empty"] = [],
            ["random_bytes"] = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 + 7)).ToArray(),
            ["ftyp_only"] = [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8],
            ["heic_header_only"] = heic[..64],
            ["heic_cut_in_half"] = heic[..(heic.Length / 2)],
            ["heic_last_byte_missing"] = heic[..^1],
            ["avif_cut_in_half"] = avif[..(avif.Length / 2)],
        };
    }

    [TestMethod]
    public void Malformed_files_return_an_error()
    {
        foreach (var (name, bytes) in Malformed())
        {
            string path = Save(bytes, name + ".heic");
            Assert.AreNotEqual(HeifError.Ok, NativeHeifBridge.ExtractPrimaryImageAndAnimationStatus(path, out _, out _), name);
            Assert.AreNotEqual(HeifError.Ok, NativeHeifBridge.ExtractThumbnail(path, out _), name);
            Assert.AreNotEqual(HeifError.Ok, NativeHeifBridge.ExtractEmbeddedThumbnail(path, out _), name);
        }
    }

    [TestMethod]
    public void Malformed_memory_returns_null()
    {
        foreach (var (name, bytes) in Malformed())
            Assert.IsNull(NativeHeifWrapper.DecodePrimaryImageFromMemory(bytes, out _), name);
    }

    [TestMethod]
    public void Malformed_animation_data_never_yields_frames()
    {
        foreach (var (name, bytes) in Malformed().Where(m => m.Value.Length > 0))
        {
            using var anim = new Animation(bytes);
            if (anim.Handle != IntPtr.Zero)
                Assert.AreEqual(0, anim.Next().durationMs, name);
        }
    }

    [TestMethod]
    public void Wrapper_throws_on_a_decoder_error() =>
        Assert.ThrowsExactly<Exception>(() => NativeHeifWrapper.DecodePrimaryImage(Save("not heif"u8.ToArray(), "text.heic")));
}
