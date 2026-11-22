using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using static FlyPhotos.Display.ImageRendering.SnapshotMath;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Draws a <see cref="PdfHqDisplayItem"/> as vectors at the current zoom, one page at a time. The same idea as
/// SvgVectorRenderer: once the view has settled, the visible part of the page (plus a margin) is rendered once
/// into a screen-resolution bitmap, the <em>detail</em>, and every frame just draws bitmaps. A bitmap of the
/// whole page at the default (fit) view, the <em>base</em>, shows under it while a pan or zoom moves past the
/// detail.
/// <para>
/// Unlike SVG, a PDF render can't be split into steps (Windows.Data.Pdf draws a region in one call, at the cost of
/// the whole page), so every render runs on the thread pool, one at a time, and the bitmaps on screen stay until
/// the new one lands. Pages are opaque, so the detail simply covers the base.
/// </para>
/// <para>
/// Threads: <see cref="Draw"/> and <see cref="Dispose"/> run on the W2D thread and own the snapshots. Renders and
/// page loads run on the thread pool, one at a time under <see cref="_gate"/>, which also guards the open document.
/// Finished bitmaps cross over through <see cref="_results"/>, tagged with the page generation they belong to.
/// </para>
/// </summary>
internal sealed partial class PdfRenderer : IRenderer, IMultiPage
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>How long the view must stay still before the detail is redrawn.</summary>
    private const int SettleMs = 150;
    /// <summary>The detail extends this fraction of the canvas size past each edge, so short pans need no redraw.</summary>
    private const float Margin = 0.25f;

    /// <summary>
    /// A bitmap of (part of) a page, owned by this renderer. <see cref="ImageToPx"/> maps image pixels (the page at
    /// 100%) to bitmap pixels; <see cref="Gen"/> is the page generation it was drawn for, and
    /// <see cref="PageSize"/> that page's size at 100%.
    /// </summary>
    private sealed record Snapshot(CanvasRenderTarget Bitmap, Matrix3x2 ImageToPx, int Gen, (int Width, int Height) PageSize,
        bool IsBase)
    {
        /// <summary>Bitmap pixels per image pixel (uniform, so M11 says it).</summary>
        public float Scale => ImageToPx.M11;

        /// <summary>The whole bitmap, in its pixels.</summary>
        public Rect Pixels { get; } = new(0, 0, Bitmap.SizeInPixels.Width, Bitmap.SizeInPixels.Height);

        /// <summary>The part of the page this bitmap covers, in image pixels.</summary>
        public Rect Coverage { get; } = SvgScene.TransformRect(
            new Rect(0, 0, Bitmap.SizeInPixels.Width, Bitmap.SizeInPixels.Height), Inverse(ImageToPx));
    }

    /// <summary>The canvas, read for its size on the W2D thread.</summary>
    private readonly CanvasAnimatedControl _canvas;
    /// <summary>The PDF: page count and the device to render on.</summary>
    private readonly PdfHqDisplayItem _item;
    /// <summary>
    /// The photo's current path, read whenever the file is opened. The open file survives a rename or delete
    /// (it is opened with delete sharing); a reopen after the file was replaced follows a rename the app saw.
    /// </summary>
    private readonly Func<string> _path;
    /// <summary>The photo's preview (leased; released in Dispose), drawn until the first page's base lands. May be null.</summary>
    private readonly PreviewDisplayItem _preview;
    /// <summary>Requests another frame; called when a render lands or the settle delay ends.</summary>
    private readonly Action _invalidate;

    // --- Thread-pool state: touched only while holding _gate. ---

    /// <summary>One render or page load at a time; Dispose takes it last to close the document.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>The open document; opened by the first page load.</summary>
    private PdfFile _file;
    /// <summary>The page on screen (or about to be).</summary>
    private PdfPage _page;
    /// <summary>The open document's page count; the item's until the first open, and it can change with a replace.</summary>
    private volatile int _pageCount;
    /// <summary>Bumped each time a page is loaded; read by Draw. Results of an older generation are dropped.</summary>
    private int _gen;

    // --- Shared. ---

    /// <summary>Numbers <see cref="LoadPageAsync"/> calls in call order; a load that isn't the newest gives up.</summary>
    private int _latestLoadId;
    /// <summary>Finished bitmaps, handed from the thread pool to the W2D thread.</summary>
    private readonly ConcurrentQueue<Snapshot> _results = new();
    /// <summary>1 while a detail render is queued or running, so Draw starts at most one.</summary>
    private int _detailRendering;
    /// <summary>Set by Dispose: work in flight frees what it makes and stops requesting frames.</summary>
    private volatile bool _disposed;
    /// <summary>The page on screen, for the page buttons and label.</summary>
    private volatile int _currentPageIndex;
    /// <summary>
    /// The canvas size in pixels, for sizing a new page's base like the default view. Set at construction (UI
    /// thread), then by every Draw. A torn read only misjudges one base's size.
    /// </summary>
    private float _viewportW, _viewportH;
    /// <summary>The view's rotation in degrees (90° steps), set by Draw; a rotated page fits differently.</summary>
    private volatile int _rotation;

    // --- W2D state. ---

    /// <summary>The visible area at screen resolution; null until the first one lands.</summary>
    private Snapshot _detail;
    /// <summary>The whole page at the default view's scale; null until the first page's base lands.</summary>
    private Snapshot _base;
    /// <summary>A new page's base, held until the canvas has laid out that page's size (see <see cref="TakeResults"/>).</summary>
    private Snapshot _pendingPage;
    /// <summary>The page generation the snapshots belong to; 0 until the first base is shown.</summary>
    private int _shownGen;
    /// <summary>The view transform of the last frame, to notice when the view moves.</summary>
    private Matrix3x2 _lastMat;
    /// <summary>When <see cref="_lastMat"/> last changed (Stopwatch ticks); the settle delay counts from here.</summary>
    private long _lastChange;
    /// <summary>Requests the settle redraw; cancelled by <see cref="Dispose"/>.</summary>
    private readonly DelayedWake _wake;

    /// <summary>
    /// UI thread. Starts opening the document and rendering page 1 on the thread pool.
    /// <paramref name="path"/> gives the photo's current path (any thread); <paramref name="preview"/> is a lease
    /// this renderer now owns, or null.
    /// </summary>
    public PdfRenderer(CanvasAnimatedControl canvas, PdfHqDisplayItem item, Func<string> path, PreviewDisplayItem preview,
        Action invalidate)
    {
        _canvas = canvas;
        _item = item;
        _path = path;
        _pageCount = item.PageCount;
        _preview = preview;
        _invalidate = invalidate;
        _wake = new DelayedWake(invalidate);
        var size = canvas.GetSize();
        (_viewportW, _viewportH) = ((float)size.Width, (float)size.Height);
        _ = LoadPageAsync(0);
    }

    /// <inheritdoc/>
    public int PageCount => _pageCount;

    /// <inheritdoc/>
    public int CurrentPageIndex => _currentPageIndex;

    /// <summary>
    /// Takes any finished bitmaps, starts a detail render if the settled view needs one, and draws: a bitmap that
    /// matches the view alone (snapped to pixels), else the base with the last detail over it. Before the first
    /// base lands, the preview. <paramref name="session"/>'s transform maps image pixels (the frame's ImageRect) to
    /// the screen.
    /// </summary>
    public void Draw(CanvasDrawingSession session, in ViewFrame frame, CanvasImageInterpolation quality, bool isAnimating)
    {
        if (_disposed) return;
        session.Antialiasing = CanvasAntialiasing.Aliased; // bitmap edges; the detail must sit on the base without seams

        // The canvas in pixels. CanvasAnimatedControl.Size is safe to read on the game-loop thread.
        var dpi = session.Dpi / 96f;
        var viewport = new Rect(0, 0, _canvas.Size.Width * dpi, _canvas.Size.Height * dpi);
        (_viewportW, _viewportH) = ((float)viewport.Width, (float)viewport.Height);
        _rotation = frame.Rotation;

        TakeResults(frame.ImageRect);

        var scale = new Vector2(frame.Mat.M11, frame.Mat.M12).Length(); // screen px per image px
        var visible = SvgScene.TransformRect(viewport, frame.MatInv);
        visible.Intersect(frame.ImageRect);
        var detailFits = Matches(_detail, visible, scale);
        var baseFits = Matches(_base, visible, scale);
        if (!detailFits && !baseFits) MaybeStartDetail(frame, viewport, visible, scale, isAnimating);

        if (_base == null && _detail == null)
        {
            if (_preview?.Bitmap is { } preview)
                session.DrawImage(preview, frame.ImageRect, preview.Bounds, 1f, quality);
            return;
        }
        var saved = session.Transform;
        if (detailFits) DrawSnapshot(session, _detail, saved, quality, snap: true);
        else if (baseFits) DrawSnapshot(session, _base, saved, quality, snap: true);
        else
        {
            if (_base != null) DrawSnapshot(session, _base, saved, quality);
            if (_detail != null) DrawSnapshot(session, _detail, saved, quality);
        }
        session.Transform = saved;
    }

    /// <summary>
    /// Starts a render of the visible area plus <see cref="Margin"/> at the current scale, once a page is shown,
    /// the view has been still for <see cref="SettleMs"/> and no other detail render is running. Before that, asks
    /// for a frame when the settle time is up.
    /// </summary>
    private void MaybeStartDetail(in ViewFrame view, Rect viewport, Rect visible, float scale, bool isAnimating)
    {
        var now = Stopwatch.GetTimestamp();
        if (view.Mat != _lastMat) { _lastMat = view.Mat; _lastChange = now; }
        if (_shownGen == 0 || _shownGen != Volatile.Read(ref _gen) || visible.IsEmpty || isAnimating) return;
        var waited = Stopwatch.GetElapsedTime(_lastChange, now).TotalMilliseconds;
        if (waited < SettleMs) { _wake.After(SettleMs - (int)waited); return; }
        if (Interlocked.Exchange(ref _detailRendering, 1) == 1) return; // the running render requests a frame when done

        var margin = Math.Max(viewport.Width, viewport.Height) * Margin / scale;
        var region = new Rect(visible.X - margin, visible.Y - margin, visible.Width + 2 * margin, visible.Height + 2 * margin);
        region.Intersect(view.ImageRect);
        // Whole bitmap pixels, so at rest the detail maps 1:1 onto the screen's pixel grid.
        double x0 = Math.Floor(region.Left * scale), y0 = Math.Floor(region.Top * scale);
        double x1 = Math.Ceiling(region.Right * scale), y1 = Math.Ceiling(region.Bottom * scale);
        var imageToPx = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation((float)-x0, (float)-y0);
        var source = new Rect(x0 / scale, y0 / scale, (x1 - x0) / scale, (y1 - y0) / scale);
        var gen = _shownGen;
        _ = Task.Run(() => RenderDetailAsync(gen, source, (int)(x1 - x0), (int)(y1 - y0), imageToPx));
    }

    /// <summary>
    /// Thread pool. Renders <paramref name="source"/> (image pixels) of page generation <paramref name="gen"/> into a
    /// <paramref name="w"/>×<paramref name="h"/> bitmap and publishes it; nothing if the page changed meanwhile. If
    /// another app has replaced the file, reloads the page from the new file instead; the details follow once that
    /// page is shown (at once if its size is unchanged, else at the next page flip, which re-fits the view).
    /// </summary>
    private async Task RenderDetailAsync(int gen, Rect source, int w, int h, Matrix3x2 imageToPx)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || gen != _gen) return;
                if (_page == null || CloseIfReplaced())
                {
                    // Replaced, or a reload after a replace failed: show the page from the file there now instead.
                    await ShowPageAsync(_currentPageIndex);
                    return;
                }
                // The image is the page's size in DIPs rounded to whole pixels; scale the source back to DIPs.
                var pageSize = PdfReader.SizeAt100(_page);
                var dipsPerPixel = _page.Size.Width / pageSize.Width;
                var dips = new Rect(source.X * dipsPerPixel, source.Y * dipsPerPixel,
                    source.Width * dipsPerPixel, source.Height * dipsPerPixel);
                var bitmap = NativePdfBridge.Render(_item.Device, _page, dips, w, h);
                Publish(new Snapshot(bitmap, imageToPx, gen, pageSize, IsBase: false));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "PDF detail render failed: {Path}", _path());
        }
        finally
        {
            Volatile.Write(ref _detailRendering, 0);
            if (!_disposed) _invalidate();
        }
    }

    /// <summary>
    /// Shows page <paramref name="pageIndex"/> (any thread; the work runs on the thread pool): opens the document on
    /// first use, renders the page's base at the default view's scale, and publishes it; Draw keeps the old page
    /// until the canvas has the new page's size. Returns the page's size at 100%; null when superseded by a newer
    /// call, disposed, or failed.
    /// </summary>
    public Task<Size> LoadPageAsync(int pageIndex)
    {
        var loadId = Interlocked.Increment(ref _latestLoadId); // here, so the newest call wins whatever the pool does
        return Task.Run(() => LoadPageCoreAsync(pageIndex, loadId));
    }

    /// <summary>
    /// Thread pool. The body of <see cref="LoadPageAsync"/>; <paramref name="loadId"/> is its call's number.
    /// </summary>
    private async Task<Size> LoadPageCoreAsync(int pageIndex, int loadId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || loadId != Volatile.Read(ref _latestLoadId)) return null;
            CloseIfReplaced();
            return await ShowPageAsync(pageIndex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "PDF page {Page} could not be shown: {Path}", pageIndex, _path());
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Under the gate. Opens the file if it isn't open, renders page <paramref name="pageIndex"/>'s base at the
    /// default view's scale, makes it the current page and publishes the base. Returns its size at 100%. The index is
    /// clamped to the document, which can have fewer pages after a replace. Throws on failure, leaving the current
    /// page as it was.
    /// </summary>
    private async Task<Size> ShowPageAsync(int pageIndex)
    {
        if (_file == null)
        {
            _file = await PdfFile.OpenAsync(_path());
            _pageCount = (int)_file.Document.PageCount;
        }
        pageIndex = Math.Clamp(pageIndex, 0, _pageCount - 1);
        var page = _file.Document.GetPage((uint)pageIndex);
        try
        {
            var pageSize = PdfReader.SizeAt100(page);
            var bitmap = PdfReader.RenderWhole(_item.Device, page, DefaultViewScale(pageSize));
            var toPx = (float)bitmap.SizeInPixels.Width / pageSize.Width;

            _page?.Dispose();
            _page = page;
            page = null;
            _currentPageIndex = pageIndex;
            Publish(new Snapshot(bitmap, Matrix3x2.CreateScale(toPx), ++_gen, pageSize, IsBase: true));
            _invalidate();
            return new Size(pageSize.Width, pageSize.Height);
        }
        finally
        {
            page?.Dispose();
        }
    }

    /// <summary>
    /// Under the gate, before every page load and detail render (one file stat). If another app has replaced the file
    /// at the photo's path (see <see cref="PdfFile.ReplacedOnDisk"/>), closes the open one so the next use reopens
    /// it, and returns true. Windows.Data.Pdf doesn't notice an overwrite itself: it keeps drawing the old pages. The
    /// bitmaps on screen stay until the new file's page lands.
    /// </summary>
    private bool CloseIfReplaced()
    {
        if (_file == null || !_file.ReplacedOnDisk(_path())) return false;
        Logger.Info("PDF replaced on disk, reopening: {Path}", _path());
        _page?.Dispose();
        _file.Dispose();
        (_page, _file) = (null, null);
        return true;
    }

    /// <summary>
    /// The scale the view gives a page of <paramref name="pageSize"/> by default. A base at that scale matches the
    /// default view exactly, so no detail render follows it.
    /// </summary>
    private float DefaultViewScale((int Width, int Height) pageSize)
    {
        var scale = ViewRules.DefaultScale(new Size(_viewportW, _viewportH),
            new Size(pageSize.Width, pageSize.Height), _rotation);
        return scale > 0 ? scale : 1f; // canvas not laid out yet
    }

    /// <summary>Thread pool, under the gate. Queues a finished bitmap for Draw, or frees it if the renderer is gone.</summary>
    private void Publish(Snapshot s)
    {
        _results.Enqueue(s);
        if (_disposed) DropResults(); // Dispose's own drain may have run already
    }

    /// <summary>
    /// W2D thread. Takes the finished bitmaps. One of an older page is freed. A new page's base waits as
    /// <see cref="_pendingPage"/> until <paramref name="imageRect"/> has that page's size (the canvas re-fits a
    /// page of another size a little later), so a page is never drawn in the previous page's frame; then it
    /// replaces everything of the old page. Otherwise each replaces the base or the detail.
    /// </summary>
    private void TakeResults(Rect imageRect)
    {
        var gen = Volatile.Read(ref _gen);
        while (_results.TryDequeue(out var r))
        {
            if (r.Gen != gen) r.Bitmap.Dispose();
            else if (r.Gen != _shownGen) { Free(ref _pendingPage); _pendingPage = r; }
            else if (r.IsBase) { Free(ref _base); _base = r; }
            else { Free(ref _detail); _detail = r; }
        }

        if (_pendingPage is not { } next) return;
        if (next.Gen != gen) { Free(ref _pendingPage); return; }
        if (imageRect.Width != next.PageSize.Width || imageRect.Height != next.PageSize.Height) return;
        Free(ref _detail);
        Free(ref _base);
        (_base, _pendingPage, _shownGen) = (next, null, next.Gen);
    }

    /// <summary>Whether <paramref name="s"/> can be shown alone: drawn at <paramref name="scale"/> and covering <paramref name="visible"/>.</summary>
    private static bool Matches(Snapshot s, Rect visible, float scale) =>
        s != null && MathF.Abs(scale / s.Scale - 1f) <= 0.001f && (visible.IsEmpty || Contains(s.Coverage, visible));

    /// <summary>
    /// Draws <paramref name="s"/> where it belongs under the current <paramref name="view"/>; with
    /// <paramref name="snap"/>, on whole screen pixels when it maps 1:1.
    /// </summary>
    private static void DrawSnapshot(CanvasDrawingSession session, Snapshot s, Matrix3x2 view,
        CanvasImageInterpolation quality, bool snap = false)
    {
        var m = Inverse(s.ImageToPx) * view;
        session.Transform = snap ? SnapToPixels(m) : m;
        session.DrawImage(s.Bitmap, s.Pixels, s.Pixels, 1f, quality);
    }

    /// <summary>Frees a snapshot's bitmap and clears the field.</summary>
    private static void Free(ref Snapshot s)
    {
        s?.Bitmap.Dispose();
        s = null;
    }

    /// <summary>Frees every bitmap still waiting for Draw.</summary>
    private void DropResults()
    {
        while (_results.TryDequeue(out var r)) r.Bitmap.Dispose();
    }

    /// <summary>The bitmaps don't depend on the scaling quality; the next frame draws with the new one.</summary>
    public void HandleScalingMethodChange() => _invalidate();

    /// <summary>
    /// W2D thread. Frees the bitmaps and the preview lease now. The document closes, and anything published
    /// meanwhile is freed, once a running render or load has let go of the gate.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _wake.Cancel();
        Free(ref _detail);
        Free(ref _base);
        Free(ref _pendingPage);
        DropResults();
        _preview?.Release();
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            DropResults(); // publishing happens under the gate, so nothing can slip past this drain
            _page?.Dispose();
            _file?.Dispose();
            (_page, _file) = (null, null);
            _gate.Release(); // any waiter sees _disposed and leaves
        });
    }
}
