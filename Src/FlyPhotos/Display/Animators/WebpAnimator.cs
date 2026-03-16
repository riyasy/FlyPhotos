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
using Windows.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using NLog;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     Real-time animator for animated WebP files, implementing <see cref="IAnimator" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a binary RIFF parser?</b>
///         The Windows WIC WebP codec does not expose per-frame metadata (delay, offset, blend
///         and dispose flags) through <c>BitmapProperties</c>. All timing and layout data is
///         extracted by <see cref="Parser" />, which walks the raw RIFF container and reads
///         VP8X, ANIM, and ANMF chunks directly — the same strategy used by
///         <c>PngAnimator</c> for APNG.
///     </para>
///     <para>
///         <b>Full-canvas vs. sub-region frames.</b>
///         Depending on the installed WIC codec version, <c>GetFrameAsync</c> may return either:
///         <list type="bullet">
///             <item>
///                 A pre-composited full-canvas bitmap (dimensions match the VP8X canvas) —
///                 stamped with <c>CanvasBlend.Copy</c> directly onto the compositor surface.
///             </item>
///             <item>
///                 A raw ANMF sub-region patch (dimensions match the ANMF frame bounds) —
///                 composited at the parsed pixel offset using the frame's blend mode.
///             </item>
///         </list>
///         <see cref="RenderFrameAsync" /> detects which case applies at runtime by comparing
///         the decoded frame's pixel dimensions against the canvas dimensions.
///     </para>
///     <para>
///         <b>DPI-scaling.</b>
///         <c>DrawImage(bitmap, float x, float y)</c> in Win2D applies DPI-scaling interpolation
///         that causes sub-pixel misalignment when the canvas and bitmap have different logical
///         DPI. <c>DrawImage(bitmap, Rect)</c> maps pixels exactly and is always used here.
///     </para>
///     <para>
///         <b>Loop count.</b>
///         The ANIM chunk loop count is parsed and logged but the animator always loops
///         infinitely via <c>totalElapsedTime % _totalAnimationDuration</c>.
///     </para>
/// </remarks>
public partial class WebpAnimator : IAnimator
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // -------------------------------------------------------------------------
    // Nested types
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Immutable per-frame data converted from the raw ANMF parser output and stored
    ///     at construction time. Avoids RIFF re-parsing on the hot render path.
    /// </summary>
    private class FrameMetadata
    {
        /// <summary>
        ///     How long this frame is displayed before advancing.
        ///     <para>
        ///         <b>WebP spec:</b> the raw ANMF duration field is in milliseconds (24-bit LE).
        ///         Durations strictly less than 10 ms are clamped to 100 ms, matching the
        ///         behaviour of Chrome and Firefox. This compensates for authoring-tool bugs
        ///         that encode near-zero delays when converting from GIF.
        ///     </para>
        /// </summary>
        public TimeSpan Delay { get; init; }

        /// <summary>
        ///     Pixel rectangle of this ANMF frame within the canvas.
        ///     <para>
        ///         <b>WebP spec:</b> ANMF stores X and Y as (actual / 2); Width and Height as
        ///         (actual − 1). Both are decoded by <see cref="Parser" /> into pixel values.
        ///     </para>
        ///     For full-canvas frames this equals the full canvas rect.
        /// </summary>
        public Rect Bounds { get; init; }

        /// <summary>
        ///     <c>true</c>  = alpha-blend this frame over the compositor surface (SourceOver).<br />
        ///     <c>false</c> = replace the region completely without blending.
        ///     <para><b>WebP spec:</b> ANMF flags byte bit 1; 0 = blend, 1 = do not blend.</para>
        /// </summary>
        public bool UseBlend { get; init; }

        /// <summary>
        ///     <c>true</c> = after this frame is displayed, clear its region to
        ///     <see cref="WebpAnimator._backgroundColor" /> before the next frame renders.
        ///     <c>false</c> = leave the region as-is (do not dispose).
        ///     <para><b>WebP spec:</b> ANMF flags byte bit 0; 0 = do not dispose, 1 = dispose to background.</para>
        /// </summary>
        public bool DisposeToBackground { get; init; }
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

    /// <summary>WIC decoder used to extract per-frame pixel data from the WebP stream.</summary>
    private readonly BitmapDecoder _decoder;

    /// <summary>
    ///     Stream wrapping the raw WebP bytes. Kept alive for the lifetime of the animator
    ///     because <see cref="_decoder" /> reads from it on every <c>GetFrameAsync</c> call.
    /// </summary>
    private readonly IRandomAccessStream _stream;

    /// <summary>Pre-converted metadata for every animation frame, indexed by frame order.</summary>
    private readonly List<FrameMetadata> _frameMetadata;

    /// <summary>Total wall-clock duration of one complete animation loop.</summary>
    private readonly TimeSpan _totalAnimationDuration;

    /// <summary>
    ///     Cumulative end-time for each frame. <c>_frameCumulativeTime[i]</c> is the elapsed
    ///     time at which frame <c>i</c> finishes displaying. Built once at construction.
    ///     Allows O(log n) frame lookup via <c>Array.BinarySearch</c> in <see cref="UpdateAsync" />.
    /// </summary>
    private readonly TimeSpan[] _frameCumulativeTime;

    /// <summary>
    ///     Off-screen render target where frames are incrementally composited.
    ///     Exposed as <see cref="Surface" /> for the Win2D render loop.
    /// </summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>
    ///     Cached full-canvas <see cref="Rect" /> pre-computed at construction.
    ///     Used by <c>DrawImage</c> to guarantee 1:1 pixel mapping, avoiding the
    ///     DPI-scaling interpolation that the float-coordinate overload applies.
    /// </summary>
    private readonly Rect _canvasRect;

    /// <summary>
    ///     Background color from the ANIM chunk, used when a frame's
    ///     <see cref="FrameMetadata.DisposeToBackground" /> is <c>true</c>.
    ///     <para>
    ///         <b>WebP spec:</b> ANIM stores the color as [B, G, R, A] — not RGBA.
    ///     </para>
    ///     Defaults to <c>Colors.Transparent</c> when the ANIM chunk is absent.
    /// </summary>
    private readonly Color _backgroundColor;

    /// <summary>
    ///     Reusable GPU staging texture, sized to <c>PixelWidth × PixelHeight</c>.
    ///     Written via <c>SetPixelBytes</c> each frame, then drawn onto <see cref="_compositedSurface" />.
    ///     Sized to the full canvas so the GPU texture stride (<c>PixelWidth × 4</c> bytes/row)
    ///     always matches the tightly-packed CPU buffer layout, regardless of the current
    ///     frame patch's width.
    /// </summary>
    private readonly CanvasBitmap _reusablePatchTexture;

    /// <summary>
    ///     Persistent CPU-side pixel buffer, sized to the full canvas at construction.
    ///     Receives decoded frame pixels before upload to <see cref="_reusablePatchTexture" />.
    ///     Grown defensively if a malformed file reports a patch larger than the canvas; never shrunk.
    /// </summary>
    private byte[] _pixelBuffer;

    /// <summary>
    ///     Pre-pinned <c>IBuffer</c> wrapper over <see cref="_pixelBuffer" />, allocated once
    ///     at construction. Passed directly to <c>CopyToBuffer</c> on every frame so WIC writes
    ///     decoded pixels straight into <see cref="_pixelBuffer" /> without a second allocation.
    ///     Replaced whenever <see cref="_pixelBuffer" /> grows (rare defensive path only).
    /// </summary>
    private IBuffer _pinnedBuffer;

    /// <summary>
    ///     Index of the last fully composited frame, or <c>-1</c> after a surface reset.
    ///     Drives the catch-up render loop in <see cref="UpdateAsync" />.
    /// </summary>
    private int _currentFrameIndex = -1;

    /// <summary>
    ///     Whether the previously rendered frame specified dispose-to-background.
    ///     Stored as a field (rather than indexing <c>_frameMetadata[frameIndex - 1]</c>)
    ///     so that the last frame's disposal is correctly applied when rendering frame 0 on
    ///     loop wrap-around, where <c>frameIndex - 1</c> would be out of range.
    /// </summary>
    private bool _previousFrameDisposeToBackground;

    /// <summary>
    ///     Canvas bounds of the previously rendered frame.
    ///     Used in conjunction with <see cref="_previousFrameDisposeToBackground" /> to clear
    ///     the correct region when applying disposal before the next frame.
    /// </summary>
    private Rect _previousFrameBounds;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    /// </summary>
    private WebpAnimator(
        CanvasControl canvas,
        BitmapDecoder decoder,
        IRandomAccessStream stream,
        uint canvasWidth,
        uint canvasHeight,
        Color backgroundColor,
        List<FrameMetadata> metadata)
    {
        _decoder = decoder;
        _stream = stream;
        _frameMetadata = metadata;
        _backgroundColor = backgroundColor;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(metadata.Sum(m => m.Delay.TotalMilliseconds));

        _frameCumulativeTime = new TimeSpan[metadata.Count];
        var cumulative = TimeSpan.Zero;
        for (int i = 0; i < metadata.Count; i++)
        {
            cumulative += metadata[i].Delay;
            _frameCumulativeTime[i] = cumulative;
        }

        // Prefer canvas dimensions from the binary parser's VP8X chunk — the authoritative
        // source per the WebP spec. Some WIC codec versions return only the first ANMF frame's
        // dimensions from OrientedPixelWidth/Height, which would size the compositor surface
        // smaller than the true canvas and cause cropping on multi-region animations.
        PixelWidth = canvasWidth > 0 ? canvasWidth : decoder.OrientedPixelWidth;
        PixelHeight = canvasHeight > 0 ? canvasHeight : decoder.OrientedPixelHeight;
        _canvasRect = new Rect(0, 0, PixelWidth, PixelHeight);

        _compositedSurface = new CanvasRenderTarget(canvas, PixelWidth, PixelHeight, 96);

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
        // A newly allocated CanvasRenderTarget contains undefined GPU memory; without this
        // clear, garbage pixels could appear if UpdateAsync is called before the first
        // RenderFrameAsync completes.
        using var ds = _compositedSurface.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="WebpAnimator" /> from raw WebP file bytes.
    /// </summary>
    /// <param name="webpData">Complete raw bytes of the WebP file.</param>
    /// <param name="canvas">The Win2D <see cref="CanvasControl" /> that owns the GPU device.</param>
    /// <returns>A fully initialised <see cref="WebpAnimator" /> ready for <see cref="UpdateAsync" /> calls.</returns>
    /// <exception cref="ArgumentException">Thrown if the WIC decoder finds zero frames.</exception>
    public static async Task<WebpAnimator> CreateAsync(byte[] webpData, CanvasControl canvas)
    {
        // Parse VP8X/ANIM/ANMF metadata from the raw RIFF binary before opening the WIC decoder.
        // WIC BitmapProperties does not expose any of these values for WebP.
        var parsed = Parser.Parse(webpData);

        // AsRandomAccessStream() wraps but does not own the underlying MemoryStream,
        // so both must be named locals to ensure both are disposed on the failure path.
        var memStream = new MemoryStream(webpData);
        var stream = memStream.AsRandomAccessStream();
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.WebpDecoderId, stream);
            if (decoder.FrameCount == 0) throw new ArgumentException("WebP contains no frames.");

            var metadata = BuildMetadata(parsed, decoder);

            return new WebpAnimator(canvas, decoder, stream,
                parsed?.CanvasWidth ?? 0,
                parsed?.CanvasHeight ?? 0,
                parsed?.BackgroundColor ?? Colors.Transparent,
                metadata);
        }
        catch
        {
            stream.Dispose();
            memStream.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Animation update loop
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Advances the animation to the correct frame for the given elapsed wall-clock time
    ///     and composites any intermediate frames skipped since the last call.
    /// </summary>
    /// <param name="totalElapsedTime">
    ///     Total time elapsed since the animator was started.
    ///     Mapped to a loop position via modulo against <see cref="_totalAnimationDuration" />.
    /// </param>
    public async Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_totalAnimationDuration == TimeSpan.Zero) return;

        var elapsedInLoop = TimeSpan.FromTicks(totalElapsedTime.Ticks % _totalAnimationDuration.Ticks);

        // O(log n) frame lookup via binary search on the cumulative end-time table.
        int idx = Array.BinarySearch(_frameCumulativeTime, elapsedInLoop);
        int targetFrameIndex = idx >= 0
            ? Math.Min(idx + 1, _frameMetadata.Count - 1)
            : Math.Min(~idx, _frameMetadata.Count - 1);

        // Loop wrap-around: reset compositor and disposal state.
        if (targetFrameIndex < _currentFrameIndex)
        {
            _currentFrameIndex = -1;
            _previousFrameDisposeToBackground = false;
            _previousFrameBounds = Rect.Empty;
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        // Catch-up pass: render any skipped frames in order so their disposal side-effects
        // are correctly applied before drawing the target frame.
        if (targetFrameIndex > _currentFrameIndex)
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
                await RenderFrameAsync(i);

        _currentFrameIndex = targetFrameIndex;
    }

    // -------------------------------------------------------------------------
    // Per-frame rendering
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Decodes a single WebP frame and composites it onto <see cref="_compositedSurface" />,
    ///     applying the previous frame's disposal and the current frame's blend mode.
    /// </summary>
    /// <param name="frameIndex">Zero-based index of the frame to render.</param>
    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // Decode frame pixels via GetSoftwareBitmapAsync + CopyToBuffer.
        //
        // Why not DetachPixelData() + BlockCopy (the previous approach):
        //   DetachPixelData() returns a new byte[] per frame (allocation #1).
        //   BlockCopy then copies it into _pixelBuffer (copy #2).
        //   That is two buffers and two copies per frame.
        //
        // Why not PixelDataProvider.CopyToBuffer:
        //   PixelDataProvider does not expose CopyToBuffer — only SoftwareBitmap does.
        //
        // Current approach — SoftwareBitmap + CopyToBuffer into _pinnedBuffer:
        //   GetSoftwareBitmapAsync allocates one unmanaged WIC buffer (unavoidable).
        //   CopyToBuffer writes directly into _pixelBuffer via the pre-pinned _pinnedBuffer —
        //   a single CPU copy with no second managed allocation.
        //   The SoftwareBitmap is disposed immediately after, freeing the unmanaged buffer.
        //   Net result: one unmanaged alloc + one copy, versus the previous one managed alloc
        //   + one unmanaged alloc + one extra copy.
        var frame = await _decoder.GetFrameAsync((uint)frameIndex);

        int frameW = (int)frame.PixelWidth;
        int frameH = (int)frame.PixelHeight;
        int requiredBytes = frameW * frameH * 4;

        // Defensive growth only — fires for malformed files reporting a patch larger than canvas.
        if (_pixelBuffer.Length < requiredBytes)
        {
            _pixelBuffer = new byte[requiredBytes];
            _pinnedBuffer = _pixelBuffer.AsBuffer();
        }

        using var softwareBitmap = await frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        // CopyToBuffer writes WIC pixels directly into _pixelBuffer via the pre-pinned
        // IBuffer wrapper — no intermediate byte[] allocation, no BlockCopy.
        // _pinnedBuffer wraps the full _pixelBuffer; SoftwareBitmap.CopyToBuffer fills it
        // from offset 0 for exactly (frameW * frameH * 4) bytes based on the bitmap's own
        // dimensions, so no sub-range slice is needed.
        softwareBitmap.CopyToBuffer(_pinnedBuffer);
        _reusablePatchTexture.SetPixelBytes(_pixelBuffer, 0, 0, frameW, frameH);

        using var ds = _compositedSurface.CreateDrawingSession();

        // Step 1 — Apply the PREVIOUS frame's dispose operation.
        // Disposal state is carried as fields rather than read from _frameMetadata[frameIndex-1]
        // so that the last frame's disposal is correctly applied when rendering frame 0 on
        // loop wrap-around.
        if (_previousFrameDisposeToBackground)
        {
            // CanvasBlend.Copy + FillRectangle clears only the previous frame's region,
            // writing through any existing alpha to hard-clear the area.
            ds.Blend = CanvasBlend.Copy;
            ds.FillRectangle(_previousFrameBounds, _backgroundColor);
            ds.Blend = CanvasBlend.SourceOver;
        }

        // Step 2 — Draw the current frame using the appropriate composite mode.
        bool isFullCanvas = frameW == PixelWidth && frameH == PixelHeight;

        if (isFullCanvas)
        {
            // Full-canvas frame: WIC has already composited everything. Stamp it wholesale
            // with Copy blend to replace whatever is on the surface. Reset blend to
            // SourceOver afterwards to leave the session in a clean, predictable state.
            ds.Blend = CanvasBlend.Copy;
            ds.DrawImage(_reusablePatchTexture, _canvasRect, new Rect(0, 0, PixelWidth, PixelHeight));
            ds.Blend = CanvasBlend.SourceOver;
        }
        else
        {
            // Sub-region ANMF patch: place at the parsed pixel offset.
            if (!metadata.UseBlend)
            {
                // "Do not blend" mode: clear the region first so old content — including its
                // alpha channel — cannot compound with the new frame's pixels. Then draw with
                // SourceOver, which is correct for both blend modes:
                //   UseBlend = true:  standard Porter-Duff "over" onto existing content.
                //   UseBlend = false: Porter-Duff "over" onto the just-cleared transparent
                //                     region, equivalent to a pixel-replace without punching
                //                     holes that CanvasBlend.Copy would cause for alpha < 255.
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(metadata.Bounds, Colors.Transparent);
                ds.Blend = CanvasBlend.SourceOver;
            }

            ds.DrawImage(_reusablePatchTexture, metadata.Bounds, new Rect(0, 0, frameW, frameH));
        }

        _previousFrameDisposeToBackground = metadata.DisposeToBackground;
        _previousFrameBounds = metadata.Bounds;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Converts raw <see cref="Parser.AnmfFrameInfo" /> records into typed
    ///     <see cref="FrameMetadata" />, applying browser-compatible timing clamping.
    ///     Falls back to a default set of full-canvas 100 ms frames if the RIFF parser
    ///     found no ANMF data (e.g. older codec that doesn't expose sub-chunks).
    /// </summary>
    private static List<FrameMetadata> BuildMetadata(Parser.WebpData parsed, BitmapDecoder decoder)
    {
        if (parsed?.Frames == null || parsed.Frames.Count == 0)
        {
            Logger.Warn("WebpAnimator: ANMF parser found no frames; using defaults for {0} WIC frames.",
                decoder.FrameCount);
            var fallbackBounds = new Rect(0, 0, decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            var fallback = new List<FrameMetadata>((int)decoder.FrameCount);
            for (int i = 0; i < (int)decoder.FrameCount; i++)
                fallback.Add(new FrameMetadata
                {
                    Delay = TimeSpan.FromMilliseconds(100),
                    Bounds = fallbackBounds,
                    UseBlend = true
                });
            return fallback;
        }

        var list = new List<FrameMetadata>(parsed.Frames.Count);
        foreach (var f in parsed.Frames)
            // WebP spec does not mandate a minimum frame duration. Chrome and Firefox clamp
            // durations strictly less than 10 ms to 100 ms to handle files that were lossily
            // converted from GIF with near-zero delays. 10 ms itself is valid (100 fps).
            list.Add(new FrameMetadata
            {
                Delay = TimeSpan.FromMilliseconds(f.DurationMs < 10 ? 100.0 : f.DurationMs),
                Bounds = new Rect(f.X, f.Y, f.Width, f.Height),
                UseBlend = f.UseBlend,
                DisposeToBackground = f.DisposeToBackground
            });
        return list;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <summary>Releases all Win2D GPU surfaces, the GPU staging texture, and the file stream.</summary>
    public void Dispose()
    {
        _compositedSurface?.Dispose();
        _reusablePatchTexture?.Dispose();
        // _stream owns the MemoryStream wrapping the raw WebP bytes. BitmapDecoder does not
        // implement IDisposable in its C# projection; its COM channel releases when _stream closes.
        _stream?.Dispose();
        GC.SuppressFinalize(this);
    }

    // =========================================================================
    // RIFF / WebP binary parser
    // =========================================================================

    /// <summary>
    ///     Self-contained static parser for the WebP RIFF container.
    ///     Extracts VP8X canvas dimensions, ANIM global parameters, and per-frame ANMF metadata.
    ///     Performs no rendering and holds no GPU resources.
    /// </summary>
    private static class Parser
    {
        /// <summary>Top-level result returned by <see cref="Parse" />.</summary>
        public class WebpData
        {
            /// <summary>
            ///     True canvas width in pixels, from the VP8X chunk.
            ///     More reliable than <c>BitmapDecoder.OrientedPixelWidth</c> for animated files
            ///     on codec versions that return only the first frame's width from that property.
            /// </summary>
            public uint CanvasWidth { get; init; }

            /// <summary>
            ///     True canvas height in pixels, from the VP8X chunk.
            ///     Same caveat as <see cref="CanvasWidth" />.
            /// </summary>
            public uint CanvasHeight { get; init; }

            /// <summary>
            ///     Canvas background color from the ANIM chunk.
            ///     <para>
            ///         <b>WebP spec:</b> stored as [B, G, R, A] — not RGBA or ARGB.
            ///     </para>
            ///     Applied when a frame's dispose-to-background flag is set.
            ///     Defaults to <c>Colors.Transparent</c> when the ANIM chunk is absent.
            /// </summary>
            public Color BackgroundColor { get; init; }

            /// <summary>
            ///     Loop count from the ANIM chunk (0 = infinite). Parsed for completeness;
            ///     the animator ignores this value and always loops infinitely.
            /// </summary>
            public ushort LoopCount { get; init; }

            /// <summary>Ordered list of per-frame data, one entry per ANMF chunk.</summary>
            public List<AnmfFrameInfo> Frames { get; init; }
        }

        /// <summary>Per-frame data extracted from a single ANMF chunk.</summary>
        public class AnmfFrameInfo
        {
            /// <summary>
            ///     Actual X pixel offset within the canvas.
            ///     <b>WebP spec:</b> stored as X/2 in the ANMF payload; multiplied by 2 here.
            /// </summary>
            public uint X { get; init; }

            /// <summary>
            ///     Actual Y pixel offset within the canvas.
            ///     <b>WebP spec:</b> stored as Y/2; multiplied by 2 here.
            /// </summary>
            public uint Y { get; init; }

            /// <summary>
            ///     Frame width in pixels.
            ///     <b>WebP spec:</b> stored as Width−1; incremented by 1 here.
            /// </summary>
            public uint Width { get; init; }

            /// <summary>
            ///     Frame height in pixels.
            ///     <b>WebP spec:</b> stored as Height−1; incremented by 1 here.
            /// </summary>
            public uint Height { get; init; }

            /// <summary>Frame display duration in milliseconds (24-bit LE in the ANMF payload).</summary>
            public uint DurationMs { get; init; }

            /// <summary>
            ///     Whether to alpha-blend this frame over the canvas.
            ///     <b>WebP spec:</b> ANMF flags byte bit 1; 0 = blend (true), 1 = do not blend (false).
            /// </summary>
            public bool UseBlend { get; init; }

            /// <summary>
            ///     Whether to clear this frame's region to the background color after display.
            ///     <b>WebP spec:</b> ANMF flags byte bit 0; 1 = dispose to background (true).
            /// </summary>
            public bool DisposeToBackground { get; init; }
        }

        // u8 string literals produce stack-allocated ReadOnlySpan<byte> without heap allocation.
        private static ReadOnlySpan<byte> RiffTag => "RIFF"u8;
        private static ReadOnlySpan<byte> WebpTag => "WEBP"u8;
        private static ReadOnlySpan<byte> AnmfTag => "ANMF"u8;
        private static ReadOnlySpan<byte> Vp8xTag => "VP8X"u8;
        private static ReadOnlySpan<byte> AnimTag => "ANIM"u8;

        /// <summary>
        ///     Scans the WebP RIFF container for VP8X, ANIM, and ANMF chunks and returns
        ///     a populated <see cref="WebpData" />.
        ///     Returns <c>null</c> if the data is not a valid WebP file.
        ///     Parser failures are caught and logged; a partial result with zero frames is
        ///     returned rather than throwing, allowing <see cref="BuildMetadata" /> to fall
        ///     back to WIC-driven defaults.
        /// </summary>
        /// <param name="data">Raw bytes of the WebP file.</param>
        public static WebpData Parse(byte[] data)
        {
            var frames = new List<AnmfFrameInfo>();
            uint canvasW = 0, canvasH = 0;
            var bgColor = Colors.Transparent;
            ushort loopCount = 0;
            try
            {
                if (data.Length < 12) return null;

                // Validate the RIFF/WEBP file header using zero-alloc span comparisons.
                if (!data.AsSpan(0, 4).SequenceEqual(RiffTag) ||
                    !data.AsSpan(8, 4).SequenceEqual(WebpTag)) return null;

                // RIFF container layout: [FourCC 4B][Size 4B][Data …], chunks repeat from offset 12.
                int offset = 12;
                while (offset + 8 <= data.Length)
                {
                    var chunkTag = data.AsSpan(offset, 4);
                    uint chunkSize = ReadUInt32LE(data, offset + 4);
                    int dataOffset = offset + 8;

                    if (chunkTag.SequenceEqual(Vp8xTag) && dataOffset + 10 <= data.Length)
                    {
                        // VP8X payload (10 bytes):
                        //   byte  0:   feature flags
                        //   bytes 1-3: reserved
                        //   bytes 4-6: Canvas Width  − 1 (24-bit LE)
                        //   bytes 7-9: Canvas Height − 1 (24-bit LE)
                        canvasW = ReadUInt24LE(data, dataOffset + 4) + 1;
                        canvasH = ReadUInt24LE(data, dataOffset + 7) + 1;
                    }
                    else if (chunkTag.SequenceEqual(AnimTag) && dataOffset + 6 <= data.Length)
                    {
                        // ANIM payload (6 bytes):
                        //   bytes 0-3: Background Color [B, G, R, A]
                        //   bytes 4-5: Loop Count (uint16 LE), 0 = infinite
                        byte b = data[dataOffset];
                        byte g = data[dataOffset + 1];
                        byte r = data[dataOffset + 2];
                        byte a = data[dataOffset + 3];
                        bgColor = Color.FromArgb(a, r, g, b);
                        loopCount = (ushort)(data[dataOffset + 4] | (data[dataOffset + 5] << 8));
                        Logger.Debug("WebpAnimator: ANIM bg=({0},{1},{2},{3}) loopCount={4} (forcing infinite).",
                            r, g, b, a, loopCount);
                    }
                    else if (chunkTag.SequenceEqual(AnmfTag) && dataOffset + 16 <= data.Length)
                    {
                        // ANMF payload (≥16 bytes, all little-endian):
                        //   bytes  0- 2: Frame X / 2        (24-bit) → pixel X = value × 2
                        //   bytes  3- 5: Frame Y / 2        (24-bit) → pixel Y = value × 2
                        //   bytes  6- 8: Frame Width  − 1   (24-bit) → pixel W = value + 1
                        //   bytes  9-11: Frame Height − 1   (24-bit) → pixel H = value + 1
                        //   bytes 12-14: Frame Duration ms  (24-bit)
                        //   byte  15:   Flags
                        //     bit 1: BlendingMethod — 0 = blend, 1 = do not blend
                        //     bit 0: DisposeMethod  — 0 = do not dispose, 1 = dispose to bg
                        uint frameX = ReadUInt24LE(data, dataOffset) * 2;
                        uint frameY = ReadUInt24LE(data, dataOffset + 3) * 2;
                        uint frameW = ReadUInt24LE(data, dataOffset + 6) + 1;
                        uint frameH = ReadUInt24LE(data, dataOffset + 9) + 1;
                        uint durMs = ReadUInt24LE(data, dataOffset + 12);
                        byte flags = data[dataOffset + 15];

                        frames.Add(new AnmfFrameInfo
                        {
                            X = frameX,
                            Y = frameY,
                            Width = frameW,
                            Height = frameH,
                            DurationMs = durMs,
                            UseBlend = (flags & 0x02) == 0,
                            DisposeToBackground = (flags & 0x01) != 0
                        });
                    }

                    // Advance to the next chunk, respecting RIFF even-byte padding.
                    // Arithmetic is done in long to prevent uint overflow on max-size chunks.
                    long nextOffset = dataOffset + (((long)chunkSize + 1) & ~1L);
                    if (nextOffset > data.Length) break;
                    offset = (int)nextOffset;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "WebpAnimator.Parser: failed to parse ANMF data. Using defaults.");
            }

            return new WebpData
            {
                CanvasWidth = canvasW,
                CanvasHeight = canvasH,
                BackgroundColor = bgColor,
                LoopCount = loopCount,
                Frames = frames
            };
        }

        /// <summary>Reads a 32-bit little-endian unsigned integer from <paramref name="d" /> at offset <paramref name="o" />.</summary>
        private static uint ReadUInt32LE(byte[] d, int o)
        {
            return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        }

        /// <summary>Reads a 24-bit little-endian unsigned integer from <paramref name="d" /> at offset <paramref name="o" />.</summary>
        private static uint ReadUInt24LE(byte[] d, int o)
        {
            return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16));
        }
    }
}