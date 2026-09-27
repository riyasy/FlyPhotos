#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace FlyPhotos.Core.Model;

internal partial class Photo : IDisposable
{
    public string FilePath { get; private set; }
    public HqDisplayItem? Hq { get; private set; }
    public PreviewDisplayItem? Preview { get; private set; }
    public Thumbnail? Thumbnail { get; private set; }

    public bool SupportsTransparency { get; }

    public bool IsVector { get; }

    public bool IsRaw { get; }

    /// <summary>An earlier HQ load found the image larger than the GPU's maximum bitmap size.</summary>
    private bool _probedOversized;

    public Photo(string selectedFilePath)
    {
        FilePath = selectedFilePath;
        string extension = Path.GetExtension(FilePath);
        SupportsTransparency = !string.IsNullOrEmpty(extension) && FormatsSupportingTransparency.Contains(extension);
        IsVector = extension.Contains(".svg", StringComparison.OrdinalIgnoreCase);
        IsRaw = !string.IsNullOrEmpty(extension) && CodecDiscovery.IsRawFile(extension);
    }

    // Extension is never changed by a rename (UI keeps it fixed), so the format-derived
    // flags above stay valid — only the path itself needs updating.
    public void Rename(string newFilePath) => FilePath = newFilePath;

    private static readonly Photo _empty = new(string.Empty);
    public static Photo Empty() => _empty;

    public async Task<bool> LoadPreviewFirstPhoto(ICanvasResourceCreatorWithDpi device)
    {
        var continueLoadingHq = false;
        DisplayItem? firstDisplay = null;

        await Task.Run(GetInitialPreview);

        switch (firstDisplay)
        {
            case PreviewDisplayItem prev:
                Preview = prev;
                GenerateThumbnail(device, prev);
                continueLoadingHq = true;
                break;
            case HqDisplayItem hq:
                Hq = hq;
                break;
        }

        return continueLoadingHq;

        async Task GetInitialPreview()
        {
            firstDisplay = await ImageReader.GetFirstPreviewSpecialHandlingAsync(device, FilePath);
        }
    }

    public async Task LoadHqFirstPhoto(ICanvasResourceCreatorWithDpi device)
    {
        await Task.Run(() => LoadHq(device));
    }

    /// <summary>
    /// Loads the HQ item via <see cref="ImageReader.GetHqImage"/>. Returns false, leaving HQ unloaded, when an
    /// oversized image is not allowed here (<paramref name="allowLarge"/> false: prefetching a neighbour) or
    /// its decode was cancelled (<paramref name="ct"/>: the user moved on). The cache retries it once it is
    /// the photo on screen again.
    /// </summary>
    public async Task<bool> LoadHq(ICanvasResourceCreatorWithDpi device, bool allowLarge = true,
        CancellationToken ct = default)
    {
        if (Hq != null) return true;
        var (hq, oversized) = await ImageReader.GetHqImage(device, FilePath,
            IsKnownOversized(device.Device.MaximumBitmapSizeInPixels), allowLarge, ct);
        _probedOversized |= oversized; // a deferred neighbour skips the doomed normal decode next time
        if (hq == null) return false;
        if (hq is TiledHqDisplayItem tiled) await HealCachedPreviewSize(tiled);
        Hq = hq;
        return true;
    }

    /// <summary>
    /// Drops a tiled HQ so its gigabytes of CPU tiles can be reclaimed once the user has moved on. Not
    /// disposed: the canvas may still draw the old renderer (and its overview bitmap) until its render thread
    /// swaps renderers, so the tiles go to the GC once that renderer lets go.
    /// </summary>
    // The ≤4 MB overview bitmap is left to its finalizer; dispose it via the canvas pump if that shows up.
    public void ReleaseTiledHq()
    {
        if (Hq is TiledHqDisplayItem) Hq = null;
    }

    /// <summary>
    /// Older builds cached a failed large image's preview with the error icon's size as the photo's size,
    /// which fools size-based routing and the initial fit. Rewrite that entry with the real size.
    /// </summary>
    // One-off migration for disk-cache entries written before large-image support; delete once
    // those builds are a few releases old (a stale entry otherwise only costs a wrong first fit).
    private async Task HealCachedPreviewSize(TiledHqDisplayItem tiled)
    {
        if (Preview is not { Origin: Origin.DiskCache, Bitmap: not null } p) return;
        int w = tiled.Pyramid.FullWidth, h = tiled.Pyramid.FullHeight;
        if (p.Metadata is { } m && (int)Math.Round(m.FullWidth) == w && (int)Math.Round(m.FullHeight) == h) return;
        await DiskCacherWithSqlite.Instance.PutInCache(FilePath, p.Bitmap, w, h, p.Rotation);
    }

    /// <summary>
    /// Whether the HQ load already knows the image exceeds the GPU's maximum bitmap size (<paramref name="maxSize"/>),
    /// so it can go straight to the tiled reader and skip the normal decoders, which would fail. Known when an
    /// earlier load found it oversized, or when the preview metadata gives the full size.
    /// </summary>
    private bool IsKnownOversized(int maxSize) => _probedOversized ||
        Preview?.Metadata is { FullWidth: > 0, FullHeight: > 0 } m && (m.FullWidth > maxSize || m.FullHeight > maxSize);

    public async Task LoadPreview(ICanvasResourceCreatorWithDpi device)
    {
        if (Preview == null || Preview.Origin == Origin.ErrorScreen ||
            Preview.Origin == Origin.Undefined)
        {
            Preview = await ImageReader.GetPreview(device, FilePath);
            if (Thumbnail == null && Preview != null && !Preview.IsErrorOrUndefined())
                GenerateThumbnail(device, Preview);
        }
    }

    public DisplayItem? GetDisplayItemBasedOn(DisplayLevel displayLevel)
    {
        return displayLevel switch
        {
            DisplayLevel.Preview => Preview,
            DisplayLevel.Hq => Hq,
            DisplayLevel.PlaceHolder => ImageReader.GetLoadingIndicator(),
            _ => null
        };
    }

    public (double, double) GetActualSize()
    {
        if (Hq is TiledHqDisplayItem tiled)
            return (tiled.Pyramid.FullWidth, tiled.Pyramid.FullHeight);
        if (Hq?.Bitmap != null)
        {
            return (Hq.Bitmap.SizeInPixels.Width, Hq.Bitmap.SizeInPixels.Height);
        }
        if (Preview != null)
        {
            if (Preview.Metadata != null && Preview.Metadata.FullWidth != 0 && Preview.Metadata.FullHeight != 0)
            {
                return (Preview.Metadata.FullWidth, Preview.Metadata.FullHeight);
            }
            return (Preview.Bitmap.SizeInPixels.Width, Preview.Bitmap.SizeInPixels.Height);
        }
        return (100, 100);
    }

    /// <summary>
    /// The photo's real pixel size — what the disk cache stores. <see cref="GetActualSize"/> sizes whatever is
    /// on screen, which is the error icon when HQ failed; this skips that and falls back to the preview.
    /// </summary>
    public (double, double) GetSourcePixelSize()
    {
        if (Hq is not { } hq || !hq.IsErrorOrUndefined() || Preview?.Bitmap == null)
            return GetActualSize();
        if (Preview.Metadata is { FullWidth: > 0, FullHeight: > 0 } m)
            return (m.FullWidth, m.FullHeight);
        return (Preview.Bitmap.SizeInPixels.Width, Preview.Bitmap.SizeInPixels.Height);
    }

    public static DisplayItem GetLoadingIndicator() => ImageReader.GetLoadingIndicator();

    private static readonly HashSet<string> FormatsSupportingTransparency =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".gif",
        ".webp",
        ".tiff",
        ".tif",
        ".svg",
        ".apng",
        ".ico",
        ".jxl",
        ".psd"
    };

    public bool IsErrorScreen(DisplayLevel currentDisplayLevel)
    {
        var dispItem = GetDisplayItemBasedOn(currentDisplayLevel);
        return dispItem == null || dispItem.IsErrorOrUndefined();
    }

    private void GenerateThumbnail(ICanvasResourceCreatorWithDpi device, PreviewDisplayItem preview)
    {
        if (preview.Bitmap == null) return;
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
            Thumbnail = new Thumbnail(rt.GetPixelBytes());
        }
        catch { }
    }

    public void Dispose()
    {
        Hq?.Dispose();
        Preview?.Dispose();
        Thumbnail = null;
    }

    public void DisposeHqOnly()
    {
        Hq?.Dispose();
        Hq = null;
    }

    public void DisposePreviewOnly()
    {
        Preview?.Dispose();
        Preview = null;
        Thumbnail = null; // its GPU copy is released by ThumbNailController on the W2D thread
    }
}