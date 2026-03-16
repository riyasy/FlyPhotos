using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Buffer = System.Buffer;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     Real-time animator for APNG (Animated PNG) files, implementing <see cref="IAnimator" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Compositing model.</b>
///         APNG is a patch-based format where each frame describes a sub-rectangle of the canvas.
///         Frames are composited incrementally onto <see cref="_compositedSurface" />, with each
///         frame specifying both how to dispose the previous frame's region and how to blend the
///         new pixels over the existing content.
///     </para>
///     <para>
///         <b>Why PNG re-assembly per frame?</b>
///         The Windows WIC PNG codec decodes complete PNG files, not raw chunk sequences.
///         APNG frame data is stored as sequences of raw IDAT/fdAT chunks — not standalone files.
///         To decode each frame, a minimal valid PNG is assembled in memory from the frame's
///         chunks plus the shared global chunks (PLTE, tRNS, etc.) and the IHDR with updated
///         dimensions, then handed to <c>CanvasBitmap.LoadAsync</c> for decoding.
///         <see cref="_reusableStream" /> and <see cref="_reusableWriter" /> are pre-allocated
///         to avoid allocating a new stream per frame.
///     </para>
///     <para>
///         <b>Default image and IsDefaultImageFirstFrame.</b>
///         APNG files may contain a default image in the standard IDAT chunks before any fcTL
///         frame control chunks. If that default image coincides with the first animation frame
///         (sequence number 0), its IDAT data is merged into frame 0's chunk list by the parser,
///         and <see cref="Parser.ApngData.IsDefaultImageFirstFrame" /> is set. The initial render
///         of frame 0 is then performed eagerly inside <see cref="CreateAsync" /> to show content
///         immediately, before the first <see cref="UpdateAsync" /> call arrives.
///     </para>
///     <para>
///         <b>Loop count.</b>
///         The acTL chunk loop count is not enforced; the animator always loops infinitely.
///     </para>
/// </remarks>
public partial class PngAnimator : IAnimator
{
    #region Nested Classes and Constants

    // APNG spec: dispose_op values (applied to the previous frame's region before drawing).
    /// <summary>Leave the previous frame's pixels in place; no disposal.</summary>
    private const byte APNG_DISPOSE_OP_NONE = 0;

    /// <summary>
    ///     Clear the previous frame's bounds to transparent before drawing the next frame.
    ///     Note: the APNG spec says "transparent black"; in practice this is always treated
    ///     as fully transparent with no colour component.
    /// </summary>
    private const byte APNG_DISPOSE_OP_BACKGROUND = 1;

    /// <summary>
    ///     Restore the canvas to the state it was in immediately before the previous frame was drawn.
    ///     A full-canvas snapshot is taken before each disposal-3 frame renders and used to restore.
    /// </summary>
    private const byte APNG_DISPOSE_OP_PREVIOUS = 2;

    // APNG spec: blend_op values (how the current frame's pixels combine with the canvas).
    /// <summary>
    ///     Replace the destination region with this frame's pixels, ignoring any existing alpha.
    ///     Implemented as: clear the region to transparent first, then composite with SourceOver.
    ///     Using CanvasBlend.Copy directly would write the patch's raw alpha values onto the surface,
    ///     punching transparent holes through previously composited frames for any pixel with alpha &lt; 255.
    /// </summary>
    private const byte APNG_BLEND_OP_SOURCE = 0;

    /// <summary>Alpha-composite this frame over the existing canvas content (Porter-Duff "over").</summary>
    private const byte APNG_BLEND_OP_OVER = 1;

    /// <summary>
    ///     Immutable per-frame data pre-parsed from APNG fcTL chunks at construction time.
    ///     Avoids re-parsing chunk metadata on the hot render path.
    /// </summary>
    private class ApngFrameMetadata
    {
        /// <summary>
        ///     How long this frame is displayed before advancing.
        ///     <para>
        ///         <b>APNG spec:</b> delay is expressed as a fraction <c>DelayNum / DelayDen</c> seconds.
        ///         When <c>DelayDen</c> is 0, the denominator is treated as 100 (not as "DelayNum is raw ms").
        ///         When <c>DelayNum</c> is 0, the delay is 0 ms, which browsers treat as 100 ms.
        ///     </para>
        /// </summary>
        public TimeSpan Delay { get; init; }

        /// <summary>
        ///     Pixel rectangle of this frame patch within the canvas.
        ///     Sourced from fcTL fields: <c>x_offset</c>, <c>y_offset</c>, <c>width</c>, <c>height</c>.
        /// </summary>
        public Rect Bounds { get; init; }

        /// <summary>Dispose operation to apply to this frame's region after it is displayed.</summary>
        public byte DisposeOp { get; init; }

        /// <summary>Blend operation controlling how this frame's pixels are composited onto the canvas.</summary>
        public byte BlendOp { get; init; }

        /// <summary>
        ///     Raw PNG data chunks (converted fdAT → IDAT, or original IDAT) for this frame.
        ///     Used by <see cref="Parser.ReconstructAndLoadCanvasBitmapAsync" /> to assemble a
        ///     decodable PNG in memory.
        /// </summary>
        public List<Parser.PngChunk> FrameDataChunks { get; init; }
    }

    #endregion

    #region Fields and Properties

    /// <inheritdoc />
    public uint PixelWidth { get; }

    /// <inheritdoc />
    public uint PixelHeight { get; }

    /// <inheritdoc />
    public ICanvasImage Surface => _compositedSurface;

    /// <summary>
    ///     Stream wrapping the raw APNG bytes. Kept alive for the lifetime of the animator
    ///     because the parser may seek it during construction, and the stream must outlive
    ///     any async operations that reference it.
    /// </summary>
    private readonly IRandomAccessStream _stream;

    /// <summary>
    ///     PNG chunks that are shared across all frames: PLTE (palette), tRNS (transparency),
    ///     and any other ancillary chunks that precede the first fcTL in the file.
    ///     Injected into every reconstructed per-frame PNG so the decoder has the context
    ///     it needs to decompress frames that reference a shared palette or transparency table.
    /// </summary>
    private readonly List<Parser.PngChunk> _globalChunks;

    /// <summary>Pre-parsed metadata for every animation frame, indexed by frame order.</summary>
    private readonly List<ApngFrameMetadata> _frameMetadata;

    /// <summary>Total wall-clock duration of one complete animation loop.</summary>
    private readonly TimeSpan _totalAnimationDuration;

    /// <summary>
    ///     Cumulative end-time for each frame. <c>_frameCumulativeTime[i]</c> is the elapsed
    ///     time at which frame <c>i</c> finishes displaying. Built once at construction.
    ///     Allows O(log n) frame lookup via <c>Array.BinarySearch</c> in <see cref="UpdateAsync" />.
    /// </summary>
    private readonly TimeSpan[] _frameCumulativeTime;

    /// <summary>The Win2D canvas context used to create GPU resources and load bitmaps.</summary>
    private readonly CanvasControl _canvas;

    /// <summary>
    ///     Off-screen render target where frames are incrementally composited.
    ///     Exposed as <see cref="Surface" /> for the Win2D render loop.
    /// </summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>
    ///     Full-canvas off-screen surface used exclusively for <see cref="APNG_DISPOSE_OP_PREVIOUS" />.
    ///     A full-canvas snapshot is taken here before each disposal-2 frame is drawn,
    ///     and restored from here when the following frame applies disposal-2.
    /// </summary>
    private readonly CanvasRenderTarget _previousFrameBackup;

    /// <summary>
    ///     Computed property returning a <see cref="Rect" /> covering the entire canvas.
    ///     Used when taking or restoring full-canvas backups for <see cref="APNG_DISPOSE_OP_PREVIOUS" />.
    /// </summary>
    private Rect CanvasRect => new(0, 0, PixelWidth, PixelHeight);

    /// <summary>
    ///     Reusable in-memory stream into which the per-frame PNG is assembled before decoding.
    ///     Pre-allocated at construction; reset (Size = 0, Seek(0)) at the start of each frame
    ///     render to avoid allocating a new stream per frame.
    /// </summary>
    private readonly InMemoryRandomAccessStream _reusableStream = new();

    /// <summary>
    ///     Binary writer over <see cref="_reusableStream" /> used to write the PNG signature,
    ///     chunk headers, and chunk data. Pre-allocated at construction.
    /// </summary>
    private readonly BinaryWriter _reusableWriter;

    /// <summary>
    ///     Reusable 13-byte IHDR data buffer. IHDR is always exactly 13 bytes per the PNG spec.
    ///     The width and height fields (bytes 0–3 and 4–7) are overwritten in-place for each
    ///     frame with the current patch dimensions, avoiding a new array allocation per frame.
    /// </summary>
    private readonly byte[] _ihdrBuffer = new byte[13];

    /// <summary>
    ///     Reusable scratch buffer for CRC computation inside <see cref="Parser.WriteChunk" />.
    ///     Each PNG chunk's CRC covers the 4-byte type tag followed by the chunk data.
    ///     The buffer is grown via <see cref="Array.Resize{T}" /> only when a chunk is larger
    ///     than any previously seen, and is never shrunk. This eliminates the per-chunk heap
    ///     allocation that would otherwise occur on every <see cref="Parser.WriteChunk" /> call
    ///     on the hot frame-render path.
    /// </summary>
    private readonly byte[] _crcBuffer = new byte[256];

    /// <summary>
    ///     Index of the last fully composited frame, or <c>-1</c> after a surface reset.
    ///     Drives the catch-up render loop in <see cref="UpdateAsync" />.
    /// </summary>
    private int _currentFrameIndex = -1;

    /// <summary>
    ///     Canvas bounds of the previously rendered frame.
    ///     Stored after each <see cref="RenderFrameAsync" /> call so the next frame knows
    ///     which region to clear or restore during disposal.
    /// </summary>
    private Rect _previousFrameRect = Rect.Empty;

    /// <summary>
    ///     Dispose operation of the previously rendered frame.
    ///     Applied at the start of the next <see cref="RenderFrameAsync" /> call before
    ///     drawing the new frame. Initialised to <see cref="APNG_DISPOSE_OP_NONE" />.
    /// </summary>
    private byte _previousFrameDisposal = APNG_DISPOSE_OP_NONE;

    #endregion

    #region Creation and Initialization

    /// <summary>
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    ///     Performs only synchronous, non-async work. The optional initial render of frame 0
    ///     (for files where the default image is the first animation frame) is handled
    ///     asynchronously by <see cref="CreateAsync" /> after this constructor returns.
    /// </summary>
    private PngAnimator(
        CanvasControl canvas,
        IRandomAccessStream stream,
        Parser.ApngData apngData,
        List<ApngFrameMetadata> metadata)
    {
        _canvas = canvas;
        _stream = stream;
        _globalChunks = apngData.GlobalChunks;
        _frameMetadata = metadata;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(metadata.Sum(m => m.Delay.TotalMilliseconds));

        _frameCumulativeTime = new TimeSpan[metadata.Count];
        var cumulative = TimeSpan.Zero;
        for (int i = 0; i < metadata.Count; i++)
        {
            cumulative += metadata[i].Delay;
            _frameCumulativeTime[i] = cumulative;
        }

        _reusableWriter = new BinaryWriter(_reusableStream.AsStreamForWrite());
        Array.Copy(apngData.IhdrChunk.Data, _ihdrBuffer, 13);

        PixelWidth = apngData.CanvasWidth;
        PixelHeight = apngData.CanvasHeight;
        _compositedSurface = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);
        _previousFrameBackup = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);

        // When the default image is NOT the first animation frame, the compositor surface
        // starts empty and frame 0 will be rendered on the first UpdateAsync call.
        // When it IS the first frame, CreateAsync renders frame 0 immediately after
        // construction so there is no transparent flicker before UpdateAsync is called.
        if (!apngData.IsDefaultImageFirstFrame)
        {
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="PngAnimator" /> from raw APNG file bytes.
    /// </summary>
    /// <param name="apngData">Complete raw bytes of the APNG file.</param>
    /// <param name="canvas">The Win2D <see cref="CanvasControl" /> context.</param>
    /// <returns>A fully initialised <see cref="PngAnimator" /> ready for <see cref="UpdateAsync" /> calls.</returns>
    public static async Task<PngAnimator> CreateAsync(byte[] apngData, CanvasControl canvas)
    {
        // AsRandomAccessStream() wraps but does not own the underlying MemoryStream,
        // so both must be named locals to ensure both are disposed on the failure path.
        var memoryStream = new MemoryStream(apngData);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        try
        {
            var parsedData = await Parser.ParseApngStreamAsync(randomAccessStream);

            // APNG spec (§4.4): delay = DelayNum / (DelayDen == 0 ? 100 : DelayDen) seconds.
            // DelayNum == 0 means zero delay, which browsers treat as 100 ms.
            var metadata = parsedData.FrameControls.Select(fc => new ApngFrameMetadata
            {
                Delay = TimeSpan.FromMilliseconds(
                    fc.DelayNum == 0
                        ? 100.0
                        : (double)fc.DelayNum / (fc.DelayDen == 0 ? 100 : fc.DelayDen) * 1000.0),
                Bounds = new Rect(fc.XOffset, fc.YOffset, fc.Width, fc.Height),
                DisposeOp = fc.DisposeOp,
                BlendOp = fc.BlendOp,
                FrameDataChunks = fc.FrameDataChunks
            }).ToList();

            var animator = new PngAnimator(canvas, randomAccessStream, parsedData, metadata);

            // When the default image is the first animation frame, render it eagerly here
            // so the surface is populated before the first UpdateAsync call arrives.
            // This render must be awaited here (in the async CreateAsync) rather than
            // performed synchronously in the constructor, because CanvasBitmap.LoadAsync
            // requires the Win2D dispatcher queue and cannot be called from a blocked thread.
            if (parsedData.IsDefaultImageFirstFrame)
            {
                await animator.RenderFrameAsync(0);
                animator._currentFrameIndex = 0;
            }

            return animator;
        }
        catch
        {
            randomAccessStream.Dispose();
            memoryStream.Dispose();
            throw;
        }
    }

    #endregion

    #region Animation Logic

    /// <summary>
    ///     Advances the animation to the correct frame for the given elapsed wall-clock time,
    ///     applying APNG blending and disposal rules incrementally.
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
            _previousFrameDisposal = APNG_DISPOSE_OP_NONE;
            _previousFrameRect = Rect.Empty;
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        // Catch-up pass: render any skipped frames in order to correctly apply their
        // disposal side-effects before compositing the target frame.
        if (targetFrameIndex > _currentFrameIndex)
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
                await RenderFrameAsync(i);

        _currentFrameIndex = targetFrameIndex;
    }

    /// <summary>
    ///     Reconstructs and decodes the APNG frame at <paramref name="frameIndex" />,
    ///     then composites it onto <see cref="_compositedSurface" /> using the correct
    ///     disposal and blend operations.
    /// </summary>
    /// <param name="frameIndex">Zero-based index of the frame to render.</param>
    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // Reconstruct a minimal valid PNG from the frame's raw chunk data and decode it.
        // CanvasBitmap.LoadAsync allocates a new GPU texture per frame — unavoidable without
        // a decoded frame cache. _reusableStream and _reusableWriter avoid the per-frame
        // stream and writer allocations that would otherwise occur here.
        using var patchBitmap = await Parser.ReconstructAndLoadCanvasBitmapAsync(
            _reusableStream, _reusableWriter, _ihdrBuffer, _crcBuffer, _globalChunks,
            metadata.FrameDataChunks, (uint)metadata.Bounds.Width, (uint)metadata.Bounds.Height,
            _canvas.Device);

        // If this frame specifies dispose-to-previous, snapshot the full compositor surface
        // now — before we draw — so it can be restored on the next iteration's disposal step.
        if (metadata.DisposeOp == APNG_DISPOSE_OP_PREVIOUS)
        {
            using var backupDs = _previousFrameBackup.CreateDrawingSession();
            backupDs.DrawImage(_compositedSurface, CanvasRect, CanvasRect);
        }

        using (var ds = _compositedSurface.CreateDrawingSession())
        {
            // Step 1 — Apply the PREVIOUS frame's dispose operation.
            if (_previousFrameDisposal == APNG_DISPOSE_OP_BACKGROUND)
            {
                // Clear the previous frame's region to transparent.
                // CanvasBlend.Copy + FillRectangle punches through any existing alpha,
                // ensuring old content is fully removed rather than blended away.
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(_previousFrameRect, Colors.Transparent);
                ds.Blend = CanvasBlend.SourceOver;
            }
            else if (_previousFrameDisposal == APNG_DISPOSE_OP_PREVIOUS)
            {
                // Restore the full canvas from the pre-draw snapshot.
                // CanvasComposite.Copy replaces destination pixels including their alpha channel.
                // Both source and destination rects must cover the full canvas because the
                // backup was captured as a full-canvas snapshot.
                ds.DrawImage(
                    _previousFrameBackup,
                    CanvasRect,
                    CanvasRect,
                    1.0f,
                    CanvasImageInterpolation.NearestNeighbor,
                    CanvasComposite.Copy);
            }

            // Step 2 — Draw the current frame patch using its specified blend operation.
            var patchSourceRect = new Rect(0, 0, patchBitmap.SizeInPixels.Width, patchBitmap.SizeInPixels.Height);

            if (metadata.BlendOp == APNG_BLEND_OP_SOURCE)
            {
                // BLEND_OP_SOURCE: the patch fully replaces the destination region.
                // Clear first so old pixels (including their alpha) cannot compound with the
                // new frame, then composite with SourceOver. Using CanvasBlend.Copy for the
                // DrawImage itself would write the patch's raw alpha channel onto the surface,
                // punching transparent holes wherever the patch has alpha < 255.
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(metadata.Bounds, Colors.Transparent);
                ds.Blend = CanvasBlend.SourceOver;
                ds.DrawImage(patchBitmap, metadata.Bounds, patchSourceRect);
            }
            else
            {
                // BLEND_OP_OVER: standard Porter-Duff alpha-composite over existing content.
                ds.DrawImage(patchBitmap, metadata.Bounds, patchSourceRect);
            }
        }

        _previousFrameRect = metadata.Bounds;
        _previousFrameDisposal = metadata.DisposeOp;
    }

    #endregion

    /// <summary>
    ///     Releases all Win2D GPU surfaces, the reusable stream and writer, and the file stream.
    /// </summary>
    public void Dispose()
    {
        _reusableWriter?.Dispose();
        _reusableStream?.Dispose();
        _compositedSurface?.Dispose();
        _previousFrameBackup?.Dispose();
        _stream?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Private APNG Parser

    /// <summary>
    ///     Self-contained static parser that walks the PNG chunk stream of an APNG file and
    ///     extracts the structural components needed for frame-by-frame rendering.
    ///     Performs no rendering and holds no GPU resources.
    /// </summary>
    private static class Parser
    {
        /// <summary>A single raw PNG chunk: a four-character type tag and its raw data bytes.</summary>
        public class PngChunk
        {
            /// <summary>Four-character ASCII chunk type (e.g. "IHDR", "IDAT", "fcTL").</summary>
            public string Type;

            /// <summary>Raw chunk payload bytes, excluding the 4-byte length and 4-byte CRC fields.</summary>
            public byte[] Data;
        }

        /// <summary>
        ///     Parsed content of an APNG <c>fcTL</c> (frame control) chunk,
        ///     plus the list of pixel data chunks that follow it.
        /// </summary>
        public class FrameControl
        {
            /// <summary>
            ///     Monotonically increasing sequence number shared across all fcTL and fdAT chunks.
            ///     Used to order frames correctly when chunks arrive out of order.
            /// </summary>
            public uint SequenceNumber, Width, Height, XOffset, YOffset;

            /// <summary>
            ///     Delay numerator and denominator. Delay in seconds = <c>DelayNum / DelayDen</c>.
            ///     When <c>DelayDen</c> is 0 the spec treats it as 100.
            /// </summary>
            public ushort DelayNum, DelayDen;

            /// <summary>Dispose operation for this frame (see <c>APNG_DISPOSE_OP_*</c> constants).</summary>
            public byte DisposeOp;

            /// <summary>Blend operation for this frame (see <c>APNG_BLEND_OP_*</c> constants).</summary>
            public byte BlendOp;

            /// <summary>
            ///     Ordered pixel data chunks for this frame. fdAT chunks are converted to IDAT
            ///     by stripping the 4-byte sequence number prefix; IDAT chunks are used as-is.
            /// </summary>
            public List<PngChunk> FrameDataChunks { get; } = [];
        }

        /// <summary>Top-level result produced by <see cref="ParseApngStreamAsync" />.</summary>
        public class ApngData
        {
            /// <summary>The IHDR chunk from the file, used as the template for per-frame IHDR reconstruction.</summary>
            public PngChunk IhdrChunk;

            /// <summary>Canvas width and height in pixels, read from IHDR.</summary>
            public uint CanvasWidth, CanvasHeight;

            /// <summary>
            ///     Global chunks shared across all frames (PLTE, tRNS, gAMA, etc.), excluding IHDR.
            ///     Injected into every reconstructed per-frame PNG.
            /// </summary>
            public List<PngChunk> GlobalChunks;

            /// <summary>All animation frames in sequence order.</summary>
            public List<FrameControl> FrameControls;

            /// <summary>
            ///     <c>true</c> when the file's default IDAT image data belongs to frame 0 of the animation.
            ///     In this case the IDAT chunks have been prepended to frame 0's chunk list,
            ///     and <see cref="PngAnimator.CreateAsync" /> renders frame 0 eagerly at startup.
            /// </summary>
            public bool IsDefaultImageFirstFrame;
        }

        /// <summary>Standard 8-byte PNG file signature, per PNG spec §5.2.</summary>
        private static readonly byte[] PngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>
        ///     Parses the APNG chunk structure on a background thread and returns an
        ///     <see cref="ApngData" /> with all structural components extracted.
        /// </summary>
        /// <param name="stream">Seekable stream positioned at byte 0 of the APNG file.</param>
        public static async Task<ApngData> ParseApngStreamAsync(IRandomAccessStream stream)
        {
            return await Task.Run(() =>
            {
                stream.Seek(0);
                using var reader = new BinaryReader(stream.AsStreamForRead());
                if (!reader.ReadBytes(8).SequenceEqual(PngSig)) throw new ArgumentException("Invalid PNG");

                var chunks = ReadAllChunks(reader);
                var global = new List<PngChunk>();
                var defaults = new List<PngChunk>();
                var fcs = new List<FrameControl>();
                PngChunk ihdr = null;

                foreach (var c in chunks)
                    if (c.Type == "IHDR")
                    {
                        ihdr = c;
                        global.Add(c);
                    }
                    else if (c.Type == "fcTL")
                    {
                        fcs.Add(ParseFc(c));
                    }
                    else if (c.Type == "fdAT" && fcs.Count > 0)
                    {
                        // Convert fdAT → IDAT by stripping the 4-byte sequence number prefix.
                        fcs.Last().FrameDataChunks.Add(ConvertFdat(c));
                    }
                    else if (c.Type == "IDAT")
                    {
                        // IDAT chunks before the first fcTL belong to the default image.
                        // IDAT chunks after an fcTL belong to the current animation frame.
                        if (fcs.Count > 0) fcs.Last().FrameDataChunks.Add(c);
                        else defaults.Add(c);
                    }
                    else if (c.Type != "IEND" && c.Type != "acTL")
                    {
                        // Collect global ancillary chunks (PLTE, tRNS, gAMA, cHRM, etc.).
                        // acTL is intentionally skipped — it merely signals "this is an APNG".
                        global.Add(c);
                    }

                var ordered = fcs.OrderBy(f => f.SequenceNumber).ToList();
                bool defaultIsFirst = false;

                // If there is a default image AND it corresponds to animation frame 0
                // (SequenceNumber == 0), merge the default IDAT data into frame 0's chunk list.
                if (defaults.Count > 0 && ordered.Count > 0 && ordered[0].SequenceNumber == 0)
                {
                    ordered[0].FrameDataChunks.InsertRange(0, defaults);
                    defaultIsFirst = true;
                }

                return new ApngData
                {
                    IhdrChunk = ihdr,
                    GlobalChunks = [.. global.Where(x => x.Type != "IHDR")],
                    FrameControls = ordered,
                    IsDefaultImageFirstFrame = defaultIsFirst,
                    CanvasWidth = ReadU32BE(ihdr.Data, 0),
                    CanvasHeight = ReadU32BE(ihdr.Data, 4)
                };
            });
        }

        /// <summary>
        ///     Assembles a minimal valid PNG file from the provided chunks into <paramref name="ms" />,
        ///     then loads and returns it as a <see cref="CanvasBitmap" />.
        ///     The IHDR buffer is mutated in-place with the supplied <paramref name="wPx" /> and
        ///     <paramref name="hPx" /> dimensions so no new IHDR array is allocated per frame.
        /// </summary>
        /// <param name="ms">Reusable in-memory stream (reset to 0 on entry).</param>
        /// <param name="w">Binary writer over <paramref name="ms" />.</param>
        /// <param name="ihdr">Reusable 13-byte IHDR data buffer (mutated in-place).</param>
        /// <param name="crcBuf">
        ///     Reusable scratch buffer for CRC computation. Grown automatically if any chunk
        ///     exceeds its current length; never shrunk. Owned by the <c>PngAnimator</c> instance.
        /// </param>
        /// <param name="globals">Global ancillary chunks to inject after IHDR.</param>
        /// <param name="data">Frame pixel data chunks (IDAT).</param>
        /// <param name="wPx">Frame width in pixels.</param>
        /// <param name="hPx">Frame height in pixels.</param>
        /// <param name="dev">Win2D device used to create the resulting <see cref="CanvasBitmap" />.</param>
        public static async Task<CanvasBitmap> ReconstructAndLoadCanvasBitmapAsync(
            InMemoryRandomAccessStream ms, BinaryWriter w, byte[] ihdr, byte[] crcBuf,
            IEnumerable<PngChunk> globals, IEnumerable<PngChunk> data,
            uint wPx, uint hPx, CanvasDevice dev)
        {
            ms.Size = 0;
            ms.Seek(0);
            w.Write(PngSig);

            Array.Copy(GetU32BE(wPx), 0, ihdr, 0, 4);
            Array.Copy(GetU32BE(hPx), 0, ihdr, 4, 4);

            WriteChunk(w, "IHDR", ihdr, ref crcBuf);
            foreach (var c in globals) WriteChunk(w, c.Type, c.Data, ref crcBuf);
            foreach (var c in data) WriteChunk(w, c.Type, c.Data, ref crcBuf);
            WriteChunk(w, "IEND", [], ref crcBuf);

            await w.BaseStream.FlushAsync();
            ms.Seek(0);
            return await CanvasBitmap.LoadAsync(dev, ms);
        }

        /// <summary>Reads all PNG chunks from <paramref name="r" /> until IEND or end-of-stream.</summary>
        private static List<PngChunk> ReadAllChunks(BinaryReader r)
        {
            var res = new List<PngChunk>();
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                var len = ReadU32BE(r);
                var type = Encoding.ASCII.GetString(r.ReadBytes(4));
                var data = r.ReadBytes((int)len);
                r.ReadBytes(4); // CRC — verified by the PNG decoder; skipped here for speed.
                res.Add(new PngChunk { Type = type, Data = data });
                if (type == "IEND") break;
            }

            return res;
        }

        /// <summary>Parses a raw fcTL chunk payload into a <see cref="FrameControl" />.</summary>
        private static FrameControl ParseFc(PngChunk c)
        {
            using var r = new BinaryReader(new MemoryStream(c.Data));
            return new FrameControl
            {
                SequenceNumber = ReadU32BE(r),
                Width = ReadU32BE(r),
                Height = ReadU32BE(r),
                XOffset = ReadU32BE(r),
                YOffset = ReadU32BE(r),
                DelayNum = ReadU16BE(r),
                DelayDen = ReadU16BE(r),
                DisposeOp = r.ReadByte(),
                BlendOp = r.ReadByte()
            };
        }

        /// <summary>
        ///     Converts an fdAT chunk into an IDAT chunk by stripping the 4-byte sequence
        ///     number prefix from the payload. The resulting chunk is structurally identical
        ///     to a standard IDAT chunk and can be decoded by any PNG decoder.
        /// </summary>
        private static PngChunk ConvertFdat(PngChunk f)
        {
            var d = new byte[f.Data.Length - 4];
            Array.Copy(f.Data, 4, d, 0, d.Length);
            return new PngChunk { Type = "IDAT", Data = d };
        }

        /// <summary>
        ///     Writes a complete PNG chunk to <paramref name="w" />:
        ///     4-byte big-endian length, 4-byte type, data bytes, 4-byte CRC32.
        ///     CRC covers the type and data fields, per PNG spec §5.3.
        ///     <para>
        ///         <paramref name="crcBuf" /> is a caller-owned scratch buffer grown in place
        ///         via <see cref="Array.Resize{T}" /> only when the chunk (4 type bytes + data)
        ///         exceeds its current capacity. It is never shrunk, so it converges to the size
        ///         of the largest chunk seen and causes no further allocations at steady state.
        ///         The CRC is computed over a <see cref="ReadOnlySpan{T}" /> slice of
        ///         <paramref name="crcBuf" /> so only the relevant bytes are hashed — no trimmed
        ///         array copy is allocated regardless of whether the buffer is larger than needed.
        ///     </para>
        /// </summary>
        private static void WriteChunk(BinaryWriter w, string t, byte[] d, ref byte[] crcBuf)
        {
            // The CRC input is the 4-byte type tag immediately followed by the chunk data.
            int needed = 4 + d.Length;
            if (crcBuf.Length < needed)
                Array.Resize(ref crcBuf, needed);

            // Encode the 4-byte chunk type directly into the scratch buffer — no temp array.
            Encoding.ASCII.GetBytes(t, 0, 4, crcBuf, 0);
            if (d.Length > 0) Buffer.BlockCopy(d, 0, crcBuf, 4, d.Length);

            WriteU32BE(w, (uint)d.Length);
            w.Write(crcBuf, 0, 4); // write type tag bytes
            if (d.Length > 0) w.Write(d); // write data bytes

            // Compute CRC over a zero-alloc ReadOnlySpan slice of the scratch buffer.
            // This covers exactly the type + data bytes regardless of the buffer's full length,
            // without creating a trimmed byte[] copy that the byte[] overload would require.
            WriteU32BE(w, Crc32.Compute(crcBuf.AsSpan(0, needed)));
        }

        /// <summary>Reads a 32-bit big-endian unsigned integer from a byte array at <paramref name="o" />.</summary>
        private static uint ReadU32BE(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        /// <summary>Reads a 32-bit big-endian unsigned integer from a <see cref="BinaryReader" />.</summary>
        private static uint ReadU32BE(BinaryReader r)
        {
            return ((uint)r.ReadByte() << 24) | ((uint)r.ReadByte() << 16) | ((uint)r.ReadByte() << 8) | r.ReadByte();
        }

        /// <summary>Reads a 16-bit big-endian unsigned integer from a <see cref="BinaryReader" />.</summary>
        private static ushort ReadU16BE(BinaryReader r)
        {
            return (ushort)((r.ReadByte() << 8) | r.ReadByte());
        }

        /// <summary>Writes a 32-bit big-endian unsigned integer to a <see cref="BinaryWriter" />.</summary>
        private static void WriteU32BE(BinaryWriter w, uint v)
        {
            w.Write((byte)(v >> 24));
            w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8));
            w.Write((byte)v);
        }

        /// <summary>Returns a 4-byte big-endian representation of <paramref name="v" />.</summary>
        private static byte[] GetU32BE(uint v)
        {
            return [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        }
    }

    #endregion
}