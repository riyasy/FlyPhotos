#nullable enable
using System.Threading;
using FlyPhotos.Display.ImageReading;

namespace FlyPhotos.Core.Model;

/// <summary>
/// The decoded images of one photo — its preview, HQ and thumbnail slots — which the prefetch cache fills and the
/// canvas reads. Each filled slot holds one lease on its item (see <see cref="DisplayItem"/>); anything else that
/// uses an item takes its own. Once the photo leaves the list it is retired: its items are released and anything a
/// load still brings is released instead of stored.
/// </summary>
internal sealed class PhotoImages
{
    // Written under _lock; read anywhere without it.
    private volatile HqDisplayItem? _hq;
    private volatile PreviewDisplayItem? _preview;
    private volatile Thumbnail? _thumbnail;
    private readonly Lock _lock = new();
    private bool _retired;

    public HqDisplayItem? Hq => _hq;
    public PreviewDisplayItem? Preview => _preview;
    public Thumbnail? Thumbnail => _thumbnail;

    /// <summary>An earlier HQ load found the image larger than the GPU's maximum bitmap size.</summary>
    public bool KnownOversized { get; set; }

    /// <summary>Stores a loaded HQ, taking over its lease. False (the item released) once retired.</summary>
    public bool TryAttachHq(HqDisplayItem hq)
    {
        lock (_lock)
        {
            if (!_retired)
            {
                _hq = hq;
                return true;
            }
        }
        hq.Release();
        return false;
    }

    /// <summary>Stores a loaded preview and its thumbnail (if any), as <see cref="TryAttachHq"/>.</summary>
    public bool TryAttachPreview(PreviewDisplayItem preview, Thumbnail? thumbnail)
    {
        lock (_lock)
        {
            if (!_retired)
            {
                _preview = preview;
                if (thumbnail != null) _thumbnail = thumbnail;
                return true;
            }
        }
        preview.Release();
        return false;
    }

    /// <summary>Empties the HQ slot and hands its lease to the caller, who releases it.</summary>
    public HqDisplayItem? DetachHq()
    {
        lock (_lock)
        {
            var hq = _hq;
            _hq = null;
            return hq;
        }
    }

    /// <summary>Same as <see cref="DetachHq"/> for the preview; the thumbnail goes with it.</summary>
    public PreviewDisplayItem? DetachPreview()
    {
        lock (_lock)
        {
            var preview = _preview;
            _preview = null;
            _thumbnail = null; // its GPU copy is released by ThumbNailController on the W2D thread
            return preview;
        }
    }


    /// <summary>The photo has left the list for good: releases its items. Idempotent.</summary>
    public void Retire()
    {
        HqDisplayItem? hq;
        PreviewDisplayItem? preview;
        lock (_lock)
        {
            if (_retired) return;
            _retired = true;
            (hq, _hq) = (_hq, null);
            (preview, _preview) = (_preview, null);
            _thumbnail = null;
        }
        hq?.Release();
        preview?.Release();
    }

    public DisplayItem? Get(DisplayLevel level) => level switch
    {
        DisplayLevel.Preview => _preview,
        DisplayLevel.Hq => _hq,
        DisplayLevel.PlaceHolder => ImageReader.GetLoadingIndicator(),
        _ => null
    };

    public bool IsErrorScreen(DisplayLevel level) => Get(level) is not { } item || item.IsErrorOrUndefined();

    /// <summary>The size to lay the photo out at: the best loaded item's full size.</summary>
    public (double, double) GetActualSize()
    {
        var hq = _hq;
        if (hq is TiledHqDisplayItem tiled)
            return (tiled.Pyramid.FullWidth, tiled.Pyramid.FullHeight);
        if (hq is SvgHqDisplayItem svg)
            return (svg.Width, svg.Height);
        if (hq?.Bitmap != null)
            return (hq.Bitmap.SizeInPixels.Width, hq.Bitmap.SizeInPixels.Height);
        if (_preview is { } preview)
        {
            if (preview.Metadata is { FullWidth: not 0, FullHeight: not 0 } m)
                return (m.FullWidth, m.FullHeight);
            return (preview.Bitmap.SizeInPixels.Width, preview.Bitmap.SizeInPixels.Height);
        }
        return (100, 100);
    }

    /// <summary>
    /// The photo's real pixel size — what the disk cache stores. <see cref="GetActualSize"/> sizes whatever is on
    /// screen, which is the error icon when HQ failed; this skips that and falls back to the preview.
    /// </summary>
    public (double, double) GetSourcePixelSize()
    {
        var preview = _preview;
        if (_hq is not { } hq || !hq.IsErrorOrUndefined() || preview?.Bitmap == null)
            return GetActualSize();
        if (preview.Metadata is { FullWidth: > 0, FullHeight: > 0 } m)
            return (m.FullWidth, m.FullHeight);
        return (preview.Bitmap.SizeInPixels.Width, preview.Bitmap.SizeInPixels.Height);
    }
}
