using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     Real-time animator for GIF89a animated files, implementing <see cref="IAnimator" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Compositing model.</b>
///         GIF animation is a patch-based format: each frame describes only the sub-rectangle
///         that changes. Frames are composited incrementally onto <see cref="_compositedSurface" />,
///         with disposal rules applied between frames to clear or restore regions as required.
///     </para>
///     <para>
///         <b>Decode path.</b>
///         <c>GetSoftwareBitmapAsync</c> is used to decode each frame into a <c>SoftwareBitmap</c>,
///         then <c>CopyToBuffer</c> writes the pixels directly into the persistent
///         <see cref="_pixelBuffer" /> via a pre-pinned <c>IBuffer</c> wrapper in a single copy.
///         The <c>SoftwareBitmap</c> is disposed immediately, so its unmanaged WIC buffer is freed
///         without GC involvement. This avoids the managed <c>byte[]</c> allocation per frame that
///         <c>DetachPixelData()</c> would produce.
///     </para>
///     <para>
///         <b>Loop count.</b>
///         The NETSCAPE2.0 application extension loop count is not read; the animator always
///         loops infinitely via <c>totalElapsedTime % _totalAnimationDuration</c>.
///     </para>
/// </remarks>
public partial class GifAnimator : IAnimator
{
    // -------------------------------------------------------------------------
    // Nested types
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Immutable per-frame data pre-loaded from WIC metadata at construction time.
    ///     Avoids repeated WIC metadata queries on the hot render path.
    /// </summary>
    private class FrameMetadata
    {
        /// <summary>
        ///     How long this frame is displayed before advancing.
        ///     <para>
        ///         <b>GIF spec:</b> the raw <c>/grctlext/Delay</c> value is in units of 1/100 s.
        ///         Raw values of 0 or 1 are clamped to 100 ms for browser compatibility —
        ///         many encoders emit 0 to mean "as fast as possible", which browsers render at 100 ms.
        ///         Any other value is multiplied by 10 to convert to milliseconds.
        ///     </para>
        /// </summary>
        public TimeSpan Delay { get; init; }

        /// <summary>
        ///     Pixel rectangle of this frame patch within the canvas.
        ///     Sourced from <c>/imgdesc/Left</c>, <c>Top</c>, <c>Width</c>, <c>Height</c>.
        /// </summary>
        public Rect Bounds { get; init; }

        /// <summary>
        ///     GIF89a disposal method for this frame.
        ///     <list type="bullet">
        ///         <item><b>0</b> — No disposal specified; treat as 1.</item>
        ///         <item><b>1</b> — Do not dispose; leave pixels in place.</item>
        ///         <item><b>2</b> — Restore to background (transparent in practice; see <see cref="RenderFrameAsync" />).</item>
        ///         <item><b>3</b> — Restore to the state before this frame was drawn.</item>
        ///         <item><b>4–7</b> — Undefined in spec; treated as do-not-dispose.</item>
        ///     </list>
        /// </summary>
        public byte Disposal { get; init; }
    }

    // -------------------------------------------------------------------------
    // IAnimator public surface
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public uint PixelWidth { get; }

    /// <inheritdoc />
    public uint PixelHeight { get; }

    /// <inheritdoc />
    public ICanvasImage Surface => _compositedSurface;

    // -------------------------------------------------------------------------
    // Private fields
    // -------------------------------------------------------------------------

    /// <summary>WIC decoder used to extract per-frame pixel data from the GIF stream.</summary>
    private readonly BitmapDecoder _decoder;

    /// <summary>
    ///     Stream wrapping the raw GIF bytes. Kept alive for the lifetime of the animator
    ///     because <see cref="_decoder" /> reads from it on every <c>GetFrameAsync</c> call.
    /// </summary>
    private readonly IRandomAccessStream _stream;

    /// <summary>Pre-parsed metadata for all frames, indexed by frame number.</summary>
    private readonly List<FrameMetadata> _frameMetadata;

    /// <summary>Total wall-clock duration of one complete animation loop.</summary>
    private readonly TimeSpan _totalAnimationDuration;

    /// <summary>
    ///     Cumulative end-time for each frame: <c>_frameCumulativeTime[i]</c> is the elapsed
    ///     time at which frame <c>i</c> finishes displaying. Built once at construction.
    ///     Used by <see cref="UpdateAsync" /> to resolve the target frame index in O(log n)
    ///     via <c>Array.BinarySearch</c>, rather than a linear scan from frame 0 each tick.
    /// </summary>
    private readonly TimeSpan[] _frameCumulativeTime;

    /// <summary>
    ///     Off-screen render target where frames are incrementally composited.
    ///     Exposed as <see cref="Surface" /> for the Win2D render loop to draw.
    /// </summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>
    ///     Full-canvas off-screen surface used exclusively for GIF disposal method 3
    ///     (restore-to-previous). A snapshot of the compositor is taken here before a
    ///     disposal-3 frame is drawn, and restored from here on the following frame.
    /// </summary>
    private readonly CanvasRenderTarget _previousFrameBackup;

    /// <summary>
    ///     Reusable GPU staging texture, sized to <c>PixelWidth × PixelHeight</c>.
    ///     Written via <c>SetPixelBytes</c> each frame, then drawn onto <see cref="_compositedSurface" />.
    ///     Sized to the full canvas — not the maximum patch size — so that the GPU texture stride
    ///     (<c>PixelWidth × 4</c> bytes/row) always matches the tightly-packed CPU buffer layout
    ///     regardless of the current frame patch's width.
    /// </summary>
    private readonly CanvasBitmap _reusablePatchTexture;

    /// <summary>
    ///     Persistent CPU-side pixel buffer, sized to the full canvas at construction.
    ///     Receives decoded frame pixels before upload to <see cref="_reusablePatchTexture" />.
    ///     Grown defensively if a malformed GIF reports a patch larger than the canvas; never shrunk.
    /// </summary>
    private byte[] _pixelBuffer;

    /// <summary>
    ///     Pre-allocated <c>IBuffer</c> wrapper over <see cref="_pixelBuffer" />.
    ///     <c>SetPixelBytes</c> accepts an <c>IBuffer</c>; constructing a wrapper on every call
    ///     would allocate a Gen0 object per frame. Pinned once here and reused on the hot path.
    ///     Replaced whenever <see cref="_pixelBuffer" /> grows.
    /// </summary>
    private IBuffer _pinnedBuffer;

    // GIF89a spec constants for frame delay conversion.
    // The raw /grctlext/Delay value is in units of 1/100 s; multiplying by 10 gives milliseconds.
    private const double GifDelayUnitMs = 10.0;
    private const double GifDefaultDelayMs = 100.0;

    /// <summary>
    ///     Index of the last fully composited frame, or <c>-1</c> when the surface has been
    ///     cleared (construction or loop restart). Drives the catch-up render loop in
    ///     <see cref="UpdateAsync" />.
    /// </summary>
    private int _currentFrameIndex = -1;

    /// <summary>
    ///     Canvas bounds of the previously rendered frame.
    ///     Stored after each <see cref="RenderFrameAsync" /> call so the next frame can apply
    ///     the correct disposal region without re-reading metadata.
    /// </summary>
    private Rect _previousFrameRect = Rect.Empty;

    /// <summary>
    ///     Disposal method of the previously rendered frame. Applied at the start of the
    ///     next <see cref="RenderFrameAsync" /> call before drawing the new frame.
    ///     Initialised to 1 (do-not-dispose) to match a clean canvas on first render.
    /// </summary>
    private byte _previousFrameDisposal = 1;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    ///     All arguments are pre-validated and pre-built by <see cref="CreateAsync" />.
    /// </summary>
    private GifAnimator(
        CanvasControl canvas,
        BitmapDecoder decoder,
        IRandomAccessStream stream,
        List<FrameMetadata> metadata)
    {
        _decoder = decoder;
        _stream = stream;
        _frameMetadata = metadata;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(metadata.Sum(m => m.Delay.TotalMilliseconds));

        _frameCumulativeTime = new TimeSpan[metadata.Count];
        var cumulative = TimeSpan.Zero;
        for (int i = 0; i < metadata.Count; i++)
        {
            cumulative += metadata[i].Delay;
            _frameCumulativeTime[i] = cumulative;
        }

        PixelWidth = _decoder.OrientedPixelWidth;
        PixelHeight = _decoder.OrientedPixelHeight;

        _compositedSurface = new CanvasRenderTarget(canvas, PixelWidth, PixelHeight, 96);
        _previousFrameBackup = new CanvasRenderTarget(canvas, PixelWidth, PixelHeight, 96);

        int fullCanvasBytes = (int)(PixelWidth * PixelHeight * 4);
        _pixelBuffer = new byte[fullCanvasBytes];
        _pinnedBuffer = _pixelBuffer.AsBuffer();

        _reusablePatchTexture = CanvasBitmap.CreateFromBytes(
            canvas.Device,
            _pixelBuffer,
            (int)PixelWidth,
            (int)PixelHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);

        // Clear the compositor surface to a defined transparent state at construction.
        // A newly allocated CanvasRenderTarget contains undefined GPU memory; leaving it
        // uncleared would show garbage pixels if UpdateAsync is called before the first
        // RenderFrameAsync completes (e.g. on a slow first-frame decode).
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="GifAnimator" /> from raw GIF file bytes.
    /// </summary>
    /// <param name="gifData">Complete raw bytes of the GIF file.</param>
    /// <param name="canvas">The Win2D <see cref="CanvasControl" /> that owns the GPU device.</param>
    /// <returns>A fully initialised <see cref="GifAnimator" /> ready for <see cref="UpdateAsync" /> calls.</returns>
    public static async Task<GifAnimator> CreateAsync(byte[] gifData, CanvasControl canvas)
    {
        // AsRandomAccessStream() wraps but does not own the underlying MemoryStream,
        // so both must be named locals to ensure both are disposed on the failure path.
        var memoryStream = new MemoryStream(gifData);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var metadata = await ReadAllFrameMetadataAsync(decoder);
            return new GifAnimator(canvas, decoder, randomAccessStream, metadata);
        }
        catch
        {
            randomAccessStream.Dispose();
            memoryStream.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Animation update loop
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Advances the animation to the correct frame for the given elapsed wall-clock time
    ///     and composites any intermediate frames that were skipped since the last call.
    /// </summary>
    /// <param name="totalElapsedTime">
    ///     Total time elapsed since the animator was started.
    ///     Mapped to a position within a single loop cycle via modulo.
    /// </param>
    public async Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_totalAnimationDuration == TimeSpan.Zero) return;

        var elapsedInLoop = TimeSpan.FromTicks(totalElapsedTime.Ticks % _totalAnimationDuration.Ticks);

        // Resolve the target frame index in O(log n) using the pre-built cumulative time table.
        // BinarySearch on a sorted array of cumulative end-times returns the insertion point
        // (~idx) for the current elapsed time, which is exactly the index of the frame that
        // should be showing. When an exact boundary is hit, we advance one frame forward to
        // avoid displaying the already-elapsed frame for an extra tick.
        int idx = Array.BinarySearch(_frameCumulativeTime, elapsedInLoop);
        int targetFrameIndex = idx >= 0
            ? Math.Min(idx + 1, _frameMetadata.Count - 1)
            : Math.Min(~idx, _frameMetadata.Count - 1);

        // Loop wrap-around: target went backwards. Reset compositor and disposal state
        // so content from the previous loop cycle does not bleed into the new one.
        if (targetFrameIndex < _currentFrameIndex)
        {
            _currentFrameIndex = -1;
            _previousFrameDisposal = 1;
            _previousFrameRect = Rect.Empty;
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        // Catch-up pass: render all frames from the last rendered index up to the target.
        // This ensures disposal side-effects from skipped frames are correctly applied even
        // when the render loop runs slower than the animation speed.
        if (targetFrameIndex > _currentFrameIndex)
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
                await RenderFrameAsync(i);

        _currentFrameIndex = targetFrameIndex;
    }

    // -------------------------------------------------------------------------
    // Per-frame rendering
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Decodes a single GIF frame and composites it onto <see cref="_compositedSurface" />,
    ///     applying the previous frame's disposal method and then drawing the new patch.
    /// </summary>
    /// <param name="frameIndex">Zero-based index of the frame to render.</param>
    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // Decode frame pixels via GetSoftwareBitmapAsync + CopyToBuffer into _pinnedBuffer.
        //
        // Why not GetPixelDataAsync + DetachPixelData (previous approach):
        //   DetachPixelData() returns a new byte[] per frame (allocation #1 — managed heap).
        //   Buffer.BlockCopy then moves it into _pixelBuffer (copy #2, second full memcpy).
        //   At 30fps that is 30 large managed arrays allocated and GC'd per second, causing
        //   frequent Gen0 collections and visible animation hiccups.
        //
        // GetSoftwareBitmapAsync + CopyToBuffer:
        //   GetSoftwareBitmapAsync allocates one unmanaged WIC buffer (not on managed heap,
        //   freed immediately via using — no GC involvement).
        //   CopyToBuffer writes WIC pixels directly into _pixelBuffer via _pinnedBuffer in
        //   a single CPU copy. No second managed byte[] is created at any point.
        var frame = await _decoder.GetFrameAsync((uint)frameIndex);

        int frameW = (int)metadata.Bounds.Width;
        int frameH = (int)metadata.Bounds.Height;
        int requiredBytes = frameW * frameH * 4;

        // Defensive growth only — _pixelBuffer is full-canvas sized, so this guard fires
        // only for malformed files that report a patch exceeding the declared canvas bounds.
        if (_pixelBuffer.Length < requiredBytes)
        {
            _pixelBuffer = new byte[requiredBytes];
            _pinnedBuffer = _pixelBuffer.AsBuffer();
        }

        using var softwareBitmap = await frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        // Single copy: WIC unmanaged buffer → _pixelBuffer via pre-pinned IBuffer wrapper.
        softwareBitmap.CopyToBuffer(_pinnedBuffer);
        _reusablePatchTexture.SetPixelBytes(_pinnedBuffer, 0, 0, frameW, frameH);

        // If this frame specifies disposal method 3 (restore-to-previous), snapshot the
        // compositor surface now — before we draw — so it can be restored on the next iteration.
        if (metadata.Disposal == 3)
        {
            using var backupDs = _previousFrameBackup.CreateDrawingSession();
            backupDs.DrawImage(_compositedSurface);
        }

        using (var ds = _compositedSurface.CreateDrawingSession())
        {
            // Step 1 — Apply the PREVIOUS frame's disposal method.
            if (_previousFrameDisposal == 2)
            {
                // Disposal method 2: restore-to-background.
                // GIF89a spec says restore to the PLTE background color index, but all
                // modern browsers instead clear to transparent. We match browser behaviour.
                // CanvasBlend.Copy + FillRectangle clears only the previous frame's region,
                // leaving the rest of the canvas undisturbed.
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(_previousFrameRect, Colors.Transparent);
                ds.Blend = CanvasBlend.SourceOver;
            }
            else if (_previousFrameDisposal == 3)
            {
                // Disposal method 3: restore-to-previous.
                // Copy the pre-draw snapshot back onto the compositor using CanvasComposite.Copy
                // so backup pixels replace destination pixels including their alpha channel.
                // SourceOver would alpha-blend the backup over the current surface, leaving
                // transparent regions in the backup see-through to whatever was drawn on top.
                ds.DrawImage(
                    _previousFrameBackup,
                    _previousFrameRect,
                    _previousFrameRect,
                    1.0f,
                    CanvasImageInterpolation.NearestNeighbor,
                    CanvasComposite.Copy);
            }

            // Step 2 — Draw the current frame patch onto the compositor.
            // GIF frames always composite with SourceOver (alpha blending over existing content).
            // There is no per-frame blend mode in GIF89a; only APNG and WebP have that.
            var sourceRect = new Rect(0, 0, frameW, frameH);
            ds.DrawImage(_reusablePatchTexture, metadata.Bounds, sourceRect);
        }

        _previousFrameRect = metadata.Bounds;
        _previousFrameDisposal = metadata.Disposal;
    }

    // -------------------------------------------------------------------------
    // Metadata loading
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Reads frame-level metadata from WIC for all frames in the decoder.
    ///     Performed once at creation time so the hot render path never queries WIC for metadata.
    /// </summary>
    /// <param name="decoder">An already-opened WIC <see cref="BitmapDecoder" /> for the GIF stream.</param>
    private static async Task<List<FrameMetadata>> ReadAllFrameMetadataAsync(BitmapDecoder decoder)
    {
        var metadataList = new List<FrameMetadata>();
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            // WARNING: This MUST be List<string>, NOT an array (string[]) or collection expression ([]).
            // GetPropertiesAsync is a WinRT method expecting IIterable<string>. CsWinRT can marshal
            // List<string> to that interface, but cannot create a CCW for a C# array or collection
            // expression — both project as ReadOnlyArray which does not implement IIterable<string>,
            // causing a System.InvalidCastException at runtime with no compile-time warning.
            // ReSharper and Rider WILL suggest converting this to a collection expression. Refuse it.
            // ReSharper disable once UseCollectionExpression
            // ReSharper disable once ConvertToConstant.Local
            var propertyKeys = new List<string>
            {
                "/grctlext/Delay", // display duration in 1/100 s units
                "/imgdesc/Left", // patch X offset in pixels
                "/imgdesc/Top", // patch Y offset in pixels
                "/imgdesc/Width", // patch width in pixels
                "/imgdesc/Height", // patch height in pixels
                "/grctlext/Disposal" // disposal method (0–7)
            };
            var props = await frame.BitmapProperties.GetPropertiesAsync(propertyKeys);

            var rawDelay = props.TryGetValue("/grctlext/Delay", out var d) ? (ushort)d.Value : 0;
            metadataList.Add(new FrameMetadata
            {
                Delay = TimeSpan.FromMilliseconds(rawDelay > 1 ? rawDelay * GifDelayUnitMs : GifDefaultDelayMs),
                Bounds = new Rect(
                    props.TryGetValue("/imgdesc/Left", out var l) ? (ushort)l.Value : 0,
                    props.TryGetValue("/imgdesc/Top", out var t) ? (ushort)t.Value : 0,
                    props.TryGetValue("/imgdesc/Width", out var w) ? (ushort)w.Value : decoder.PixelWidth,
                    props.TryGetValue("/imgdesc/Height", out var h) ? (ushort)h.Value : decoder.PixelHeight),
                Disposal = props.TryGetValue("/grctlext/Disposal", out var disp) ? (byte)disp.Value : (byte)1
            });
        }

        return metadataList;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <summary>Releases all Win2D GPU surfaces, the GPU staging texture, and the file stream.</summary>
    public void Dispose()
    {
        _compositedSurface?.Dispose();
        _previousFrameBackup?.Dispose();
        _reusablePatchTexture?.Dispose();
        _stream?.Dispose();
        GC.SuppressFinalize(this);
    }
}