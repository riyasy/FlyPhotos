using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlyPhotos.Controllers.Animators;


public partial class GifAnimator : IAnimator
{
    // Frame metadata that we pre-load.
    private class FrameMetadata
    {
        public TimeSpan Delay { get; init; }
        public Rect Bounds { get; init; }
        public byte Disposal { get; init; }
    }
    public uint PixelWidth { get; }
    public uint PixelHeight { get; }

    // Main animation driver components
    private readonly BitmapDecoder _decoder;
    private readonly IRandomAccessStream _stream;
    private readonly List<FrameMetadata> _frameMetadata;
    private readonly TimeSpan _totalAnimationDuration;
    private readonly CanvasControl _canvas;

    // Off-screen surfaces for composing frames
    private readonly CanvasRenderTarget _compositedSurface;
    private readonly CanvasRenderTarget _previousFrameBackup; // For disposal method 3

    // State for rendering logic
    private int _currentFrameIndex = -1;
    private Rect _previousFrameRect = Rect.Empty;
    private byte _previousFrameDisposal = 1; // 1: Do not dispose


    public ICanvasImage Surface => _compositedSurface;

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

        _compositedSurface = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);
        _previousFrameBackup = new CanvasRenderTarget(_canvas, PixelWidth, PixelHeight, 96);
    }


    public static async Task<GifAnimator> CreateAsync(byte[] gifData, CanvasControl canvas)
    {
        var memoryStream = new MemoryStream(gifData);
        var randomAccessStream = memoryStream.AsRandomAccessStream();
        // The new private internal method does the rest of the work.
        // The stream will be owned and disposed by the animator instance.
        return await CreateAsyncInternal(randomAccessStream, canvas);
    }

    private static async Task<GifAnimator> CreateAsyncInternal(IRandomAccessStream stream,
        CanvasControl canvas)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.FrameCount == 0)
            {
                throw new ArgumentException("GIF data contains no frames.");
            }

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
        {
            for (int i = _currentFrameIndex + 1; i <= targetFrameIndex; i++)
            {
                await RenderFrameAsync(i);
            }
        }
        _currentFrameIndex = targetFrameIndex;
    }

    private async Task RenderFrameAsync(int frameIndex)
    {
        var metadata = _frameMetadata[frameIndex];

        // --- AWAIT FIRST ---
        // Perform all asynchronous operations and get all data needed for drawing
        // before we ever open a DrawingSession.
        var frame = await _decoder.GetFrameAsync((uint)frameIndex);
        var softwareBitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var frameBitmap = CanvasBitmap.CreateFromSoftwareBitmap(_canvas, softwareBitmap);

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
                // To clear a region without blending, we first make our backup surface entirely transparent.
                using (var clearDs = _previousFrameBackup.CreateDrawingSession())
                {
                    clearDs.Clear(Colors.Transparent);
                }

                // Then we copy a transparent rectangle from the backup surface onto the main surface,
                // effectively "stamping" transparency to clear the area.
                var sourceRect = new Rect(0, 0, _previousFrameRect.Width, _previousFrameRect.Height);
                ds.DrawImage(_previousFrameBackup, _previousFrameRect, sourceRect, 1.0f, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
            }
            else if (_previousFrameDisposal == 3) // Restore to previous state
            {
                // Use the DrawImage overload that specifies CanvasComposite.Copy.
                // This copies the pixels from the backup, replacing what's on the main surface.
                ds.DrawImage(_previousFrameBackup, _previousFrameRect, _previousFrameRect, 1.0f, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
            }

            // 3. Draw the CURRENT frame.
            // This uses the default SourceOver blending, which is correct for overlaying the new frame.
            ds.DrawImage(frameBitmap, (float)metadata.Bounds.X, (float)metadata.Bounds.Y);
        }

        // 4. Update state for the next iteration.
        _previousFrameRect = metadata.Bounds;
        _previousFrameDisposal = metadata.Disposal;
    }

    private static async Task<List<FrameMetadata>> ReadAllFrameMetadataAsync(BitmapDecoder decoder)
    {
        var metadataList = new List<FrameMetadata>();
        const double defaultGifDelayMs = 100.0;
        const double minimumGifDelayMs = 20.0;
        const double gifDelayMultiplier = 10.0;

        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var propertyKeys = new[] {
                "System.Animation.FrameDelay",
                "/imgdesc/Left",
                "/imgdesc/Top",
                "/imgdesc/Width",
                "/imgdesc/Height",
                "/grctlext/Disposal"
            };
            var props = await frame.BitmapProperties.GetPropertiesAsync(propertyKeys);

            // CALLING LIKE THIS IS NOT AOT SAFE
            //var props = await frame.BitmapProperties.GetPropertiesAsync([
            //    "System.Animation.FrameDelay", "/imgdesc/Left", "/imgdesc/Top",
            //    "/imgdesc/Width", "/imgdesc/Height", "/grctlext/Disposal"
            //]);

            // Frame Delay
            double delayMs = defaultGifDelayMs;
            if (props.TryGetValue("System.Animation.FrameDelay", out var delayValue) && delayValue.Type == PropertyType.UInt16)
            {
                ushort rawDelay = (ushort)delayValue.Value;
                if (rawDelay > 0)
                {
                    delayMs = rawDelay * gifDelayMultiplier;
                    if (delayMs < minimumGifDelayMs) delayMs = defaultGifDelayMs;
                }
            }

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

    public void Dispose()
    {
        _compositedSurface?.Dispose();
        _previousFrameBackup?.Dispose();
        //_decoder.Dispose(); // BitmapDecoder is IDisposable in UWP/WinUI
        _stream?.Dispose();
        GC.SuppressFinalize(this);
    }
}