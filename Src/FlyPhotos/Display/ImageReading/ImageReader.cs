#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Loads display items for a path. Each load opens one <see cref="ImageFile"/>, runs the decoder chain that
/// <see cref="DecoderChains"/> picks for its format, and only when that fails looks further: at the header
/// (a mislabelled file) and, for the full image, at the size (larger than the GPU can hold).
/// </summary>
internal static class ImageReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Provides pre-rendered indicator bitmaps (loading, error, file-not-found) used as fallback display items.</summary>
    private static IndicatorFactory _indicators = null!;

    /// <summary>
    /// Initializes shared resources that depend on the Win2D canvas, specifically the <see cref="IndicatorFactory"/>.
    /// Must be called once before any other image loading methods.
    /// </summary>
    public static void Initialize(ICanvasResourceCreatorWithDpi d2dCanvas)
    {
        _indicators = new IndicatorFactory(d2dCanvas);
    }

    /// <summary>
    /// Loads the best available display item for the first photo: a fast embedded preview where the format
    /// has one, otherwise the full image.
    /// </summary>
    public static async Task<DisplayItem> GetFirstPreviewSpecialHandlingAsync(
        ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        if (!File.Exists(path)) return new StaticHqDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen);
        if (!AppConfig.Settings.OpenExitZoom && await FromDiskCache(d2dCanvas, path) is { } cached) return cached;

        using var file = new ImageFile(path);
        if (await Decode(d2dCanvas, file, Purpose.FirstPreview) is { } item) return item;

        // For PNG, GIF, TIFF, etc. the first-photo chain is the full-image readers, which all fail on an
        // image over the GPU's maximum bitmap size. Return a preview instead, so the caller's HQ load
        // (GetHqImage) runs next and takes the large-image path while the preview is on screen.
        if (await IsOversized(d2dCanvas, file) && await Decode(d2dCanvas, file, Purpose.Preview) is { } preview)
            return preview;
        return HqFailed();
    }

    /// <summary>
    /// Loads a low-resolution preview of an image suitable for the thumbnail ribbon or preview: the disk
    /// cache, else an embedded preview (EXIF thumbnail, embedded JPEG), else a resized decode.
    /// </summary>
    public static async Task<PreviewDisplayItem> GetPreview(ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        if (!File.Exists(path)) return new PreviewDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen);
        if (await FromDiskCache(d2dCanvas, path) is { } cached) return cached;

        using var file = new ImageFile(path);
        return await Decode(d2dCanvas, file, Purpose.Preview) as PreviewDisplayItem ??
               new PreviewDisplayItem(_indicators.PreviewFailed, Origin.ErrorScreen);
    }

    /// <summary>
    /// Loads the full high-quality image for display in the main viewer. Images over the GPU's maximum bitmap
    /// size fail every normal decoder, so they fall through to <see cref="LargeImageReader"/> as the last step.
    /// </summary>
    /// <param name="knownOversized">
    /// The caller already knows the image is too big (preview metadata, or an earlier call's
    /// <c>Oversized</c>), so the doomed normal decode is skipped.
    /// </param>
    /// <param name="allowLarge">
    /// False when prefetching a neighbour: an oversized image's tiles could take gigabytes, so it is not
    /// decoded yet.
    /// </param>
    /// <param name="ct">Cancels a large decode (the user moved on). Normal decodes don't observe it.</param>
    /// <returns>
    /// <c>Item</c>: the image, or the error screen when nothing could read it; null when an oversized image
    /// was not allowed here or its decode was cancelled, so the caller retries later. <c>Oversized</c>: the
    /// image is too big for a single GPU bitmap; pass it back as <paramref name="knownOversized"/>.
    /// </returns>
    public static async Task<(HqDisplayItem? Item, bool Oversized)> GetHqImage(ICanvasResourceCreatorWithDpi d2dCanvas,
        string path, bool knownOversized = false, bool allowLarge = true, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return (new StaticHqDisplayItem(_indicators.FileNotFound, Origin.ErrorScreen), false);

        using var file = new ImageFile(path);
        if (!knownOversized)
        {
            if (await Decode(d2dCanvas, file, Purpose.Hq, ct: CancellationToken.None) is HqDisplayItem hq) return (hq, false);
            // Preview metadata can't always tell up front (missing, or cached by an older build), so a failure
            // may mean "too big". The size probe keeps a genuinely broken file on the error path.
            if (!await IsOversized(d2dCanvas, file)) return (HqFailed(), false);
        }

        if (!allowLarge) return (null, true);
        try
        {
            if (await Decode(d2dCanvas, file, Purpose.Large, ct: ct) is HqDisplayItem large) return (large, true);
        }
        catch (OperationCanceledException)
        {
            return (null, true);
        }

        // No decoder could read it at any size: show the normal error screen.
        return (HqFailed(), true);
    }

    /// <summary>Returns a loading indicator display item to show while an image is being asynchronously decoded.</summary>
    public static DisplayItem GetLoadingIndicator() => new PreviewDisplayItem(_indicators.Loading, Origin.ErrorScreen);

    /// <summary>
    /// Runs the decoder chain for the file's format; null when every decoder failed. Only then is the header
    /// read: a file whose content doesn't match its extension is decoded once more as what it really is.
    /// </summary>
    /// <param name="onAttempt">Called before each reader runs (the profiler counts and names them).</param>
    /// <param name="ct">Cancels a <see cref="Purpose.Large"/> decode; surfaces as OperationCanceledException.</param>
    internal static async Task<DisplayItem?> Decode(ICanvasResourceCreatorWithDpi ctrl, ImageFile file,
        Purpose purpose, Action<DecodeStep>? onAttempt = null, CancellationToken ct = default)
    {
        var chain = ChainFor(file, purpose);
        if (await RunChain(ctrl, file, chain, onAttempt, ct) is { } item) return item;
        if (!await file.CorrectFormatFromHeaderAsync()) return null;

        // Content whose format gets the same chain (e.g. JPEG data in a .mpo) would only fail the same way.
        var corrected = ChainFor(file, purpose);
        return corrected.SequenceEqual(chain) ? null : await RunChain(ctrl, file, corrected, onAttempt, ct);
    }

    /// <summary>The decoder chain for the file's current format, with the user's settings.</summary>
    private static List<DecodeStep> ChainFor(ImageFile file, Purpose purpose) =>
        DecoderChains.For(file.Format, file.Extension, purpose,
            new ReadOptions(AppConfig.Settings.OpenExitZoom, AppConfig.Settings.RawDecoderPriority),
            CodecDiscovery.Support);

    /// <summary>
    /// Tries each step in order and returns the first item decoded, or null when all failed. A step that throws
    /// is logged and skipped; cancellation is not swallowed.
    /// </summary>
    private static async Task<DisplayItem?> RunChain(ICanvasResourceCreatorWithDpi ctrl, ImageFile file,
        List<DecodeStep> chain, Action<DecodeStep>? onAttempt, CancellationToken ct)
    {
        foreach (var step in chain)
        {
            onAttempt?.Invoke(step);
            if (ReadsByPath(step)) file.CloseStream();
            try
            {
                if (await Run(step, ctrl, file, ct) is { } item) return item;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error(ex, "{0} failed for {1}", step, file.Path);
            }
        }
        return null;
    }

    /// <summary>
    /// The steps whose reader opens the file itself from <c>file.Path</c> (native libraries) rather than using the
    /// shared stream. Must match the <see cref="Run"/> arms that pass <c>file.Path</c>.
    /// </summary>
    private static bool ReadsByPath(DecodeStep step) => step is
        DecodeStep.HeifEmbedded or DecodeStep.HeifHq or
        DecodeStep.MagickHq or DecodeStep.MagickRawHq or DecodeStep.MagickResized or DecodeStep.MagickRawEmbedded or
        DecodeStep.PsdEmbedded or DecodeStep.SvgHq or DecodeStep.SvgResized or DecodeStep.Rawler or
        DecodeStep.LargeHeif or DecodeStep.LargeMagick;

    /// <summary>Runs one reader; null when it couldn't read the file.</summary>
    internal static async Task<DisplayItem?> Run(DecodeStep step, ICanvasResourceCreatorWithDpi ctrl, ImageFile file,
        CancellationToken ct = default) =>
        step switch
        {
            DecodeStep.HeifEmbedded => ItemOrNull(NativeHeifReader.GetEmbedded(ctrl, file.Path)),
            DecodeStep.HeifHq => ItemOrNull(NativeHeifReader.GetHq(ctrl, file.Path)),
            DecodeStep.AvifHq => ItemOrNull(await NativeAvifReader.GetHq(ctrl, file)),
            DecodeStep.WicEmbedded => ItemOrNull(await WicReader.GetEmbedded(ctrl, file)),
            DecodeStep.WicResized => ItemOrNull(await WicReader.GetResized(ctrl, file)),
            DecodeStep.WicHq => ItemOrNull(await WicReader.GetHq(ctrl, file)),
            DecodeStep.WicRawHq => ItemOrNull(await WicReader.GetHq(ctrl, file, isRaw: true)),
            DecodeStep.MagickHq => ItemOrNull(await MagickNetWrap.GetHq(ctrl, file.Path)),
            DecodeStep.MagickRawHq => ItemOrNull(await MagickNetWrap.GetHq(ctrl, file.Path, isRaw: true)),
            DecodeStep.MagickResized => ItemOrNull(await MagickNetWrap.GetResized(ctrl, file.Path)),
            DecodeStep.MagickRawEmbedded => ItemOrNull(await MagickNetWrap.GetEmbeddedForRawFile(ctrl, file.Path)),
            DecodeStep.MagicScalerResized => ItemOrNull(await MagicScalerWrap.GetResized(ctrl, file)),
            DecodeStep.PsdEmbedded => ItemOrNull(await PsdReader.GetEmbedded(ctrl, file.Path)),
            DecodeStep.SvgHq => ItemOrNull(ResvgWrap.GetHq(ctrl, file.Path)),
            DecodeStep.SvgResized => ItemOrNull(ResvgWrap.GetResized(ctrl, file.Path)),
            DecodeStep.Gif => ItemOrNull(await GifReader.GetHq(ctrl, file)),
            DecodeStep.Webp => ItemOrNull(await WebpReader.GetHq(ctrl, file)),
            DecodeStep.Png => ItemOrNull(await PngReader.GetHq(ctrl, file)),
            DecodeStep.Tiff => ItemOrNull(await TiffReader.GetHq(ctrl, file)),
            DecodeStep.IcoHq => ItemOrNull(await IcoReader.GetHq(ctrl, file)),
            DecodeStep.IcoPreview => ItemOrNull(await IcoReader.GetPreview(ctrl, file)),
            DecodeStep.Rawler => ItemOrNull(RawlerWrapper.GetHq(ctrl, file.Path)),
            DecodeStep.LargeHeif => LargeImageReader.DecodeHeif(ctrl, file.Path, ct),
            DecodeStep.LargeWic => await LargeImageReader.DecodeWic(ctrl, file, ct),
            DecodeStep.LargeMagick => LargeImageReader.DecodeMagick(ctrl, file.Path, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
        };

    /// <summary>A reader's (success, item) result as the item, or null when it failed.</summary>
    private static DisplayItem? ItemOrNull<T>((bool Ok, T Item) result) where T : DisplayItem =>
        result.Ok ? result.Item : null;

    /// <summary>The file's cached preview, or null on a cache miss.</summary>
    private static async Task<PreviewDisplayItem?> FromDiskCache(ICanvasResourceCreatorWithDpi d2dCanvas, string path)
    {
        var (bitmap, width, height) = await DiskCacherWithSqlite.Instance.ReturnFromCache(d2dCanvas, path);
        return bitmap == null ? null : new PreviewDisplayItem(bitmap, Origin.DiskCache, new ImageMetadata(width, height));
    }

    /// <summary>
    /// Whether a failed decode was caused by the image being over the GPU's maximum bitmap size rather than
    /// by a broken file. Only failures pay for this size probe.
    /// </summary>
    private static Task<bool> IsOversized(ICanvasResourceCreatorWithDpi d2dCanvas, ImageFile file) =>
        LargeImageReader.IsOversizedAsync(file, d2dCanvas.Device.MaximumBitmapSizeInPixels);

    /// <summary>The error screen shown when no decoder could read the image.</summary>
    private static StaticHqDisplayItem HqFailed() => new(_indicators.HqFailed, Origin.ErrorScreen);
}
