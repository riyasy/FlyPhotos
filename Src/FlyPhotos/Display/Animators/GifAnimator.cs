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
using Buffer = System.Buffer;

namespace FlyPhotos.Display.Animators;

/// <summary>
///     A real-time animator for GIF images. It composites frames on-demand based on elapsed time,
///     using a zero-allocation GPU texture reuse strategy for optimal rendering performance.
/// </summary>
public partial class GifAnimator : IAnimator
{
    /// <summary>
    ///     Stores pre-parsed metadata for a single animation frame.
    /// </summary>
    private class FrameMetadata
    {
        /// <summary>How long this frame is displayed before advancing to the next one.</summary>
        public TimeSpan Delay { get; init; }

        /// <summary>The boundary rectangle of the frame patch to apply.</summary>
        public Rect Bounds { get; init; }

        /// <summary>The disposal method to apply after this frame is rendered (1=Keep, 2=Background, 3=Previous).</summary>
        public byte Disposal { get; init; }
    }

    /// <inheritdoc />
    public uint PixelWidth { get; }

    /// <inheritdoc />
    public uint PixelHeight { get; }

    // Main animation driver components
    /// <summary>The WIC decoder used to extract individual GIF frames.</summary>
    private readonly BitmapDecoder _decoder;

    /// <summary>The underlying stream supplying the GIF bytes.</summary>
    private readonly IRandomAccessStream _stream;

    /// <summary>The sequentially ordered list of metadata for all frames in the GIF.</summary>
    private readonly List<FrameMetadata> _frameMetadata;

    /// <summary>The total duration of one complete loop of the animation.</summary>
    private readonly TimeSpan _totalAnimationDuration;

    /// <summary>The parent CanvasControl context used to create Win2D resources.</summary>
    private readonly CanvasControl _canvas;

    // Off-screen surfaces for composing frames
    /// <summary>The off-screen render target where frames are accumulated and composited.</summary>
    private readonly CanvasRenderTarget _compositedSurface;

    /// <summary>Used to back up the canvas state for disposal method 3 (Restore to Previous).</summary>
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

    /// <summary>
    ///     The disposal method of the previously drawn frame (1: Do not dispose, 2: Restore to background, 3: Restore to
    ///     previous).
    /// </summary>
    private byte _previousFrameDisposal = 1;

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

    /// <inheritdoc />
    public ICanvasImage Surface => _compositedSurface;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GifAnimator" /> class.
    ///     Private constructor. Use <see cref="CreateAsync" /> to instantiate.
    /// </summary>
    private GifAnimator(
        CanvasControl canvas,
        BitmapDecoder decoder,
        IRandomAccessStream stream,
        List<FrameMetadata> metadata)
    {
        _canvas = canvas;
        _decoder = decoder;
        _stream = stream;
        _frameMetadata = metadata;
        _totalAnimationDuration = TimeSpan.FromMilliseconds(metadata.Sum(m => m.Delay.TotalMilliseconds));

        PixelWidth = _decoder.OrientedPixelWidth;
        PixelHeight = _decoder.OrientedPixelHeight;

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
    }

    /// <summary>
    ///     Asynchronously creates a <see cref="GifAnimator" /> from the raw GIF file bytes.
    /// </summary>
    /// <param name="gifData">Complete raw bytes of the GIF file.</param>
    /// <param name="canvas">The Win2D CanvasControl context.</param>
    /// <returns>A fully initialized <see cref="GifAnimator" />.</returns>
    public static async Task<GifAnimator> CreateAsync(byte[] gifData, CanvasControl canvas)
    {
        var memoryStream = new MemoryStream(gifData);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        // The new private internal method does the rest of the work.
        // The stream will be owned and disposed by the animator instance.
        return await CreateAsyncInternal(randomAccessStream, canvas);
    }

    /// <summary>
    ///     Internal helper to create the animator from a prepared random access stream.
    ///     Handles the WIC decoder initialization and GIF frame metadata parsing.
    /// </summary>
    private static async Task<GifAnimator> CreateAsyncInternal(IRandomAccessStream stream,
        CanvasControl canvas)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.FrameCount == 0)
                throw new ArgumentException("GIF data contains no frames.");

            var metadata = await ReadAllFrameMetadataAsync(decoder);

            // Pass the stream to the constructor so it can be disposed later.
            return new GifAnimator(canvas, decoder, stream, metadata);
        }
        catch (Exception)
        {
            // If creation fails at any point, we must dispose the stream we were given.
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Advances the animation to the correct frame based on the total elapsed time, applying
    ///     standard GIF disposal rules to compose the final surface.
    /// </summary>
    /// <param name="totalElapsedTime">The elapsed wall-clock time.</param>
    public async Task UpdateAsync(TimeSpan totalElapsedTime)
    {
        if (_totalAnimationDuration == TimeSpan.Zero) return;

        // Loop the animation
        var elapsedInLoop = TimeSpan.FromTicks(totalElapsedTime.Ticks % _totalAnimationDuration.Ticks);

        // Find the target frame index
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

        // If we've looped, we need to reset the entire animation state.
        if (targetFrameIndex < _currentFrameIndex)
        {
            // Reset the core state variables to their initial values.
            _currentFrameIndex = -1;
            _previousFrameDisposal = 1; // Default: Do not dispose.
            _previousFrameRect = Rect.Empty;

            // Clear the canvas to start the new loop fresh.
            using var ds = _compositedSurface.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
        }

        // Render all frames from the current one up to the target frame
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
    /// <param name="softwareBitmap">The decoded WIC bitmap frame.</param>
    /// <param name="bounds">The positional boundaries of the frame patch.</param>
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
    ///     Decodes, extracts, and composites a single GIF frame onto the target surface correctly mapping previous disposal
    ///     rules.
    /// </summary>
    /// <param name="frameIndex">The index of the frame to render.</param>
    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // --- AWAIT FIRST ---
        // Perform all asynchronous operations and get all data needed for drawing
        // before we ever open a DrawingSession.
        var frame = await _decoder.GetFrameAsync((uint)frameIndex);
        using var softwareBitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        // Inject bytes into the GPU texture manually
        UpdateSharedBitmap(softwareBitmap, metadata.Bounds);

        // --- THEN DRAW ---
        // Now that we have everything, we can perform all drawing.

        // 2. Prepare for NEXT frame's disposal. This must be done *before* we draw the current frame.
        // If the CURRENT frame's disposal is 3, we back up the canvas's current state.
        if (metadata.Disposal == 3)
        {
            using var backupDs = _previousFrameBackup.CreateDrawingSession();
            backupDs.DrawImage(_compositedSurface);
        }

        // Now, perform all drawing for the current frame in a single, atomic session.
        using (var ds = _compositedSurface.CreateDrawingSession())
        {
            // 1. Handle disposal of the PREVIOUS frame.
            if (_previousFrameDisposal == 2) // Restore to background (transparent)
            {
                // OPTIMIZED: Instead of clearing the whole backup texture to copy a transparent 
                // hole, we can just use CanvasBlend.Copy to punch a transparent hole directly!
                ds.Blend = CanvasBlend.Copy;
                ds.FillRectangle(_previousFrameRect, Colors.Transparent);
                ds.Blend = CanvasBlend.SourceOver; // Reset to normal blending
            }
            else if (_previousFrameDisposal == 3) // Restore to previous state
            {
                // Use the DrawImage overload that specifies CanvasComposite.Copy.
                // This copies the pixels from the backup, replacing what's on the main surface.
                ds.DrawImage(_previousFrameBackup, _previousFrameRect, _previousFrameRect, 1.0f, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
            }

            // 3. Draw the CURRENT frame.
            // This uses the default SourceOver blending, which is correct for overlaying the new frame.
            // DPI BUG FIX: Use the Rect overload to guarantee 1:1 pixel mapping.
            ds.DrawImage(_sharedBitmap, _canvasRect);
        }

        // 4. Update state for the next iteration.
        _previousFrameRect = metadata.Bounds;
        _previousFrameDisposal = metadata.Disposal;
    }

    /// <summary>
    ///     Pre-reads the entire GIF's frame list, extracting dimensions, delays, offsets, and disposal methods from WIC.
    /// </summary>
    private static async Task<List<FrameMetadata>> ReadAllFrameMetadataAsync(BitmapDecoder decoder)
    {
        var metadataList = new List<FrameMetadata>();
        const double defaultGifDelayMs = 100.0;
        const double gifDelayMultiplier = 10.0;

        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var propertyKeys = new[]
            {
                "/grctlext/Delay",
                "/imgdesc/Left",
                "/imgdesc/Top",
                "/imgdesc/Width",
                "/imgdesc/Height",
                "/grctlext/Disposal"
            };
            var props = await frame.BitmapProperties.GetPropertiesAsync(propertyKeys);

            // Get native GIF delay property
            var rawDelay = props.TryGetValue("/grctlext/Delay", out var delay) ? (ushort)delay.Value : 0;
            // The rawDelay is in 1/100s of a second. A value of 0 or 1 is a special case,
            // treated as 100ms for browser compatibility. Any other value is respected.
            var delayMs = rawDelay > 1 ? rawDelay * gifDelayMultiplier : defaultGifDelayMs;
            // Frame Dimensions
            var frameLeft = props.TryGetValue("/imgdesc/Left", out var l) ? (ushort)l.Value : 0;
            var frameTop = props.TryGetValue("/imgdesc/Top", out var t) ? (ushort)t.Value : 0;
            var frameWidth = props.TryGetValue("/imgdesc/Width", out var w) ? (ushort)w.Value : decoder.PixelWidth;
            var frameHeight = props.TryGetValue("/imgdesc/Height", out var h) ? (ushort)h.Value : decoder.PixelHeight;

            // Disposal Method
            var disposal = props.TryGetValue("/grctlext/Disposal", out var d) ? (byte)d.Value : (byte)1;

            metadataList.Add(new FrameMetadata
            {
                Delay = TimeSpan.FromMilliseconds(delayMs),
                Bounds = new Rect(frameLeft, frameTop, frameWidth, frameHeight),
                Disposal = disposal
            });
        }

        return metadataList;
    }

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
}