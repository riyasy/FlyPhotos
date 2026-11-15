#nullable enable
using System.Threading;

namespace FlyPhotos.Core.Model;

/// <summary>
/// The decoded images of one photo — its preview, HQ and thumbnail slots — which the prefetch cache fills and the
/// canvas reads. Each filled slot holds one lease on its item (see <see cref="DisplayItem"/>); anything else that
/// uses an item takes its own. Once the photo leaves the list it is retired: its items are released and anything a
/// load still brings is released instead of stored.
/// <para>
/// Threading: written by the prefetch thread (and by the UI thread for the first photo, before the prefetch thread
/// starts, and by <see cref="Retire"/> when the photo leaves the list). Read from any thread: the UI thread picks what
/// to show, the thumbnail strip reads <see cref="Thumbnail"/> on its render thread. Writes take <see cref="_lock"/> so
/// a store, a detach and a retire never interleave; reads take no lock and may see an item that is released the
/// moment after, which is why a reader leases the item (<see cref="DisplayItem.TryAcquire"/>) before using it.
/// </para>
/// </summary>
internal sealed class DisplaySlots
{
    // The slots. Written under _lock; read anywhere without it (volatile, so a reader sees a fully built item).
    // Each non-null item carries one lease owned by the slot, ended when the item is detached, replaced or retired.
    private volatile HqDisplayItem? _hq;
    private volatile PreviewDisplayItem? _preview;
    // Device-independent pixels, no lease: the strip's GPU copy is its own (see Thumbnail).
    private volatile Thumbnail? _thumbnail;
    // Guards every write to the three slots and _retired.
    private readonly Lock _lock = new();
    // Set once by Retire, under _lock: from then on nothing is stored, and whatever arrives is released.
    private bool _retired;

    /// <summary>The full-quality image, or null. May be released the moment after it is read: lease it to use it.</summary>
    public HqDisplayItem? Hq => _hq;
    /// <summary>The fast, smaller image shown until the HQ is ready, or null. Lease it to use it, as <see cref="Hq"/>.</summary>
    public PreviewDisplayItem? Preview => _preview;
    /// <summary>The thumbnail strip's pixels, made from the preview; dropped with it.</summary>
    public Thumbnail? Thumbnail => _thumbnail;

    /// <summary>
    /// An earlier HQ load found the image larger than the GPU's maximum bitmap size. Set by the HQ decode (thread
    /// pool), read by the next one. Only a hint, so it needs no lock: a load that misses it just tries the normal
    /// decoders first.
    /// </summary>
    public bool KnownOversized { get; set; }

    /// <summary>
    /// Whether the image is known to exceed the GPU's maximum bitmap size (<paramref name="maxSize"/>), so the HQ
    /// load can go straight to the tiled reader and skip the normal decoders, which would fail. Known when an
    /// earlier load found it oversized, or when the preview metadata gives the full size.
    /// </summary>
    public bool IsKnownOversized(int maxSize) => KnownOversized ||
        _preview?.Metadata is { FullWidth: > 0, FullHeight: > 0 } m && (m.FullWidth > maxSize || m.FullHeight > maxSize);

    /// <summary>
    /// Stores a loaded HQ, taking over its lease; an item it replaces is released. False (the item released) once
    /// retired.
    /// </summary>
    public bool TryAttachHq(HqDisplayItem hq)
    {
        HqDisplayItem? replaced = null;
        bool stored;
        lock (_lock)
        {
            stored = !_retired;
            if (stored) (replaced, _hq) = (_hq, hq);
        }
        (stored ? replaced : hq)?.Release(); // the same item stored twice: its extra lease is the one released
        return stored;
    }

    /// <summary>Stores a loaded preview and its thumbnail (if any), as <see cref="TryAttachHq"/>.</summary>
    public bool TryAttachPreview(PreviewDisplayItem preview, Thumbnail? thumbnail)
    {
        PreviewDisplayItem? replaced = null;
        bool stored;
        lock (_lock)
        {
            stored = !_retired;
            if (stored)
            {
                (replaced, _preview) = (_preview, preview);
                if (thumbnail != null) _thumbnail = thumbnail;
            }
        }
        (stored ? replaced : preview)?.Release();
        return stored;
    }

    /// <summary>
    /// Empties the HQ slot and hands its lease to the caller, who releases it. Null if the slot was empty. The canvas
    /// keeps its own lease on an item on screen, so unloading it here doesn't free what is drawn.
    /// </summary>
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

    /// <summary>
    /// The photo has left the list for good (or the viewer is closing): releases its items, and every later attach
    /// releases what it is given. Idempotent. Something still drawing or reading an item holds its own lease, so
    /// this never frees a bitmap in use.
    /// </summary>
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

    /// <summary>The stored item for <paramref name="level"/>; null for the placeholder, which no photo stores.</summary>
    public DisplayItem? Get(DisplayLevel level) => level switch
    {
        DisplayLevel.Preview => _preview,
        DisplayLevel.Hq => _hq,
        _ => null
    };

    /// <summary>Whether <paramref name="level"/> has nothing real to show: an empty slot, or an error item.</summary>
    public bool IsErrorScreen(DisplayLevel level) => Get(level) is not { } item || item.IsErrorOrUndefined();

    /// <summary>The size to lay the photo out at: the best loaded item's full size.</summary>
    public (double, double) GetActualSize() => SizeOf(_hq) ?? SizeOf(_preview) ?? (100, 100);

    /// <summary>
    /// The photo's real pixel size — what the disk cache stores. <see cref="GetActualSize"/> sizes whatever is on
    /// screen, which is the error icon when HQ failed; this skips that and falls back to the preview.
    /// </summary>
    public (double, double) GetSourcePixelSize() =>
        SizeOf(_hq is { } hq && hq.IsErrorOrUndefined() ? null : _hq) ?? SizeOf(_preview) ?? (100, 100);

    // An item's full size, read under a lease: the prefetch cache may release the item at any moment. Null for no
    // item, an item already freed, or one with no size to give.
    private static (double, double)? SizeOf(DisplayItem? item)
    {
        if (item == null || !item.TryAcquire()) return null;
        try
        {
            return item switch
            {
                TiledHqDisplayItem tiled => (tiled.Pyramid.FullWidth, tiled.Pyramid.FullHeight),
                SvgHqDisplayItem svg => (svg.Width, svg.Height),
                PreviewDisplayItem { Metadata: { FullWidth: > 0, FullHeight: > 0 } m } => (m.FullWidth, m.FullHeight),
                { Bitmap: { } bitmap } => (bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height),
                _ => null
            };
        }
        finally
        {
            item.Release();
        }
    }
}
