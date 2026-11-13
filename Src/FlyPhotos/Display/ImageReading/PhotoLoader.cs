#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// The decodes behind the prefetch cache and the first photo. Each returns what it loaded, with its lease, for the
/// caller to store in the photo's <see cref="PhotoImages"/> (or release). Holds no state of its own.
/// </summary>
internal static class PhotoLoader
{
    /// <summary>
    /// The first photo's fast path (<see cref="ImageReader.GetFirstPreviewSpecialHandlingAsync"/>), which may
    /// return the HQ straight away. True when a preview was stored and the HQ is still to load.
    /// </summary>
    public static async Task<bool> LoadFirstPreview(Photo photo, ICanvasResourceCreatorWithDpi device)
    {
        var first = await Task.Run(() => ImageReader.GetFirstPreviewSpecialHandlingAsync(device, photo.FilePath));
        switch (first)
        {
            case PreviewDisplayItem preview:
                photo.Images.TryAttachPreview(preview, MakeThumbnail(device, preview));
                return true;
            case HqDisplayItem hq:
                photo.Images.TryAttachHq(hq);
                break;
        }
        return false;
    }

    /// <summary>
    /// Decodes the HQ item via <see cref="ImageReader.GetHqImage"/>. Null when an oversized image is not allowed
    /// here (<paramref name="allowLarge"/> false: prefetching a neighbour) or its decode was cancelled
    /// (<paramref name="ct"/>: the user moved on). The cache retries it once it is the photo on screen again.
    /// </summary>
    public static async Task<HqDisplayItem?> LoadHq(Photo photo, ICanvasResourceCreatorWithDpi device,
        bool allowLarge = true, CancellationToken ct = default)
    {
        var images = photo.Images;
        var (hq, oversized) = await ImageReader.GetHqImage(device, photo.FilePath,
            IsKnownOversized(images, device.Device.MaximumBitmapSizeInPixels), allowLarge, ct);
        if (oversized) images.KnownOversized = true; // a deferred neighbour skips the doomed normal decode next time
        if (hq is TiledHqDisplayItem tiled) await HealCachedPreviewSize(photo, tiled);
        return hq;
    }

    /// <summary>Decodes the preview and its thumbnail.</summary>
    /// <param name="purpose">The preview pass to run; see <see cref="ImageReader.GetPreview"/>.</param>
    /// <returns>
    /// Null when the light pass found nothing and the preview needs a full-image decode: the caller runs the heavy pass.
    /// </returns>
    public static async Task<(PreviewDisplayItem Preview, Thumbnail? Thumbnail)?> LoadPreview(Photo photo,
        ICanvasResourceCreatorWithDpi device, Purpose purpose = Purpose.Preview)
    {
        var preview = await ImageReader.GetPreview(device, photo.FilePath, purpose);
        if (preview == null) return null;
        var thumbnail = preview.IsErrorOrUndefined() ? null : MakeThumbnail(device, preview);
        return (preview, thumbnail);
    }

    /// <summary>
    /// Whether the HQ load already knows the image exceeds the GPU's maximum bitmap size (<paramref name="maxSize"/>),
    /// so it can go straight to the tiled reader and skip the normal decoders, which would fail. Known when an
    /// earlier load found it oversized, or when the preview metadata gives the full size.
    /// </summary>
    private static bool IsKnownOversized(PhotoImages images, int maxSize) => images.KnownOversized ||
        images.Preview?.Metadata is { FullWidth: > 0, FullHeight: > 0 } m && (m.FullWidth > maxSize || m.FullHeight > maxSize);

    /// <summary>
    /// Older builds cached a failed large image's preview with the error icon's size as the photo's size,
    /// which fools size-based routing and the initial fit. Rewrite that entry with the real size.
    /// </summary>
    // One-off migration for disk-cache entries written before large-image support; delete once
    // those builds are a few releases old (a stale entry otherwise only costs a wrong first fit).
    private static async Task HealCachedPreviewSize(Photo photo, TiledHqDisplayItem tiled)
    {
        if (photo.Images.Preview is not { Origin: Origin.DiskCache, Bitmap: not null } p) return;
        int w = tiled.Pyramid.FullWidth, h = tiled.Pyramid.FullHeight;
        if (p.Metadata is { } m && (int)Math.Round(m.FullWidth) == w && (int)Math.Round(m.FullHeight) == h) return;
        if (!p.TryAcquire()) return; // evicted meanwhile
        try { await DiskCacherWithSqlite.Instance.PutInCache(photo.FilePath, p.Bitmap, w, h, p.Rotation); }
        finally { p.Release(); }
    }

    /// <summary>A square, centre-cropped thumbnail of <paramref name="preview"/>; null if it can't be made.</summary>
    private static Thumbnail? MakeThumbnail(ICanvasResourceCreatorWithDpi device, PreviewDisplayItem preview)
    {
        if (preview.Bitmap == null) return null;
        try
        {
            var src = preview.Bitmap;
            float srcW = src.SizeInPixels.Width;
            float srcH = src.SizeInPixels.Height;
            float cropSize = Math.Min(srcW, srcH);
            int size = Constants.ThumbnailPixelBufferSize;
            using var rt = new CanvasRenderTarget(device, size, size, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                if (preview.Rotation != 0)
                {
                    var center = new System.Numerics.Vector2(size / 2f, size / 2f);
                    ds.Transform = System.Numerics.Matrix3x2.CreateRotation(
                        (float)(preview.Rotation * Math.PI / 180.0), center);
                }
                ds.DrawImage(src,
                    new Rect(0, 0, size, size),
                    new Rect((srcW - cropSize) / 2, (srcH - cropSize) / 2, cropSize, cropSize),
                    1f, CanvasImageInterpolation.MultiSampleLinear);
            }
            return new Thumbnail(rt.GetPixelBytes());
        }
        catch
        {
            return null;
        }
    }
}
