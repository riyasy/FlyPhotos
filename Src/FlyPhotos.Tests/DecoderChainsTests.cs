using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageReading;
using D = FlyPhotos.Display.ImageReading.DecodeStep;

namespace FlyPhotos.Tests;

[TestClass]
public sealed class DecoderChainsTests
{
    /// <summary>A case-insensitive extension set, as CodecDiscovery builds them.</summary>
    private static HashSet<string> Set(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);

    /// <summary>A small stand-in for what CodecDiscovery finds on a real machine.</summary>
    private static readonly CodecSupport Codecs = new(
        wic: Set(".jpg", ".png", ".gif", ".webp", ".bmp", ".tif", ".heic", ".nef"),
        wicRaw: Set(".nef"),
        magick: Set(".jpg", ".png", ".heic", ".avif", ".psd", ".nef", ".tga"),
        magickRaw: Set(".nef"),
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
        CollectionAssert.AreEqual(new[] { D.HeifEmbedded, D.HeifHq, D.WicHq, D.MagickHq }, Chain(".heic", Purpose.FirstPreview));
        CollectionAssert.AreEqual(new[] { D.HeifHq, D.WicHq, D.MagickHq },
            Chain(".heic", Purpose.FirstPreview, Defaults with { SkipEmbedded = true }));
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
