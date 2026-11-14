#nullable enable
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Where the display pipeline (the controller and the prefetch cache) gets its images: decodes, the disk cache and
/// the shared indicator items. Every load returns at once and decodes off the calling thread; what it returns carries
/// its lease, for the caller to store in the photo's <see cref="DisplaySlots"/> or release. Tests use a scripted one.
/// </summary>
internal interface IPhotoDecoder
{
    /// <summary>Opens the disk cache ahead of the first lookup. The startup thread, before the listing.</summary>
    void WarmUp();

    /// <summary>
    /// The first photo's fast path, which may bring the HQ straight away; stores what it loads in the photo. True when
    /// a preview was stored and the HQ is still to load.
    /// </summary>
    Task<bool> LoadFirstPreview(Photo photo);

    /// <summary>
    /// The HQ item. Null when an oversized image is not allowed here (<paramref name="allowLarge"/> false: prefetching
    /// a neighbour) or its decode was cancelled (<paramref name="ct"/>). A decoder that reports a failure returns the
    /// error item; one that throws is the caller's to handle.
    /// </summary>
    Task<HqDisplayItem?> LoadHq(Photo photo, bool allowLarge, CancellationToken ct);

    /// <summary>
    /// The preview and its thumbnail. Null when the light pass found nothing and the preview needs a full-image decode
    /// (<see cref="Purpose.PreviewHeavy"/>).
    /// </summary>
    Task<(PreviewDisplayItem Preview, Thumbnail? Thumbnail)?> LoadPreview(Photo photo, Purpose purpose);

    /// <summary>Writes a preview to the disk cache under its own lease (it may be released meanwhile).</summary>
    Task PutPreviewInDiskCache(string path, PreviewDisplayItem preview, int width, int height);

    /// <summary>The placeholder shown while nothing is loaded. Shared, never freed: needs no lease.</summary>
    DisplayItem LoadingIndicator { get; }

    /// <summary>The error screen for an HQ decode that threw. Shared bitmap, never freed.</summary>
    HqDisplayItem HqFailed();

    /// <summary>The error screen for a preview decode that threw. Shared bitmap, never freed.</summary>
    PreviewDisplayItem PreviewFailed();
}

/// <summary>The real <see cref="IPhotoDecoder"/>: <see cref="ImageReader"/>'s chains and the SQLite disk cache.</summary>
internal sealed class PhotoDecoder : IPhotoDecoder
{
    private readonly ICanvasResourceCreatorWithDpi _device;

    public PhotoDecoder(ICanvasResourceCreatorWithDpi device)
    {
        _device = device;
        ImageReader.Initialize(device); // the indicators are made lazily, once the device is up
    }

    public void WarmUp() => _ = DiskCacherWithSqlite.Instance;

    public async Task<bool> LoadFirstPreview(Photo photo)
    {
        var first = await Task.Run(() => ImageReader.GetFirstPreviewSpecialHandlingAsync(_device, photo.FilePath));
        switch (first)
        {
            case PreviewDisplayItem preview:
                photo.Slots.TryAttachPreview(preview, MakeThumbnail(preview));
                return true;
            case HqDisplayItem hq:
                photo.Slots.TryAttachHq(hq);
                break;
        }
        return false;
    }

    public Task<HqDisplayItem?> LoadHq(Photo photo, bool allowLarge, CancellationToken ct) => Task.Run(async () =>
    {
        var slots = photo.Slots;
        var (hq, oversized) = await ImageReader.GetHqImage(_device, photo.FilePath,
            slots.IsKnownOversized(_device.Device.MaximumBitmapSizeInPixels), allowLarge, ct);
        if (oversized) slots.KnownOversized = true; // a deferred neighbour skips the doomed normal decode next time
        if (hq is TiledHqDisplayItem tiled) await HealCachedPreviewSize(photo, tiled);
        return hq;
    });

    public Task<(PreviewDisplayItem Preview, Thumbnail? Thumbnail)?> LoadPreview(Photo photo, Purpose purpose) =>
        Task.Run<(PreviewDisplayItem, Thumbnail?)?>(async () =>
        {
            var preview = await ImageReader.GetPreview(_device, photo.FilePath, purpose);
            if (preview == null) return null;
            var thumbnail = preview.IsErrorOrUndefined() ? null : MakeThumbnail(preview);
            return (preview, thumbnail);
        });

    public Task PutPreviewInDiskCache(string path, PreviewDisplayItem preview, int width, int height) =>
        Task.Run(() => PutInDiskCache(path, preview, width, height));

    public DisplayItem LoadingIndicator => ImageReader.GetLoadingIndicator();
    public HqDisplayItem HqFailed() => ImageReader.GetHqFailedIndicator();
    public PreviewDisplayItem PreviewFailed() => ImageReader.GetPreviewFailedIndicator();

    /// <summary>
    /// Older builds cached a failed large image's preview with the error icon's size as the photo's size,
    /// which fools size-based routing and the initial fit. Rewrite that entry with the real size.
    /// </summary>
    // One-off migration for disk-cache entries written before large-image support; delete once
    // those builds are a few releases old (a stale entry otherwise only costs a wrong first fit).
    private static async Task HealCachedPreviewSize(Photo photo, TiledHqDisplayItem tiled)
    {
        if (photo.Slots.Preview is not { Origin: Origin.DiskCache, Bitmap: not null } p) return;
        int w = tiled.Pyramid.FullWidth, h = tiled.Pyramid.FullHeight;
        if (p.Metadata is { } m && (int)Math.Round(m.FullWidth) == w && (int)Math.Round(m.FullHeight) == h) return;
        await PutInDiskCache(photo.FilePath, p, w, h);
    }

    private static async Task PutInDiskCache(string path, PreviewDisplayItem preview, int width, int height)
    {
        if (!preview.TryAcquire()) return; // already released
        try { await DiskCacherWithSqlite.Instance.PutInCache(path, preview.Bitmap, width, height, preview.Rotation); }
        finally { preview.Release(); }
    }

    /// <summary>A square, centre-cropped thumbnail of <paramref name="preview"/>; null if it can't be made.</summary>
    private Thumbnail? MakeThumbnail(PreviewDisplayItem preview)
    {
        if (preview.Bitmap == null) return null;
        try
        {
            var src = preview.Bitmap;
            float srcW = src.SizeInPixels.Width;
            float srcH = src.SizeInPixels.Height;
            float cropSize = Math.Min(srcW, srcH);
            int size = Constants.ThumbnailPixelBufferSize;
            using var rt = new CanvasRenderTarget(_device, size, size, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                if (preview.Rotation != 0)
                {
                    var center = new Vector2(size / 2f, size / 2f);
                    ds.Transform = Matrix3x2.CreateRotation(
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
