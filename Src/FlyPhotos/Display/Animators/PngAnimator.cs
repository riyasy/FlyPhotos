using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Buffer = System.Buffer;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     A real-time animator for APNG files, designed to mirror the logic and API
///     of a GIF animator. It composites frames on-demand based on elapsed time,
///     providing an efficient way to play complex animations in a Win2D context.
/// </summary>
public partial class PngAnimator : IAnimator
{
    #region Nested Classes and Constants

    // Constants for APNG specification values.
    /// <summary>Do not dispose the previous frame; leave its raw pixels on the canvas.</summary>
    private const byte APNG_DISPOSE_OP_NONE = 0;

    /// <summary>Clear the previous frame's bounds to transparent black before drawing the new frame.</summary>
    private const byte APNG_DISPOSE_OP_BACKGROUND = 1;

    /// <summary>Restore the canvas to the state it was in before the previous frame was drawn.</summary>
    private const byte APNG_DISPOSE_OP_PREVIOUS = 2;

    /// <summary>Overwrite existing pixels without alpha blending.</summary>
    private const byte APNG_BLEND_OP_SOURCE = 0;

    /// <summary>Composite the new frame over existing pixels using alpha blending.</summary>
    private const byte APNG_BLEND_OP_OVER = 1;

    /// <summary>
    ///     Stores pre-parsed metadata for a single APNG animation frame.
    /// </summary>
    private class ApngFrameMetadata
    {
        /// <summary>How long this frame is displayed before advancing to the next one.</summary>
        public TimeSpan Delay { get; init; }

        /// <summary>The boundary rectangle of the frame patch to apply.</summary>
        public Rect Bounds { get; init; }

        /// <summary>How the previous frame's pixels should be handled before rendering.</summary>
        public byte DisposeOp { get; init; }

        /// <summary>How this frame's pixels should be composited over the canvas.</summary>
        public byte BlendOp { get; init; }

        /// <summary>The raw PNG chunks (IDAT or fdAT) that contain this frame's encoded pixel data.</summary>
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

    // APNG structure data, held for the lifetime of the animator
    /// <summary>The underlying stream supplying the APNG bytes.</summary>
    private readonly IRandomAccessStream _stream;

    /// <summary>The main image header chunk defining canvas dimensions, bit depth, and color type.</summary>
    private readonly Parser.PngChunk _ihdrChunk;

    /// <summary>Non-image chunks (like PLTE palettes, tRNS transparency) needed to decode extracted frames.</summary>
    private readonly List<Parser.PngChunk> _globalChunks;

    /// <summary>The sequentially ordered list of metadata for all frames.</summary>
    private readonly List<ApngFrameMetadata> _frameMetadata;

    /// <summary>The total duration of one complete loop of the animation.</summary>
    private readonly TimeSpan _totalAnimationDuration;

    /// <summary>The parent CanvasControl context used to create Win2D resources.</summary>
    private readonly CanvasControl _canvas;

    // Off-screen surfaces for composing frames
    /// <summary>The off-screen render target where frames are accumulated and composited.</summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>Used to back up the canvas state for DISPOSE_OP_PREVIOUS.</summary>
    private readonly CanvasRenderTarget _previousFrameBackup;

    /// <summary>
    ///     Cached full-canvas <see cref="Rect" />. Avoids allocating a new struct on every draw call.
    /// </summary>
    private readonly Rect _canvasRect;

    // State for rendering logic
    /// <summary>The index of the last fully composited frame.</summary>
    private int _currentFrameIndex = -1;

    /// <summary>The bounds of the previously drawn frame, used for applying disposal rules.</summary>
    private Rect _previousFrameRect = Rect.Empty;

    /// <summary>The disposal method of the previously drawn frame.</summary>
    private byte _previousFrameDisposal = APNG_DISPOSE_OP_NONE;

    // Zero-allocation texture updating state
    /// <summary>A single, reusable shared GPU texture wrapped over the pixel buffer.</summary>
    private readonly CanvasBitmap _sharedBitmap;

    /// <summary>The raw, full-screen byte buffer used to feed the <see cref="_sharedBitmap" />.</summary>
    private readonly byte[] _pixelBuffer;

    /// <summary>An IBuffer wrapper over <see cref="_pixelBuffer" /> for native interop zero-copy operations.</summary>
    private readonly IBuffer _pixelIBuffer;

    /// <summary>A temporary buffer used to extract the raw pixel bytes from a decoded <see cref="SoftwareBitmap" /> patch.</summary>
    private byte[] _patchBuffer;

    /// <summary>An IBuffer wrapper over <see cref="_patchBuffer" /> for faster native interop copying.</summary>
    private IBuffer _patchIBuffer;

    /// <summary>The rectangle representing the footprint of the last patch drawn, to cleanly clear the buffer region.</summary>
    private Rect _prevPatchRect = Rect.Empty;

    #endregion

    #region Creation and Initialization

    /// <summary>
    ///     Initializes a new instance of the <see cref="PngAnimator" /> class.
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    /// </summary>
    private PngAnimator(
        CanvasControl canvas,
        IRandomAccessStream stream,
        Parser.ApngData apngData,
        List<ApngFrameMetadata> metadata)
    {
        _canvas = canvas;
        _stream = stream;
        _ihdrChunk = apngData.IhdrChunk;
        _globalChunks = apngData.GlobalChunks;
        _frameMetadata = metadata;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(metadata.Sum(m => m.Delay.TotalMilliseconds));

        PixelWidth = apngData.CanvasWidth;
        PixelHeight = apngData.CanvasHeight;

        _pixelBuffer = new byte[PixelWidth * PixelHeight * 4];
        _pixelIBuffer = _pixelBuffer.AsBuffer();

        _patchBuffer = new byte[PixelWidth * PixelHeight * 4];
        _patchIBuffer = _patchBuffer.AsBuffer();

        _sharedBitmap = CanvasBitmap.CreateFromBytes(
            canvas.Device,
            _pixelBuffer,
            (int)PixelWidth,
            (int)PixelHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);

        _canvasRect = new Rect(0, 0, PixelWidth, PixelHeight);
        _compositedSurface = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);
        _previousFrameBackup = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);

        // If the first frame is not the default image, render it immediately.
        if (!apngData.IsDefaultImageFirstFrame)
        {
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }
        else
        {
            // If the default image IS the first frame, we need to draw it.
            // This is a special case handled by the parser's metadata for frame 0.
            // We force an initial render of frame 0.
            Task.Run(async () => await RenderFrameAsync(0)).Wait();
            _currentFrameIndex = 0;
        }
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="PngAnimator" /> from the raw APNG file bytes.
    /// </summary>
    /// <param name="apngData">Complete raw bytes of the APNG file.</param>
    /// <param name="canvas">The Win2D CanvasControl context.</param>
    /// <returns>A fully initialized <see cref="PngAnimator" />.</returns>
    public static async Task<PngAnimator> CreateAsync(byte[] apngData, CanvasControl canvas)
    {
        var memoryStream = new MemoryStream(apngData);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        return await CreateAsyncInternal(randomAccessStream, canvas);
    }

    /// <summary>
    ///     Internal helper that handles stream wrapping and triggers the initial background parsing
    ///     of the APNG chunks to extract frame instructions.
    /// </summary>
    private static async Task<PngAnimator> CreateAsyncInternal(IRandomAccessStream stream,
        CanvasControl canvas)
    {
        try
        {
            // 1. Parse the entire APNG structure first.
            var apngData = await Parser.ParseApngStreamAsync(stream);
            if (apngData.FrameControls.Count == 0)
                throw new ArgumentException("APNG file contains no animation frames.");

            // 2. Convert parser's FrameControl objects into our internal ApngFrameMetadata.
            var metadata = new List<ApngFrameMetadata>();
            foreach (var fc in apngData.FrameControls)
            {
                // Calculate delay. If denominator is 0, spec says treat numerator as milliseconds.
                double delayMs = 100.0; // Default delay
                if (fc.DelayNum > 0)
                    delayMs = fc.DelayDen == 0 ? fc.DelayNum : (double)fc.DelayNum / fc.DelayDen * 1000.0;

                metadata.Add(new ApngFrameMetadata
                {
                    Delay = TimeSpan.FromMilliseconds(delayMs),
                    Bounds = new Rect(fc.XOffset, fc.YOffset, fc.Width, fc.Height),
                    DisposeOp = fc.DisposeOp,
                    BlendOp = fc.BlendOp,
                    FrameDataChunks = fc.FrameDataChunks
                });
            }

            return new PngAnimator(canvas, stream, apngData, metadata);
        }
        catch (Exception)
        {
            // If creation fails, we are responsible for the stream.
            stream.Dispose();
            throw;
        }
    }

    #endregion

    #region Animation Logic

    /// <summary>
    ///     Advances the animation to the correct frame based on the total elapsed time, applying
    ///     standard APNG blending and disposal rules to compose the final surface.
    /// </summary>
    /// <param name="totalElapsedTime">The elapsed wall-clock time.</param>
    public async Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_totalAnimationDuration == TimeSpan.Zero) return;

        var elapsedInLoop = TimeSpan.FromTicks(totalElapsedTime.Ticks % _totalAnimationDuration.Ticks);

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

        if (targetFrameIndex < _currentFrameIndex)
        {
            _currentFrameIndex = -1;
            _previousFrameDisposal = APNG_DISPOSE_OP_NONE;
            _previousFrameRect = Rect.Empty;

            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        if (targetFrameIndex > _currentFrameIndex)
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
                await RenderFrameAsync(i);

        _currentFrameIndex = targetFrameIndex;
    }

    /// <summary>
    ///     Copies the software bitmap patch into the reusable <see cref="_pixelBuffer" /> and pushes it to the GPU via
    ///     <see cref="_sharedBitmap" />.
    ///     Eliminates per-frame texture GC and GPU reallocations.
    /// </summary>
    /// <param name="softwareBitmap">The decoded WIC bitmap frame representing a single APNG segment.</param>
    /// <param name="bounds">The positional boundaries of the frame patch on the full canvas.</param>
    private void UpdateSharedBitmap(SoftwareBitmap softwareBitmap, Rect bounds)
    {
        int pw = softwareBitmap.PixelWidth;
        int ph = softwareBitmap.PixelHeight;

        // 1. FAST PATH: If the frame covers the whole canvas, skip CPU mapping entirely.
        if (pw == PixelWidth && ph == PixelHeight && bounds.X == 0 && bounds.Y == 0)
        {
            softwareBitmap.CopyToBuffer(_pixelIBuffer);
            _sharedBitmap.SetPixelBytes(_pixelBuffer);
            _prevPatchRect = new Rect(0, 0, PixelWidth, PixelHeight);
            return;
        }

        // 2. Safely ensure the patch buffer is large enough
        int requiredPatchSize = pw * ph * 4;
        if (requiredPatchSize > _patchBuffer.Length)
        {
            _patchBuffer = new byte[requiredPatchSize];
            _patchIBuffer = _patchBuffer.AsBuffer();
        }

        // Copy pixels into our reusable pre-allocated patch buffer to avoid GC array allocations
        softwareBitmap.CopyToBuffer(_patchIBuffer);

        int fullStride = (int)PixelWidth * 4;
        int patchStride = pw * 4;

        // 3. Clear the footprint of the previous patch in the full-screen pixel buffer
        if (_prevPatchRect.Width > 0 && _prevPatchRect.Height > 0)
        {
            int prevW = (int)_prevPatchRect.Width;
            int prevH = (int)_prevPatchRect.Height;
            int prevX = (int)_prevPatchRect.X;
            int prevY = (int)_prevPatchRect.Y;
            int prevStride = prevW * 4;
            for (int row = 0; row < prevH; row++)
                Array.Clear(_pixelBuffer, (prevY + row) * fullStride + prevX * 4, prevStride);
        }

        // 4. Map the new patch into the correct geometrical location within the full-screen pixel buffer
        int dstX = (int)bounds.X;
        int dstY = (int)bounds.Y;
        int safeW = Math.Min(pw, (int)PixelWidth - dstX);
        int safeH = Math.Min(ph, (int)PixelHeight - dstY);
        int safePatchStride = safeW * 4;

        for (int row = 0; row < safeH; row++)
            Buffer.BlockCopy(_patchBuffer, row * patchStride, _pixelBuffer, (dstY + row) * fullStride + dstX * 4, safePatchStride);

        // 5. One single GPU-transfer 
        _sharedBitmap.SetPixelBytes(_pixelBuffer);

        // IMPORTANT: Record safeW/safeH so we don't clear out-of-bounds on the next frame
        _prevPatchRect = new Rect(dstX, dstY, safeW, safeH);
    }

    /// <summary>
    ///     Decodes an isolated APNG sub-frame, extracts it bounds, and composites it
    ///     onto the target surface mapped to previous blend and disposal rules.
    /// </summary>
    /// <param name="frameIndex">The index of the frame to render.</param>
    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // --- AWAIT FIRST ---
        // Decode the frame's IDAT patch into a SoftwareBitmap
        using var softwareBitmap = await Parser.CreateSoftwareBitmapFromChunksAsync(
            _ihdrChunk,
            _globalChunks,
            metadata.FrameDataChunks,
            (uint)metadata.Bounds.Width,
            (uint)metadata.Bounds.Height);

        UpdateSharedBitmap(softwareBitmap, metadata.Bounds);

        // --- THEN DRAW ---

        // 2. Prepare for NEXT frame's disposal.
        // If the CURRENT frame's disposal is PREVIOUS, we back up the canvas's current state.
        if (metadata.DisposeOp == APNG_DISPOSE_OP_PREVIOUS)
        {
            var backupRegion = new Rect(0, 0, _compositedSurface.Size.Width, _compositedSurface.Size.Height);
            using var backupDs = _previousFrameBackup.CreateDrawingSession();
            backupDs.DrawImage(_compositedSurface, backupRegion, backupRegion);
        }

        // Now, perform all drawing for the current frame in a single, atomic session.
        using (var ds = _compositedSurface.CreateDrawingSession())
        {
            switch (_previousFrameDisposal)
            {
                case APNG_DISPOSE_OP_BACKGROUND:
                    ds.Blend = CanvasBlend.Copy;
                    ds.FillRectangle(_previousFrameRect, Colors.Transparent);
                    ds.Blend = CanvasBlend.SourceOver;
                    break;
                case APNG_DISPOSE_OP_PREVIOUS:
                    ds.DrawImage(_previousFrameBackup, _previousFrameRect, _previousFrameRect);
                    break;
            }

            switch (metadata.BlendOp)
            {
                // 3. Draw the CURRENT frame. This is where APNG's BlendOp matters.
                case APNG_BLEND_OP_SOURCE:
                    ds.Blend = CanvasBlend.Copy;
                    ds.FillRectangle(metadata.Bounds, Colors.Transparent);
                    ds.Blend = CanvasBlend.SourceOver;
                    // DPI BUG FIX: Use the Rect overload to guarantee 1:1 pixel mapping.
                    ds.DrawImage(_sharedBitmap, _canvasRect);
                    break;
                case APNG_BLEND_OP_OVER:
                    // DPI BUG FIX: Use the Rect overload to guarantee 1:1 pixel mapping.
                    ds.DrawImage(_sharedBitmap, _canvasRect);
                    break;
            }
        }

        // 4. Update state for the next iteration.
        _previousFrameRect = metadata.Bounds;
        _previousFrameDisposal = metadata.DisposeOp;
    }

    #endregion

    /// <summary>
    ///     Disposes off-screen bitmaps, textures, and unmanaged active streams.
    /// </summary>
    public void Dispose()
    {
        _compositedSurface?.Dispose();
        _previousFrameBackup?.Dispose();
        _sharedBitmap?.Dispose();
        _stream?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Private APNG Parser

    /// <summary>
    ///     A self-contained static parser that reads an APNG stream and extracts its
    ///     structural components without performing any rendering.
    /// </summary>
    private static class Parser
    {
        /// <summary>A single generic PNG chunk.</summary>
        public class PngChunk
        {
            public string Type;
            public byte[] Data;
        }

        /// <summary>The metadata extracted from an APNG 'fcTL' (frame control) chunk.</summary>
        public class FrameControl
        {
            public uint SequenceNumber, Width, Height, XOffset, YOffset;
            public ushort DelayNum, DelayDen;
            public byte DisposeOp, BlendOp;
            public List<PngChunk> FrameDataChunks { get; } = [];
        }

        /// <summary>Aggregated parsed data from the entire APNG file.</summary>
        public class ApngData
        {
            public PngChunk IhdrChunk { get; init; }
            public uint CanvasWidth { get; init; }
            public uint CanvasHeight { get; init; }
            public List<PngChunk> GlobalChunks { get; init; }
            public List<FrameControl> FrameControls { get; init; }
            public bool IsDefaultImageFirstFrame { get; init; }
        }

        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        private const string AcTL = "acTL", FcTL = "fcTL", FdAT = "fdAT";

        /// <summary>
        ///     Reads through an entire stream synchronously (wrapped in Task.Run) to parse the PNG/APNG structure.
        ///     Extracts all standard chunks, IDAT lists, and fcTL/fdAT sequence links.
        /// </summary>
        public static Task<ApngData> ParseApngStreamAsync(IRandomAccessStream stream)
        {
            // Parsing is CPU-bound, so run it on a background thread.
            return Task.Run(() =>
            {
                stream.Seek(0);
                using var reader = new BinaryReader(stream.AsStreamForRead());

                if (!reader.ReadBytes(PngSignature.Length).SequenceEqual(PngSignature))
                    throw new ArgumentException("Not a valid PNG file.");

                var chunks = ReadAllChunks(reader);
                var globalChunks = new List<PngChunk>();
                var defaultImageIdatChunks = new List<PngChunk>();
                var frameControls = new List<FrameControl>();
                PngChunk ihdrChunk = null;

                foreach (var chunk in chunks)
                    switch (chunk.Type)
                    {
                        case "IHDR":
                            ihdrChunk = chunk;
                            globalChunks.Add(chunk);
                            break;
                        case AcTL: break; // Just confirms it's an APNG
                        case FcTL: frameControls.Add(ParseFrameControl(chunk)); break;
                        case FdAT:
                            if (frameControls.Count != 0) frameControls.Last().FrameDataChunks.Add(ConvertFdatToIdat(chunk));
                            break;
                        case "IDAT":
                            if (frameControls.Count != 0)
                                frameControls.Last().FrameDataChunks.Add(chunk);
                            else
                                defaultImageIdatChunks.Add(chunk);
                            break;
                        default:
                            if (frameControls.Count == 0 && chunk.Type != "IEND")
                                globalChunks.Add(chunk);
                            break;
                    }

                if (ihdrChunk == null) throw new InvalidDataException("IHDR chunk not found.");

                var orderedFrames = frameControls.OrderBy(fc => fc.SequenceNumber).ToList();
                bool defaultImageIsFirstFrame = false;

                // If the default image exists, check if it's used as the first frame.
                if (defaultImageIdatChunks.Count != 0)
                {
                    var firstFrameControl = orderedFrames.FirstOrDefault();
                    if (firstFrameControl?.SequenceNumber == 0)
                    {
                        // The default IDATs belong to the first frame.
                        firstFrameControl.FrameDataChunks.InsertRange(0, defaultImageIdatChunks);
                        defaultImageIsFirstFrame = true;
                    }
                }

                return new ApngData
                {
                    IhdrChunk = ihdrChunk,
                    CanvasWidth = ReadUInt32BigEndian(ihdrChunk.Data, 0),
                    CanvasHeight = ReadUInt32BigEndian(ihdrChunk.Data, 4),
                    GlobalChunks = [.. globalChunks.Where(c => c.Type != "IHDR")],
                    FrameControls = orderedFrames,
                    IsDefaultImageFirstFrame = defaultImageIsFirstFrame
                };
            });
        }

        /// <summary>
        ///     Reconstructs a valid, stand-alone PNG file in-memory using an IHDR chunk, global chunks (PLTE, tRNS), and a list of
        ///     specific IDAT chunks.
        ///     Decodes this combined buffer with WIC to return a software bitmap representing the isolated frame patch.
        /// </summary>
        public static async Task<SoftwareBitmap> CreateSoftwareBitmapFromChunksAsync(PngChunk ihdrChunk, IEnumerable<PngChunk> otherChunks,
            IEnumerable<PngChunk> dataChunks, uint? frameWidth = null, uint? frameHeight = null)
        {
            using var ms = new InMemoryRandomAccessStream();
            await using var writer = new BinaryWriter(ms.AsStreamForWrite());
            writer.Write(PngSignature);
            var ihdrData = (byte[])ihdrChunk.Data.Clone();
            if (frameWidth.HasValue && frameHeight.HasValue)
            {
                Array.Copy(GetBytesBigEndian(frameWidth.Value), 0, ihdrData, 0, 4);
                Array.Copy(GetBytesBigEndian(frameHeight.Value), 0, ihdrData, 4, 4);
            }

            WriteChunk(writer, "IHDR", ihdrData);
            foreach (var chunk in otherChunks) WriteChunk(writer, chunk.Type, chunk.Data);
            foreach (var chunk in dataChunks) WriteChunk(writer, chunk.Type, chunk.Data);
            WriteChunk(writer, "IEND", []);

            await writer.BaseStream.FlushAsync();
            ms.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ms);
            var frame = await decoder.GetFrameAsync(0);
            return await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        private static List<PngChunk> ReadAllChunks(BinaryReader r)
        {
            var c = new List<PngChunk>();
            while (r.BaseStream.Position < r.BaseStream.Length)
                try
                {
                    var len = ReadUInt32BigEndian(r);
                    var type = Encoding.ASCII.GetString(r.ReadBytes(4));
                    var data = r.ReadBytes((int)len);
                    ReadUInt32BigEndian(r); // Skip CRC
                    c.Add(new PngChunk { Type = type, Data = data });
                    if (string.Equals(type, "IEND")) break;
                }
                catch (EndOfStreamException)
                {
                    break;
                }

            return c;
        }

        private static FrameControl ParseFrameControl(PngChunk fc)
        {
            using var r = new BinaryReader(new MemoryStream(fc.Data));
            return new FrameControl
            {
                SequenceNumber = ReadUInt32BigEndian(r),
                Width = ReadUInt32BigEndian(r),
                Height = ReadUInt32BigEndian(r),
                XOffset = ReadUInt32BigEndian(r),
                YOffset = ReadUInt32BigEndian(r),
                DelayNum = ReadUInt16BigEndian(r),
                DelayDen = ReadUInt16BigEndian(r),
                DisposeOp = r.ReadByte(),
                BlendOp = r.ReadByte()
            };
        }

        private static PngChunk ConvertFdatToIdat(PngChunk f)
        {
            var d = new byte[f.Data.Length - 4];
            Array.Copy(f.Data, 4, d, 0, d.Length);
            return new PngChunk { Type = "IDAT", Data = d };
        }

        private static void WriteChunk(BinaryWriter w, string t, byte[] d)
        {
            var typeBytes = Encoding.ASCII.GetBytes(t);
            var crcBytes = new byte[typeBytes.Length + d.Length];
            Buffer.BlockCopy(typeBytes, 0, crcBytes, 0, typeBytes.Length);
            if (d.Length > 0) Buffer.BlockCopy(d, 0, crcBytes, typeBytes.Length, d.Length);

            WriteUInt32BigEndian(w, (uint)d.Length);
            w.Write(typeBytes);
            if (d.Length > 0) w.Write(d);
            WriteUInt32BigEndian(w, Crc32.Compute(crcBytes));
        }

        private static uint ReadUInt32BigEndian(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static uint ReadUInt32BigEndian(BinaryReader r)
        {
            return (uint)((r.ReadByte() << 24) | (r.ReadByte() << 16) | (r.ReadByte() << 8) | r.ReadByte());
        }

        private static ushort ReadUInt16BigEndian(BinaryReader r)
        {
            return (ushort)((r.ReadByte() << 8) | r.ReadByte());
        }

        private static void WriteUInt32BigEndian(BinaryWriter w, uint v)
        {
            w.Write((byte)(v >> 24));
            w.Write((byte)(v >> 16));
            w.Write((byte)(v >> 8));
            w.Write((byte)v);
        }

        private static byte[] GetBytesBigEndian(uint v)
        {
            return [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        }
    }

    #endregion
}