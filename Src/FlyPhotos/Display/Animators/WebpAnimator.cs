using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using NLog;

namespace FlyPhotos.Display.Animators;

/// <summary>
/// Real-time animator for animated WebP files, implementing <see cref="IAnimator"/> so it
/// integrates with <c>AnimatedImageRenderer</c> without any format-specific changes there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a binary parser?</b>
/// The Windows WIC WebP codec does not expose per-frame metadata (delay, offset, blend flags)
/// through <c>BitmapProperties</c>. All timing and layout data is therefore extracted by
/// <see cref="Parser"/>, which walks the raw RIFF container and reads VP8X, ANIM, and ANMF chunks
/// directly — the same strategy used by <c>PngAnimator</c> for APNG.
/// </para>
/// <para>
/// <b>Full-canvas vs. sub-region frames.</b>
/// Depending on the installed WIC codec version, <c>GetFrameAsync</c> may return either:
/// <list type="bullet">
///   <item>A pre-composited full-canvas bitmap (same size as VP8X canvas) — stamped with
///   <c>CanvasBlend.Copy</c> directly onto the compositor surface.</item>
///   <item>A raw ANMF sub-region patch (same size as the ANMF frame) — composited at the
///   parsed pixel offset using the frame's blend mode.</item>
/// </list>
/// <see cref="RenderFrameAsync"/> detects which case applies at runtime by comparing the
/// decoded bitmap dimensions against the parsed ANMF bounds.
/// </para>
/// <para>
/// <b>DPI-scaling caveat.</b>
/// <c>DrawImage(bitmap, x, y)</c> in Win2D triggers DPI-scaling interpolation that causes
/// sub-pixel cropping when the canvas and bitmap have different logical DPI.
/// <c>DrawImage(bitmap, Rect)</c> maps pixels exactly and must always be used here.
/// </para>
/// <para>
/// <b>Loop count.</b>
/// The ANIM chunk loop count is parsed and logged but the animator always loops infinitely
/// (the render loop drives timing via <c>totalElapsedTime % _totalAnimationDuration</c>).
/// </para>
/// </remarks>
public partial class WebpAnimator : IAnimator
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // -------------------------------------------------------------------------
    // Frame metadata (pre-loaded from binary parser at construction time)
    // -------------------------------------------------------------------------

    /// <summary>Immutable per-frame data produced by <see cref="Parser"/> and stored at startup.</summary>
    private class FrameMetadata
    {
        /// <summary>How long this frame is displayed before advancing to the next one.</summary>
        public TimeSpan Delay { get; init; }

        /// <summary>
        /// Pixel rectangle of this ANMF frame within the canvas.
        /// For full-canvas frames this equals the whole canvas rect.
        /// </summary>
        public Rect Bounds { get; init; }

        /// <summary>
        /// <c>true</c>  = alpha-blend over the compositor surface (SourceOver).<br/>
        /// <c>false</c> = overwrite the target region completely (do not blend).
        /// </summary>
        public bool UseBlend { get; init; }

        /// <summary>
        /// When <c>true</c>, the compositor region covered by this frame must be cleared to
        /// <c>_backgroundColor</c> <em>after</em> this frame is displayed and before the next
        /// frame is drawn. Corresponds to WebP disposal method 1 (dispose to background).
        /// </summary>
        public bool DisposeToBackground { get; init; }
    }

    // -------------------------------------------------------------------------
    // IAnimator public surface
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public uint PixelWidth { get; }

    /// <inheritdoc/>
    public uint PixelHeight { get; }

    /// <inheritdoc/>
    public ICanvasImage Surface => _compositedSurface;

    // -------------------------------------------------------------------------
    // Private state
    // -------------------------------------------------------------------------

    private readonly BitmapDecoder _decoder;
    private readonly IRandomAccessStream _stream;
    private readonly List<FrameMetadata> _frameMetadata;
    private readonly TimeSpan _totalAnimationDuration;
    private readonly CanvasControl _canvas;

    /// <summary>Off-screen render target where frames are accumulated frame-by-frame.</summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>
    /// Cached full-canvas <see cref="Rect"/>. Avoids allocating a new struct on every draw
    /// call in the common full-canvas-frame path.
    /// </summary>
    private readonly Rect _canvasRect;

    /// <summary>
    /// Background color from the ANIM chunk. Used when <see cref="FrameMetadata.DisposeToBackground"/>
    /// is <c>true</c> to clear the disposed region. Defaults to transparent if the ANIM chunk is absent.
    /// </summary>
    private readonly Windows.UI.Color _backgroundColor;

    /// <summary>
    /// Index of the last fully rendered frame, or -1 when the compositor surface has been
    /// cleared (e.g. at loop restart). Used to detect whether we need to catch-up render
    /// intermediate frames in a given <see cref="UpdateAsync"/> call.
    /// </summary>
    private int _currentFrameIndex = -1;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    private WebpAnimator(
        CanvasControl canvas,
        BitmapDecoder decoder,
        IRandomAccessStream stream,
        uint canvasWidth,
        uint canvasHeight,
        Windows.UI.Color backgroundColor,
        List<FrameMetadata> metadata)
    {
        _canvas = canvas;
        _decoder = decoder;
        _stream = stream;
        _frameMetadata = metadata;
        _backgroundColor = backgroundColor;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(SumDelays(metadata));

        // Prefer the VP8X canvas size from the binary parser — it is the authoritative
        // canvas dimension per spec. decoder.OrientedPixelWidth may reflect only the
        // first ANMF frame's size on some codec versions, causing the compositor surface
        // to be allocated smaller than the canvas → cropping.
        PixelWidth  = canvasWidth  > 0 ? canvasWidth  : decoder.OrientedPixelWidth;
        PixelHeight = canvasHeight > 0 ? canvasHeight : decoder.OrientedPixelHeight;

        _canvasRect = new Rect(0, 0, PixelWidth, PixelHeight);
        _compositedSurface = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);
    }

    /// <summary>
    /// Asynchronously creates a <see cref="WebpAnimator"/> from the raw WebP file bytes.
    /// </summary>
    /// <param name="webpData">
    /// Complete raw bytes of the WebP file (typically pre-loaded by <c>WebpReader</c>).
    /// </param>
    /// <param name="canvas">
    /// The Win2D <see cref="CanvasControl"/> that owns the GPU device used for rendering.
    /// </param>
    /// <returns>A fully initialised <see cref="WebpAnimator"/> ready to receive <see cref="UpdateAsync"/> calls.</returns>
    /// <exception cref="ArgumentException">Thrown if the decoded file contains zero frames.</exception>
    public static async Task<WebpAnimator> CreateAsync(byte[] webpData, CanvasControl canvas)
    {
        // Parse VP8X canvas dimensions and ANMF frame metadata directly from the raw RIFF
        // binary. This is the only reliable source for:
        //   - True canvas size (decoder.OrientedPixelWidth may return first-frame size).
        //   - Per-frame delay/position/blend (WIC BitmapProperties doesn't expose these).
        var parsed = Parser.Parse(webpData);

        // Wrap the byte array in a stream for the WIC BitmapDecoder.
        // Using MemoryStream.AsRandomAccessStream() is safe here; the MemoryStream is kept
        // alive via the stream wrapper and both are owned by the animator instance.
        var memStream = new MemoryStream(webpData);
        var stream = memStream.AsRandomAccessStream();
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.WebpDecoderId, stream);
            if (decoder.FrameCount == 0)
                throw new ArgumentException("WebP data contains no frames.");

            var metadata = BuildMetadata(parsed, decoder);

            return new WebpAnimator(canvas, decoder, stream,
                parsed?.CanvasWidth ?? 0, parsed?.CanvasHeight ?? 0,
                parsed?.BackgroundColor ?? Colors.Transparent,
                metadata);
        }
        catch (Exception)
        {
            stream.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Animation update loop
    // -------------------------------------------------------------------------

    /// <summary>
    /// Advances the animation to the correct frame for the given elapsed wall-clock time
    /// and composites any intermediate frames that were skipped since the last call.
    /// </summary>
    /// <param name="totalElapsedTime">
    /// Total time elapsed since the animator was started (from the render loop's stopwatch).
    /// The animator maps this to a position within the looping animation automatically.
    /// </param>
    public async Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_totalAnimationDuration == TimeSpan.Zero) return;

        // Map wall-clock time to a position within a single loop cycle.
        // The modulo creates a seamless infinite loop regardless of loop count in the file.
        var elapsedInLoop = TimeSpan.FromTicks(totalElapsedTime.Ticks % _totalAnimationDuration.Ticks);

        // Walk the frame list to find which frame corresponds to elapsedInLoop.
        // A linear scan is fine — frame counts for WebP are typically small (<300).
        int targetFrameIndex = 0;
        var accumulatedTime = TimeSpan.Zero;
        for (int i = 0; i < _frameMetadata.Count; i++)
        {
            accumulatedTime += _frameMetadata[i].Delay;
            if (elapsedInLoop < accumulatedTime)
            {
                targetFrameIndex = i;
                break;
            }
        }

        // Detect loop wrap-around: target has gone backwards relative to where we are.
        // Reset the compositor to a clean transparent state so disposal from the previous
        // loop cycle doesn't bleed into the new one.
        if (targetFrameIndex < _currentFrameIndex)
        {
            _currentFrameIndex = -1;
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        // Render all frames from (_currentFrameIndex + 1) up to targetFrameIndex inclusive.
        // This catch-up pass ensures disposal side-effects from skipped frames are still
        // applied correctly even if the render loop ran slower than the animation speed.
        if (targetFrameIndex > _currentFrameIndex)
        {
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
                await RenderFrameAsync(i);
        }
        _currentFrameIndex = targetFrameIndex;
    }

    // -------------------------------------------------------------------------
    // Per-frame rendering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decodes and composites a single frame onto <see cref="_compositedSurface"/>.
    /// </summary>
    /// <remarks>
    /// All async WIC work (decode, pixel extraction) is completed before a
    /// <c>CanvasDrawingSession</c> is opened. This avoids holding a GPU drawing session
    /// open across <c>await</c> boundaries, which can cause deadlocks on some drivers.
    /// </remarks>
    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // ---- Async decode (no DrawingSession open yet) ----
        var frame = await _decoder.GetFrameAsync((uint)frameIndex);

        using var softwareBitmap = await frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using var frameBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvas, softwareBitmap);

        // Read SizeInPixels once into a local; SizeInPixels is a property that calls into
        // unmanaged code, so caching avoids a redundant interop call.
        var bmpSize = frameBitmap.SizeInPixels;

        // Determine whether WIC returned a raw ANMF sub-region patch or a full-canvas
        // pre-composited frame. A sub-region bitmap has the same dimensions as the ANMF
        // bounds parsed from the binary; a full-canvas bitmap matches PixelWidth × PixelHeight.
        bool isRawPatch = bmpSize.Width == metadata.Bounds.Width &&
                          bmpSize.Height == metadata.Bounds.Height;

        // ---- GPU compositing (DrawingSession open) ----
        using var ds = _compositedSurface.CreateDrawingSession();

        // Step 1 — Apply the PREVIOUS frame's disposal before drawing the current frame.
        //
        // WebP disposal is defined as: "restore the canvas region covered by frame N-1
        // to the ANIM background color before rendering frame N."  Applying it here
        // (at the start of rendering frame N) rather than at the end of frame N-1 means
        // the compositor surface always reflects the correct "before" state when we begin.
        if (frameIndex > 0)
        {
            var prevMetadata = _frameMetadata[frameIndex - 1];
            if (prevMetadata.DisposeToBackground)
            {
                // CanvasBlend.Copy writes through any existing alpha — equivalent to
                // a hard clear of that exact region without touching the rest of the canvas.
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(prevMetadata.Bounds, _backgroundColor);
            }
        }

        // Step 2 — Draw the current frame.
        if (!isRawPatch && bmpSize.Width == PixelWidth && bmpSize.Height == PixelHeight)
        {
            // Full-canvas pre-composited frame: WIC has already composited everything.
            // Stamp it wholesale with Copy to replace whatever is on the surface.
            ds.Blend = CanvasBlend.Copy;
            ds.DrawImage(frameBitmap, _canvasRect);
        }
        else
        {
            // Sub-region ANMF patch: place it at the parsed pixel offset with the
            // frame's specified blend mode.

            if (!metadata.UseBlend)
            {
                // Overwrite mode ("do not blend"): punch a transparent hole in the region
                // before drawing so the patch completely replaces any prior content there.
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(metadata.Bounds, Colors.Transparent);
            }

            // Alpha-blend mode (default): composites the patch over the existing surface.
            ds.Blend = CanvasBlend.SourceOver;

            // IMPORTANT: use DrawImage(image, Rect) — NOT DrawImage(image, float x, float y).
            // The Rect overload performs exact pixel-to-pixel mapping.
            // The float-coordinate overload applies Win2D's DPI-scaling transform, which
            // introduces sub-pixel interpolation and causes visible cropping/misalignment.
            ds.DrawImage(frameBitmap, metadata.Bounds);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sums the total animation duration from the metadata list.
    /// Uses a plain foreach loop to avoid LINQ overhead on a hot construction path.
    /// </summary>
    private static double SumDelays(List<FrameMetadata> metadata)
    {
        double sum = 0;
        foreach (var m in metadata) sum += m.Delay.TotalMilliseconds;
        return sum;
    }

    /// <summary>
    /// Converts parsed <see cref="Parser.AnmfFrameInfo"/> into <see cref="FrameMetadata"/>,
    /// applying browser-compatible timing clamping.
    /// </summary>
    /// <remarks>
    /// Chrome and Firefox clamp any frame duration ≤ 10 ms to 100 ms. This compensates for
    /// WebP files that were lossily converted from GIF with incorrectly encoded sub-10 ms
    /// delays (a common authoring tool bug). Without this clamp such files would run at
    /// hundreds of fps and appear as a blur.
    /// </remarks>
    private static List<FrameMetadata> BuildMetadata(Parser.WebpData parsed, BitmapDecoder decoder)
    {
        if (parsed?.Frames == null || parsed.Frames.Count == 0)
        {
            // Parser returned nothing — codec version may not expose ANMF chunks, or the
            // file is not a valid animated WebP. Fall back to one full-canvas frame per WIC
            // frame with a safe 100 ms delay.
            Logger.Warn("WebpAnimator: ANMF parser found no frames; using defaults for {0} WIC frames.",
                decoder.FrameCount);
            var fallbackBounds = new Rect(0, 0, decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            var fallback = new List<FrameMetadata>((int)decoder.FrameCount);
            for (int i = 0; i < (int)decoder.FrameCount; i++)
                fallback.Add(new FrameMetadata { Delay = TimeSpan.FromMilliseconds(100), Bounds = fallbackBounds, UseBlend = true });
            return fallback;
        }

        var list = new List<FrameMetadata>(parsed.Frames.Count);
        foreach (var f in parsed.Frames)
        {
            // Browser timing clamp: durations ≤ 10 ms → 100 ms (matches Chrome/Firefox).
            double durationMs = f.DurationMs <= 10 ? 100.0 : f.DurationMs;
            list.Add(new FrameMetadata
            {
                Delay = TimeSpan.FromMilliseconds(durationMs),
                Bounds = new Rect(f.X, f.Y, f.Width, f.Height),
                UseBlend = f.UseBlend,
                DisposeToBackground = f.DisposeToBackground
            });
        }
        return list;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        // _compositedSurface is a Win2D GPU resource — must be explicitly released.
        _compositedSurface?.Dispose();
        // _stream owns the MemoryStream wrapping the raw WebP bytes — close the COM channel.
        _stream?.Dispose();
        // BitmapDecoder is a WinRT COM object. It does not implement IDisposable in its
        // C# projection; the underlying COM channel is released when _stream is disposed.
        GC.SuppressFinalize(this);
    }

    // =========================================================================
    // RIFF / WebP binary parser
    // =========================================================================

    /// <summary>
    /// Parses the RIFF container of an animated WebP file to extract:
    /// <list type="bullet">
    ///   <item>VP8X canvas dimensions (authoritative canvas size).</item>
    ///   <item>ANIM global animation parameters (background color, loop count).</item>
    ///   <item>Per-frame ANMF metadata (position, duration, blend/dispose flags).</item>
    /// </list>
    /// This is necessary because the Windows WIC WebP codec does not expose any of these
    /// values through <c>BitmapFrame.BitmapProperties</c>.
    /// </summary>
    private static class Parser
    {
        /// <summary>Top-level result returned by <see cref="Parse"/>.</summary>
        public class WebpData
        {
            /// <summary>
            /// True canvas width in pixels, from the VP8X chunk.
            /// More reliable than <c>BitmapDecoder.OrientedPixelWidth</c> for animated files.
            /// </summary>
            public uint CanvasWidth { get; init; }

            /// <summary>
            /// True canvas height in pixels, from the VP8X chunk.
            /// More reliable than <c>BitmapDecoder.OrientedPixelHeight</c> for animated files.
            /// </summary>
            public uint CanvasHeight { get; init; }

            /// <summary>
            /// Canvas background color from the ANIM chunk (bytes [B, G, R, A] per spec),
            /// converted to <see cref="Windows.UI.Color"/>. Applied when a frame's
            /// <see cref="AnmfFrameInfo.DisposeToBackground"/> is <c>true</c>.
            /// Defaults to <c>Colors.Transparent</c> if the ANIM chunk is absent.
            /// </summary>
            public Windows.UI.Color BackgroundColor { get; init; }

            /// <summary>
            /// Loop count from the ANIM chunk (0 = infinite). Parsed for completeness;
            /// the animator always loops infinitely regardless of this value.
            /// </summary>
            public ushort LoopCount { get; init; }

            /// <summary>Ordered list of per-frame metadata, one entry per ANMF chunk.</summary>
            public List<AnmfFrameInfo> Frames { get; init; }
        }

        /// <summary>Per-frame data extracted from a single ANMF chunk.</summary>
        public class AnmfFrameInfo
        {
            /// <summary>Actual X pixel offset of this frame within the canvas (RIFF stores X/2).</summary>
            public uint X { get; init; }

            /// <summary>Actual Y pixel offset of this frame within the canvas (RIFF stores Y/2).</summary>
            public uint Y { get; init; }

            /// <summary>Frame width in pixels (RIFF stores Width−1).</summary>
            public uint Width { get; init; }

            /// <summary>Frame height in pixels (RIFF stores Height−1).</summary>
            public uint Height { get; init; }

            /// <summary>Display duration of this frame in milliseconds.</summary>
            public uint DurationMs { get; init; }

            /// <summary>
            /// <c>true</c> = alpha-blend over the canvas (SourceOver).<br/>
            /// <c>false</c> = overwrite (do not blend). ANMF flags byte bit 1.
            /// </summary>
            public bool UseBlend { get; init; }

            /// <summary>
            /// <c>true</c> = clear this frame's region to the ANIM background color after display.
            /// <c>false</c> = leave the canvas region as-is ("do not dispose"). ANMF flags byte bit 0.
            /// </summary>
            public bool DisposeToBackground { get; init; }
        }

        // u8 string literals produce ReadOnlySpan<byte> without any heap allocation.
        private static ReadOnlySpan<byte> RiffTag => "RIFF"u8;
        private static ReadOnlySpan<byte> WebpTag => "WEBP"u8;
        private static ReadOnlySpan<byte> AnmfTag => "ANMF"u8;
        private static ReadOnlySpan<byte> Vp8xTag => "VP8X"u8;
        private static ReadOnlySpan<byte> AnimTag => "ANIM"u8;

        /// <summary>
        /// Scans the WebP RIFF container for VP8X, ANIM, and ANMF chunks and returns
        /// a <see cref="WebpData"/> with all extracted information.
        /// </summary>
        /// <param name="data">Raw bytes of the WebP file.</param>
        /// <returns>
        /// Parsed <see cref="WebpData"/>, or <c>null</c> if <paramref name="data"/> is not
        /// a valid WebP file or parsing encounters a fatal error.
        /// </returns>
        public static WebpData Parse(byte[] data)
        {
            var frames = new List<AnmfFrameInfo>();
            uint canvasW = 0, canvasH = 0;
            var bgColor = Colors.Transparent; // default: transparent (most common)
            ushort loopCount = 0;             // default: infinite
            try
            {
                // A valid WebP file must begin with "RIFF????WEBP" (12 bytes minimum).
                if (data.Length < 12) return null;

                // Validate RIFF/WEBP header using zero-alloc span comparisons.
                if (!data.AsSpan(0, 4).SequenceEqual(RiffTag) ||
                    !data.AsSpan(8, 4).SequenceEqual(WebpTag)) return null;

                // RIFF chunk layout: [FourCC 4B][Size 4B][Data …], repeat.
                // Animated WebP always starts with a VP8X chunk at offset 12.
                int offset = 12;
                while (offset + 8 <= data.Length)
                {
                    var  chunkTag  = data.AsSpan(offset, 4);
                    uint chunkSize = ReadUInt32LE(data, offset + 4);
                    int  dataOffset = offset + 8;   // byte index of the chunk payload

                    if (chunkTag.SequenceEqual(Vp8xTag) && dataOffset + 10 <= data.Length)
                    {
                        // VP8X payload layout (10 bytes):
                        //   byte  0:   feature flags (bit 1 = animation, etc.)
                        //   bytes 1-3: reserved
                        //   bytes 4-6: Canvas Width  − 1 (24-bit LE) → actual = value + 1
                        //   bytes 7-9: Canvas Height − 1 (24-bit LE) → actual = value + 1
                        canvasW = ReadUInt24LE(data, dataOffset + 4) + 1;
                        canvasH = ReadUInt24LE(data, dataOffset + 7) + 1;
                    }
                    else if (chunkTag.SequenceEqual(AnimTag) && dataOffset + 6 <= data.Length)
                    {
                        // ANIM payload layout (6 bytes):
                        //   bytes 0-3: Background Color in [B, G, R, A] byte order (note: NOT RGBA)
                        //   bytes 4-5: Loop Count (uint16 LE), 0 = infinite
                        byte b = data[dataOffset];
                        byte g = data[dataOffset + 1];
                        byte r = data[dataOffset + 2];
                        byte a = data[dataOffset + 3];
                        bgColor   = Windows.UI.Color.FromArgb(a, r, g, b);
                        loopCount = (ushort)(data[dataOffset + 4] | data[dataOffset + 5] << 8);
                        Logger.Debug("WebpAnimator: ANIM bg=({0},{1},{2},{3}) loopCount={4} (forcing infinite).",
                            r, g, b, a, loopCount);
                    }
                    else if (chunkTag.SequenceEqual(AnmfTag) && dataOffset + 16 <= data.Length)
                    {
                        // ANMF payload layout (≥16 bytes, all little-endian):
                        //   bytes  0- 2: Frame X / 2   (24-bit) → actual pixel X = value * 2
                        //   bytes  3- 5: Frame Y / 2   (24-bit) → actual pixel Y = value * 2
                        //   bytes  6- 8: Frame Width  − 1 (24-bit) → actual W = value + 1
                        //   bytes  9-11: Frame Height − 1 (24-bit) → actual H = value + 1
                        //   bytes 12-14: Frame Duration in milliseconds (24-bit)
                        //   byte  15:   Flags
                        //     bit 1: BlendingMethod — 0 = alpha-blend, 1 = do not blend
                        //     bit 0: DisposeMethod  — 0 = do not dispose, 1 = dispose to bg
                        uint frameX = ReadUInt24LE(data, dataOffset)      * 2;
                        uint frameY = ReadUInt24LE(data, dataOffset + 3)  * 2;
                        uint frameW = ReadUInt24LE(data, dataOffset + 6)  + 1;
                        uint frameH = ReadUInt24LE(data, dataOffset + 9)  + 1;
                        uint durMs  = ReadUInt24LE(data, dataOffset + 12);
                        byte flags  = data[dataOffset + 15];

                        frames.Add(new AnmfFrameInfo
                        {
                            X = frameX, Y = frameY, Width = frameW, Height = frameH,
                            DurationMs = durMs,
                            UseBlend            = (flags & 0x02) == 0, // bit 1 = 0 → alpha-blend
                            DisposeToBackground = (flags & 0x01) != 0  // bit 0 = 1 → dispose to bg
                        });
                    }

                    // Advance to the next chunk.
                    // RIFF pads odd-sized chunks to even boundaries with one padding byte.
                    // All arithmetic is done in long to prevent uint.MaxValue+1 wrap-around,
                    // and the result is range-checked before truncating to int.
                    long nextOffset = (long)dataOffset + (((long)chunkSize + 1) & ~1L);
                    if (nextOffset > data.Length || nextOffset > int.MaxValue) break;
                    offset = (int)nextOffset;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "WebpAnimator.Parser: failed to parse ANMF data. Using defaults.");
            }
            return new WebpData
            {
                CanvasWidth     = canvasW,
                CanvasHeight    = canvasH,
                BackgroundColor = bgColor,
                LoopCount       = loopCount,
                Frames          = frames
            };
        }

        /// <summary>Reads a 32-bit unsigned integer from <paramref name="d"/> at <paramref name="o"/> (little-endian).</summary>
        private static uint ReadUInt32LE(byte[] d, int o) =>
            (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);

        /// <summary>Reads a 24-bit unsigned integer from <paramref name="d"/> at <paramref name="o"/> (little-endian).</summary>
        private static uint ReadUInt24LE(byte[] d, int o) =>
            (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16);
    }
}
