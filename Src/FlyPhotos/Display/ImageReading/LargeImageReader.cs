#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Services;
using ImageMagick;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Decodes images larger than the GPU's maximum bitmap size into a CPU-side <see cref="TilePyramid"/>,
/// drawn by TiledImageRenderer. WIC decodes in one pass when the image fits a byte[] and in tile-row
/// strips beyond that; Magick always goes in strips; libheif only offers a whole-image decode.
/// Cancellation (the user left the photo) is honoured between strips and pyramid levels, and surfaces as
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal static class LargeImageReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Decodes the file into a tile pyramid and uploads its top level as the overview bitmap. Tries libheif
    /// (HEIF/AVIF only), then WIC, then Magick, each only for extensions it supports.
    /// </summary>
    /// <returns>The tiled display item, or null when no decoder could read the file.</returns>
    /// <exception cref="OperationCanceledException">The user left the photo before the decode finished.</exception>
    public static async Task<TiledHqDisplayItem?> GetHq(ICanvasResourceCreatorWithDpi ctrl, string path,
        CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path);

        TilePyramid? pyramid = null;
        if (CodecDiscovery.IsHeif(ext)) pyramid = await Try(path, "libheif", () => Task.FromResult(DecodeHeif(path, ct)));
        if (pyramid == null && CodecDiscovery.IsWicSupported(ext)) pyramid = await Try(path, "WIC", () => DecodeWic(path, ct));
        if (pyramid == null && CodecDiscovery.IsMagickSupported(ext)) pyramid = await Try(path, "Magick", () => Task.FromResult(DecodeMagick(path, ct)));
        if (pyramid == null) return null;

        var top = pyramid.Levels[^1];
        var overview = CanvasBitmap.CreateFromBytes(ctrl.Device, top.ToPackedPixels(), top.Width, top.Height,
            pyramid.Format, 96f);
        return new TiledHqDisplayItem(overview, Origin.Disk, pyramid);
    }

    /// <summary>
    /// Header-only size check (no pixel decode): WIC, then a Magick ping for what WIC can't read.
    /// Only called after a normal HQ read has failed, so normal images never pay for it. False when the
    /// size can't be read, i.e. the file is broken rather than too big.
    /// </summary>
    public static async Task<bool> IsOversizedAsync(string path, int maxSize)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(path, false);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return decoder.PixelWidth > maxSize || decoder.PixelHeight > maxSize;
        }
        catch (Exception) { /* not WIC-readable; try Magick */ }

        try
        {
            var info = new MagickImageInfo(path);
            return info.Width > maxSize || info.Height > maxSize;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs one decoder, logging and swallowing its failure (as null) so the next decoder gets a turn.
    /// Cancellation is not swallowed.
    /// </summary>
    private static async Task<TilePyramid?> Try(string path, string decoder, Func<Task<TilePyramid?>> decode)
    {
        try
        {
            return await decode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "LargeImageReader - {0} failed for {1}", decoder, path);
            return null;
        }
    }

    /// <summary>
    /// Decodes a HEIF/AVIF with native libheif. libheif returns the whole image as one RGBA byte[], so this
    /// fails (and Magick takes over) when the image is larger than a .NET array can hold.
    /// </summary>
    /// <returns>An RGBA pyramid, or null when libheif returned no pixels.</returns>
    private static TilePyramid? DecodeHeif(string path, CancellationToken ct)
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(path);
        if (img?.Pixels is not { Length: > 0 } pixels) return null;
        ct.ThrowIfCancellationRequested();
        var level0 = new TileLevel(img.Width, img.Height);
        level0.FillAll(pixels);
        return TilePyramid.Build(level0, DirectXPixelFormat.R8G8B8A8UIntNormalized, ct);
    }

    /// <summary>
    /// Decodes frame 0 with WIC, EXIF orientation applied and colour-managed to sRGB. One pass when the
    /// image fits a byte[], otherwise one strip per tile row.
    /// </summary>
    /// <returns>A premultiplied BGRA pyramid.</returns>
    private static async Task<TilePyramid?> DecodeWic(string path, CancellationToken ct)
    {
        using var stream = await StorageOps.GetWin2DPerformantStream(path);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var frame = await decoder.GetFrameAsync(0);
        var level0 = new TileLevel((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);

        // WIC re-decodes from the top of the file for every strip (strip n costs ~n× strip 0), so decode
        // in one pass whenever the whole image fits in a byte[]. Strips only for the truly huge.
        if ((long)level0.Width * level0.Height * 4 <= Array.MaxLength)
        {
            var pixels = await GetPixels(frame, new BitmapTransform());
            ct.ThrowIfCancellationRequested();
            level0.FillAll(pixels);
        }
        else
        {
            // O(n²) in strips; a native sequential IWICBitmapFrameDecode::CopyPixels export fixes it if these are common.
            for (var row = 0; row < level0.Rows; row++)
            {
                ct.ThrowIfCancellationRequested();
                var (y0, rows) = level0.StripFor(row);
                // Bounds are in the oriented (post-EXIF-rotation) output space.
                var transform = new BitmapTransform
                {
                    Bounds = new BitmapBounds { X = 0, Y = (uint)y0, Width = (uint)level0.Width, Height = (uint)rows }
                };
                level0.FillTileRow(row, await GetPixels(frame, transform), y0);
            }
        }
        return TilePyramid.Build(level0, DirectXPixelFormat.B8G8R8A8UIntNormalized, ct);
    }

    /// <summary>
    /// Reads the pixels of the transform's bounds (the whole frame when unset) as premultiplied BGRA8,
    /// EXIF-oriented and in sRGB.
    /// </summary>
    private static async Task<byte[]> GetPixels(BitmapFrame frame, BitmapTransform transform)
    {
        var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return data.DetachPixelData();
    }

    /// <summary>
    /// Decodes with Magick.NET, the fallback for formats WIC can't read (PSD and others). Auto-oriented,
    /// reduced to 8-bit and premultiplied, then pulled out one tile-row strip at a time.
    /// </summary>
    /// <returns>A premultiplied BGRA pyramid, or null when Magick can't export a strip.</returns>
    private static TilePyramid? DecodeMagick(string path, CancellationToken ct)
    {
        using var image = new MagickImage(path);
        ct.ThrowIfCancellationRequested();
        image.AutoOrient();
        image.Depth = 8;
        image.Alpha(AlphaOption.Set);
        image.Alpha(AlphaOption.Associate); // premultiply, as MagickNetWrap.GetHq does
        var level0 = new TileLevel((int)image.Width, (int)image.Height);
        using var pixels = image.GetPixelsUnsafe();
        for (var row = 0; row < level0.Rows; row++)
        {
            ct.ThrowIfCancellationRequested();
            var (y0, rows) = level0.StripFor(row);
            var strip = pixels.ToByteArray(0, y0, (uint)level0.Width, (uint)rows, "BGRA");
            if (strip == null) return null;
            level0.FillTileRow(row, strip, y0);
        }
        return TilePyramid.Build(level0, DirectXPixelFormat.B8G8R8A8UIntNormalized, ct);
    }
}
