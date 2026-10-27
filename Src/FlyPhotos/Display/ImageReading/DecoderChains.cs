#nullable enable
using System.Collections.Generic;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.ImageReading;

/// <summary>What the caller wants from a decode.</summary>
internal enum Purpose
{
    /// <summary>The first photo shown at launch: a fast embedded preview if there is one, else the full image.</summary>
    FirstPreview,
    /// <summary>A small preview for the thumbnail strip and the preview cache.</summary>
    Preview,
    /// <summary>The full image.</summary>
    Hq,
    /// <summary>The full image when it is larger than the GPU's maximum bitmap size: decoded into tiles.</summary>
    Large
}

/// <summary>
/// One step of a decoder chain: a single reader method to try (not to be confused with a WIC
/// <c>BitmapDecoder</c> or the user's <see cref="RawDecoder"/> setting). <see cref="ImageReader"/> maps each
/// to the reader method it names.
/// </summary>
internal enum DecodeStep
{
    HeifEmbedded, HeifHq, AvifHq,
    WicEmbedded, WicResized, WicHq, WicRawHq,
    MagickHq, MagickRawHq, MagickResized, MagickRawEmbedded,
    MagicScalerResized,
    PsdEmbedded, SvgHq, SvgResized,
    Gif, Webp, Png, Tiff, IcoHq, IcoPreview,
    Rawler,
    LargeHeif, LargeWic, LargeMagick
}

/// <summary>The user settings that change which decoders are tried.</summary>
/// <param name="SkipEmbedded">Open-exit-zoom: the first photo skips its low-res embedded preview.</param>
/// <param name="RawPriority">The order RAW decoders are tried in.</param>
internal sealed record ReadOptions(bool SkipEmbedded, IReadOnlyList<RawDecoder> RawPriority);

/// <summary>Which extensions each codec family can read. Built once by CodecDiscovery; tests build their own.</summary>
internal sealed class CodecSupport(
    IReadOnlySet<string> wic, IReadOnlySet<string> wicRaw,
    IReadOnlySet<string> magick, IReadOnlySet<string> magickRaw,
    IReadOnlySet<string> rawler)
{
    /// <summary>WIC can read the extension.</summary>
    public bool Wic(string ext) => wic.Contains(ext);

    /// <summary>A WIC RAW codec can read the extension.</summary>
    public bool WicRaw(string ext) => wicRaw.Contains(ext);

    /// <summary>WIC can read the extension, and not as RAW.</summary>
    public bool WicNonRaw(string ext) => wic.Contains(ext) && !wicRaw.Contains(ext);

    /// <summary>ImageMagick can read the extension.</summary>
    public bool Magick(string ext) => magick.Contains(ext);

    /// <summary>ImageMagick can read the extension as RAW.</summary>
    public bool MagickRaw(string ext) => magickRaw.Contains(ext);

    /// <summary>ImageMagick can read the extension, and not as RAW.</summary>
    public bool MagickNonRaw(string ext) => magick.Contains(ext) && !magickRaw.Contains(ext);

    /// <summary>Rawler can read the extension.</summary>
    public bool Rawler(string ext) => rawler.Contains(ext);

    /// <summary>The extension is a camera RAW format for any of the RAW decoders.</summary>
    public bool Raw(string ext) => rawler.Contains(ext) || wicRaw.Contains(ext) || magickRaw.Contains(ext);
}

/// <summary>
/// Which decoders to try, in order, for a format and purpose. Pure: deciding is kept apart from decoding
/// (<see cref="ImageReader"/>) so the routing can be unit tested without files or a GPU.
/// </summary>
internal static class DecoderChains
{
    /// <summary>The decoders to try, in order, for a file of <paramref name="format"/> and the given purpose.</summary>
    /// <param name="extension">Used only by the codec-support checks of the generic chain.</param>
    public static List<DecodeStep> For(ImageFormat format, string extension, Purpose purpose, ReadOptions options,
        CodecSupport codecs)
    {
        var chain = new List<DecodeStep>(4);

        switch (purpose, format)
        {
            // PSD's embedded preview is used even with open-exit-zoom: its full decode is the slow Magick one.
            case (Purpose.FirstPreview, ImageFormat.Psd):
                Add(DecodeStep.PsdEmbedded);
                Add(DecodeStep.MagickHq);
                break;
            // The first photo shows a fast embedded preview where the format has one, then the full image.
            case (Purpose.FirstPreview or Purpose.Hq, _):
                if (purpose == Purpose.FirstPreview && !options.SkipEmbedded) AddEmbedded();
                AddFullImage();
                break;

            case (Purpose.Preview, ImageFormat.Heif or ImageFormat.Avif):
                Add(DecodeStep.HeifEmbedded);
                Add(DecodeStep.MagickResized, codecs.Magick(extension));
                break;
            case (Purpose.Preview, ImageFormat.Psd): Add(DecodeStep.PsdEmbedded); break;
            case (Purpose.Preview, ImageFormat.Svg): Add(DecodeStep.SvgResized); break;
            case (Purpose.Preview, ImageFormat.Gif or ImageFormat.Png or ImageFormat.Bmp or ImageFormat.Webp):
                Add(DecodeStep.WicResized);
                break;
            case (Purpose.Preview, ImageFormat.Ico): Add(DecodeStep.IcoPreview); break;
            case (Purpose.Preview, ImageFormat.Tiff):
                Add(DecodeStep.WicEmbedded);
                Add(DecodeStep.WicResized);
                break;
            // Tiled decoders for an image over the GPU's maximum bitmap size.
            case (Purpose.Large, _):
                Add(DecodeStep.LargeHeif, format is ImageFormat.Heif or ImageFormat.Avif);
                Add(DecodeStep.LargeWic, codecs.Wic(extension));
                Add(DecodeStep.LargeMagick, codecs.Magick(extension));
                break;

            case (Purpose.Preview, _):
                Add(DecodeStep.WicEmbedded, codecs.Wic(extension));
                Add(DecodeStep.MagickRawEmbedded, codecs.MagickRaw(extension));
                Add(DecodeStep.MagicScalerResized, codecs.Wic(extension));
                Add(DecodeStep.MagickResized, codecs.Magick(extension));
                break;
        }
        return chain;

        // Appends the step when its condition holds.
        void Add(DecodeStep step, bool when = true)
        {
            if (when) chain.Add(step);
        }

        // The fast embedded preview, for formats that carry one.
        void AddEmbedded()
        {
            switch (format)
            {
                case ImageFormat.Heif or ImageFormat.Avif:
                    Add(DecodeStep.HeifEmbedded);
                    break;
                case ImageFormat.Jpeg or ImageFormat.Bmp or ImageFormat.Other:
                    Add(DecodeStep.WicEmbedded, codecs.Wic(extension));
                    Add(DecodeStep.MagickRawEmbedded, codecs.MagickRaw(extension));
                    break;
            }
        }

        // The full-image decoders for the format.
        void AddFullImage()
        {
            switch (format)
            {
                case ImageFormat.Heif or ImageFormat.Avif:
                    Add(format == ImageFormat.Heif ? DecodeStep.HeifHq : DecodeStep.AvifHq);
                    Add(DecodeStep.WicHq, codecs.Wic(extension));
                    Add(DecodeStep.MagickHq, codecs.Magick(extension));
                    break;
                case ImageFormat.Psd: Add(DecodeStep.MagickHq); break;
                case ImageFormat.Svg: Add(DecodeStep.SvgHq); break;
                case ImageFormat.Gif: Add(DecodeStep.Gif); break;
                case ImageFormat.Webp: Add(DecodeStep.Webp); break;
                case ImageFormat.Png: Add(DecodeStep.Png); break;
                case ImageFormat.Ico: Add(DecodeStep.IcoHq); break;
                case ImageFormat.Tiff: Add(DecodeStep.Tiff); break;
                // Generic chain (JPEG, BMP, RAW and the long tail), keyed by what each codec says it supports.
                default:
                    AddRawDecoders();
                    Add(DecodeStep.WicHq, codecs.WicNonRaw(extension));
                    Add(DecodeStep.MagickHq, codecs.MagickNonRaw(extension));
                    break;
            }
        }

        // The RAW decoders that read the extension, in the user's priority order.
        void AddRawDecoders()
        {
            foreach (var raw in options.RawPriority)
            {
                switch (raw)
                {
                    case RawDecoder.Rawler: Add(DecodeStep.Rawler, codecs.Rawler(extension)); break;
                    case RawDecoder.WIC: Add(DecodeStep.WicRawHq, codecs.WicRaw(extension)); break;
                    case RawDecoder.ImageMagick: Add(DecodeStep.MagickRawHq, codecs.MagickRaw(extension)); break;
                }
            }
        }
    }
}
