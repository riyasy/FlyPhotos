using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Display.State;
using D = FlyPhotos.Display.ImageReading.DecodeStep;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class DecoderChainsTests
{
    /// <summary>A case-insensitive extension set, as CodecDiscovery builds them.</summary>
    private static HashSet<string> Set(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);

    /// <summary>A small stand-in for what CodecDiscovery finds on a real machine.</summary>
    private static readonly CodecSupport Codecs = new(
        wic: Set(".jpg", ".png", ".gif", ".webp", ".bmp", ".tif", ".heic", ".nef", ".dng", ".jxl", ".dds"),
        wicRaw: Set(".nef", ".dng"),
        magick: Set(".jpg", ".png", ".heic", ".avif", ".psd", ".nef", ".dng", ".jxl", ".tga", ".dds"),
        magickRaw: Set(".nef", ".dng"),
        rawler: Set(".nef"));

    /// <summary>The app's default settings: embedded previews on, RAW decoders WIC → Rawler → Magick.</summary>
    private static readonly ReadOptions Defaults = new(false, [RawDecoder.WIC, RawDecoder.Rawler, RawDecoder.ImageMagick]);

    /// <summary>The chain for a file with this extension, its format taken from the extension.</summary>
    private static List<D> Chain(string ext, Purpose purpose, ReadOptions? options = null) =>
        DecoderChains.For(ImageFormats.FromExtension(ext), ext, purpose, options ?? Defaults, Codecs);

    [TestMethod]
    public void Heic_full_image_tries_libheif_then_wic_then_magick() =>
        CollectionAssert.AreEqual(new[] { D.HeifHq, D.WicHq, D.MagickHq }, Chain(".heic", Purpose.Hq));

    [TestMethod]
    public void Avif_skips_codecs_that_cannot_read_it() =>
        CollectionAssert.AreEqual(new[] { D.AvifHq, D.MagickHq }, Chain(".avif", Purpose.Hq));

    [TestMethod]
    public void First_photo_uses_embedded_preview_unless_open_exit_zoom()
    {
        CollectionAssert.AreEqual(new[] { D.HeifEmbeddedOnly, D.HeifHq, D.WicHq, D.MagickHq }, Chain(".heic", Purpose.FirstPreview));
        CollectionAssert.AreEqual(new[] { D.HeifHq, D.WicHq, D.MagickHq },
            Chain(".heic", Purpose.FirstPreview, Defaults with { SkipEmbedded = true }));
    }

    [TestMethod]
    public void First_heif_photo_never_generates_a_preview_but_the_strip_does()
    {
        CollectionAssert.AreEqual(new[] { D.HeifEmbeddedOnly, D.AvifHq, D.MagickHq }, Chain(".avif", Purpose.FirstPreview));
        CollectionAssert.AreEqual(new[] { D.HeifEmbeddedOnly, D.HeifResized, D.MagickResized }, Chain(".heic", Purpose.Preview));
    }

    // The light chain is what background loading runs at full parallelism; a file it can't serve goes to the
    // heavy lane (#276).

    [TestMethod]
    public void Heavy_lane_picks_the_pending_key_nearest_the_centre()
    {
        // Keys are not positions: position 0..5 holds these keys.
        int[] keys = [50, 51, 52, 53, 54, 55];

        // First photo on screen, keys pending in the order their light attempts ended (near to far): nearest wins.
        var pending = new HashSet<int> { 51, 53, 55 };
        Assert.AreEqual(51, KeyWindow.Nearest(keys, 0, 300, pending.Contains));

        // Centre in the middle: equal distance goes to the one before the centre.
        Assert.AreEqual(52, KeyWindow.Nearest(keys, 3, 300, new HashSet<int> { 52, 54 }.Contains));
        Assert.AreEqual(53, KeyWindow.Nearest(keys, 3, 300, new HashSet<int> { 53, 52 }.Contains));

        // Outside the window half, or nothing pending: none.
        Assert.IsNull(KeyWindow.Nearest(keys, 0, 2, new HashSet<int> { 55 }.Contains));
        Assert.IsNull(KeyWindow.Nearest(keys, 0, 300, _ => false));
        Assert.IsNull(KeyWindow.Nearest(keys, -1, 300, _ => true));
    }

    [TestMethod]
    public void Light_preview_of_heif_and_avif_is_the_stored_thumbnail_only()
    {
        CollectionAssert.AreEqual(new[] { D.HeifEmbeddedOnly }, Chain(".heic", Purpose.PreviewLight));
        CollectionAssert.AreEqual(new[] { D.HeifEmbeddedOnly }, Chain(".avif", Purpose.PreviewLight));
    }

    [TestMethod]
    public void Light_preview_stops_before_the_first_full_image_decode()
    {
        // JXL has no thumbnail and no reduced-size decode: only the quick thumbnail attempt is light.
        CollectionAssert.AreEqual(new[] { D.WicEmbedded, D.MagicScalerResized, D.MagickResized }, Chain(".jxl", Purpose.Preview));
        CollectionAssert.AreEqual(new[] { D.WicEmbedded }, Chain(".jxl", Purpose.PreviewLight));
        CollectionAssert.AreEqual(new[] { D.WicEmbedded, D.MagickRawEmbedded }, Chain(".nef", Purpose.PreviewLight));
        // ImageMagick only: nothing light at all.
        Assert.AreEqual(0, Chain(".tga", Purpose.PreviewLight).Count);
        // The DNG codec may return the full-size image as the thumbnail (iPhone ProRAW).
        Assert.AreEqual(0, Chain(".dng", Purpose.PreviewLight).Count);
    }

    [TestMethod]
    public void Light_preview_is_the_whole_chain_when_nothing_in_it_is_heavy()
    {
        foreach (var ext in new[] { ".png", ".gif", ".webp", ".bmp", ".tif", ".psd", ".svg", ".ico", ".dds" })
            CollectionAssert.AreEqual(Chain(ext, Purpose.Preview), Chain(ext, Purpose.PreviewLight), ext);
    }

    [TestMethod]
    public void Light_preview_of_jpeg_keeps_its_reduced_size_decode_and_drops_only_the_magick_fallback()
    {
        CollectionAssert.AreEqual(new[] { D.WicEmbedded, D.MagicScalerResized, D.MagickResized }, Chain(".jpg", Purpose.Preview));
        CollectionAssert.AreEqual(new[] { D.WicEmbedded, D.MagicScalerResized }, Chain(".jpg", Purpose.PreviewLight));
    }

    [TestMethod]
    public void Light_preview_is_a_prefix_of_the_preview_chain_without_heavy_steps()
    {
        foreach (var ext in new[] { ".jpg", ".png", ".gif", ".webp", ".bmp", ".tif", ".heic", ".avif", ".psd", ".svg",
                     ".ico", ".dds", ".nef", ".dng", ".jxl", ".tga", ".xyz" })
        {
            var format = ImageFormats.FromExtension(ext);
            var full = Chain(ext, Purpose.Preview);
            var light = Chain(ext, Purpose.PreviewLight);

            CollectionAssert.AreEqual(full.Take(light.Count).ToList(), light, ext);
            CollectionAssert.AreEqual(full.Skip(light.Count).ToList(), Chain(ext, Purpose.PreviewHeavy), ext);
            Assert.IsFalse(light.Any(step => DecoderChains.IsHeavy(step, format, ext)), ext);
            // What was cut starts with a heavy step: the light chain doesn't stop early.
            Assert.IsTrue(light.Count == full.Count || DecoderChains.IsHeavy(full[light.Count], format, ext), ext);
        }
    }

    [TestMethod]
    public void Raw_decoders_follow_the_user_priority()
    {
        CollectionAssert.AreEqual(new[] { D.WicRawHq, D.Rawler, D.MagickRawHq }, Chain(".nef", Purpose.Hq));
        CollectionAssert.AreEqual(new[] { D.MagickRawHq, D.Rawler, D.WicRawHq },
            Chain(".nef", Purpose.Hq, Defaults with { RawPriority = [RawDecoder.ImageMagick, RawDecoder.Rawler, RawDecoder.WIC] }));
    }

    [TestMethod]
    public void Raw_preview_tries_embedded_previews_before_resizing() =>
        CollectionAssert.AreEqual(new[] { D.WicEmbedded, D.MagickRawEmbedded, D.MagicScalerResized, D.MagickResized },
            Chain(".nef", Purpose.Preview));

    [TestMethod]
    public void Jpeg_aliases_get_the_same_chain() =>
        CollectionAssert.AreEqual(Chain(".jpg", Purpose.Hq), DecoderChains.For(ImageFormat.Jpeg, ".jpg", Purpose.Hq, Defaults, Codecs));

    [TestMethod]
    public void Dedicated_readers_for_animated_and_multi_page_formats()
    {
        CollectionAssert.AreEqual(new[] { D.Webp }, Chain(".webp", Purpose.Hq));
        CollectionAssert.AreEqual(new[] { D.Gif }, Chain(".gif", Purpose.FirstPreview));
        CollectionAssert.AreEqual(new[] { D.Tiff }, Chain(".tiff", Purpose.Hq));
        CollectionAssert.AreEqual(new[] { D.WicEmbedded, D.WicResized }, Chain(".tif", Purpose.Preview));
    }

    [TestMethod]
    public void Bmp_full_image_uses_the_generic_chain_but_preview_uses_wic_resize()
    {
        CollectionAssert.AreEqual(new[] { D.WicHq }, Chain(".bmp", Purpose.Hq));
        CollectionAssert.AreEqual(new[] { D.WicResized }, Chain(".bmp", Purpose.Preview));
    }

    [TestMethod]
    public void Oversized_images_try_libheif_only_for_heif_then_wic_then_magick()
    {
        CollectionAssert.AreEqual(new[] { D.LargeHeif, D.LargeWic, D.LargeMagick }, Chain(".heic", Purpose.Large));
        CollectionAssert.AreEqual(new[] { D.LargeWic, D.LargeMagick }, Chain(".jpg", Purpose.Large));
        CollectionAssert.AreEqual(new[] { D.LargeMagick }, Chain(".psd", Purpose.Large));
    }

    [TestMethod]
    public void Dds_uses_only_directxtex()
    {
        // WIC and Magick both claim .dds in the stand-in codec set; neither is tried.
        CollectionAssert.AreEqual(new[] { D.DdsHq }, Chain(".dds", Purpose.Hq));
        CollectionAssert.AreEqual(new[] { D.DdsHq }, Chain(".dds", Purpose.FirstPreview));
        CollectionAssert.AreEqual(new[] { D.DdsResized }, Chain(".dds", Purpose.Preview));
        Assert.AreEqual(0, Chain(".dds", Purpose.Large).Count);
    }

    [TestMethod]
    public void Raw_check_covers_every_raw_codec() =>
        Assert.IsTrue(Codecs.Raw(".nef") && !Codecs.Raw(".jpg"));

    [TestMethod]
    public void Unsupported_extension_gets_an_empty_chain() =>
        Assert.AreEqual(0, Chain(".xyz", Purpose.Hq).Count);

    [TestMethod]
    public void Mislabelled_avif_retries_with_the_avif_chain()
    {
        // What ImageFile does after the .webp chain fails and the header says AVIF.
        var actual = ImageFormats.Detect([0, 0, 0, 0x20, .."ftypavif"u8, 0, 0, 0, 0])!.Value;
        CollectionAssert.AreEqual(new[] { D.AvifHq, D.MagickHq },
            DecoderChains.For(actual, ImageFormats.ExtensionOf(actual), Purpose.Hq, Defaults, Codecs));
    }
}
