#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using ImageMagick;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Decodes images larger than the GPU's maximum bitmap size into a CPU-side <see cref="TilePyramid"/>,
/// drawn by TiledImageRenderer, and uploads the pyramid's top level as the overview bitmap. WIC decodes in
/// one pass when the image fits a byte[] and in tile-row strips beyond that; Magick always goes in strips;
/// libheif only offers a whole-image decode. Which of them to try is decided by <see cref="DecoderChains"/>.
/// Cancellation (the user left the photo) is honoured between strips and pyramid levels, and surfaces as
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal static class LargeImageReader
{
    /// <summary>
    /// Header-only size check (no pixel decode): WIC, then a Magick ping for what WIC can't read.
    /// Only called after a normal HQ read has failed, so normal images never pay for it. False when the
    /// size can't be read, i.e. the file is broken rather than too big.
    /// </summary>
    public static async Task<bool> IsOversizedAsync(ImageFile file, int maxSize)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(await file.GetStreamAsync(bufferNetworkFile: false));
            return decoder.PixelWidth > maxSize || decoder.PixelHeight > maxSize;
        }
        catch (Exception) { /* not WIC-readable; try Magick */ }

        try
        {
            var info = new MagickImageInfo(file.Path);
            return info.Width > maxSize || info.Height > maxSize;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a HEIF/AVIF with native libheif. libheif returns the whole image as one RGBA byte[], so this
    /// fails (and the next decoder takes over) when the image is larger than a .NET array can hold.
    /// </summary>
    /// <returns>The tiled item, or null when libheif returned no pixels.</returns>
    public static TiledHqDisplayItem? DecodeHeif(ICanvasResourceCreatorWithDpi ctrl, string path, CancellationToken ct)
    {
        var img = NativeHeifWrapper.DecodePrimaryImage(path);
        if (img?.Pixels is not { Length: > 0 } pixels) return null;
        ct.ThrowIfCancellationRequested();
        var level0 = new TileLevel(img.Width, img.Height);
        level0.FillAll(pixels);
        return ToItem(ctrl, TilePyramid.Build(level0, DirectXPixelFormat.R8G8B8A8UIntNormalized, ct));
    }

    /// <summary>Uploads the pyramid's top level as the overview bitmap and wraps both in a tiled item.</summary>
    private static TiledHqDisplayItem ToItem(ICanvasResourceCreatorWithDpi ctrl, TilePyramid pyramid)
    {
        var top = pyramid.Levels[^1];
        var overview = CanvasBitmap.CreateFromBytes(ctrl.Device, top.ToPackedPixels(), top.Width, top.Height,
            pyramid.Format, 96f);
        return new TiledHqDisplayItem(overview, Origin.Disk, pyramid);
    }

    /// <summary>
    /// Decodes frame 0 with WIC, EXIF orientation applied and colour-managed to sRGB. One pass when the
    /// image fits a byte[], otherwise one strip per tile row.
    /// </summary>
    /// <returns>The tiled item, from a premultiplied BGRA pyramid.</returns>
    public static async Task<TiledHqDisplayItem?> DecodeWic(ICanvasResourceCreatorWithDpi ctrl, ImageFile file,
        CancellationToken ct)
    {
        var decoder = await BitmapDecoder.CreateAsync(await file.GetStreamAsync());
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
        return ToItem(ctrl, TilePyramid.Build(level0, DirectXPixelFormat.B8G8R8A8UIntNormalized, ct));
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
    /// <returns>The tiled item from a premultiplied BGRA pyramid, or null when Magick can't export a strip.</returns>
    public static TiledHqDisplayItem? DecodeMagick(ICanvasResourceCreatorWithDpi ctrl, string path, CancellationToken ct)
    {
        using var image = new MagickImage(path);
        ct.ThrowIfCancellationRequested();
        image.AutoOrient();
        image.Depth = 8;
        image.Alpha(AlphaAction.Set);
        image.Alpha(AlphaAction.Associate); // premultiply, as MagickNetWrap.GetHq does
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
        return ToItem(ctrl, TilePyramid.Build(level0, DirectXPixelFormat.B8G8R8A8UIntNormalized, ct));
    }
}
