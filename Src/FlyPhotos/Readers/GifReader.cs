using FlyPhotos.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlyPhotos.Readers;

/// <summary>
/// Reads and decodes GIF files, handling frame composition and disposal logic.
/// NOTE: The consumer of the returned 'DisplayItem' (and the List of CanvasBitmaps within it)
/// is responsible for disposing those resources to prevent VRAM leaks.
/// </summary>
internal class GifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Constants for GIF frame delay calculation
    private const int GifDelayMultiplier = 10; // GIF delay is in 1/100s, this converts to ms.
    private const int MinimumGifDelayMs = 20;  // Treat delays under 20ms as invalid/too fast.
    private const int DefaultGifDelayMs = 100; // Use 100ms for invalid or zero-delay frames.

    public static Task<(bool, DisplayItem)> GetPreview(CanvasControl ctrl, string inputPath)
        => LoadGif(inputPath); // The 'ctrl' parameter is not needed with CanvasDevice.GetSharedDevice()

    public static Task<(bool, DisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
        => LoadGifAsFile(inputPath); // Currently no resolution scaling

    public static async Task<(bool, DisplayItem)> LoadGif(string inputPath)
    {
        // These are the primary off-screen surfaces that must be disposed.
        CanvasRenderTarget compositedSurface = null;
        CanvasRenderTarget previousFrameBackup = null;

        var frames = new List<CanvasBitmap>();

        try
        {
            using var stream = File.OpenRead(inputPath).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            uint frameCount = decoder.FrameCount;

            if (frameCount == 0)
            {
                Logger.Warn("GIF file has no frames: {0}", inputPath);
                return (false, null);
            }

            var canvasDevice = CanvasDevice.GetSharedDevice();            
            var durations = new List<TimeSpan>();
            uint width = decoder.OrientedPixelWidth;
            uint height = decoder.OrientedPixelHeight;

            // Initialize the main drawing surface and the backup surface for disposal method 3.
            compositedSurface = new CanvasRenderTarget(canvasDevice, width, height, 96);
            previousFrameBackup = new CanvasRenderTarget(canvasDevice, width, height, 96);

            // Variables to track the state between frames for correct disposal.
            Rect previousFrameRect = Rect.Empty;
            byte previousFrameDisposal = 1; // Default: Do not dispose.

            for (uint i = 0; i < frameCount; i++)
            {
                var frame = await decoder.GetFrameAsync(i);

                // --- 1. Handle Disposal of the PREVIOUS frame ---
                // This must be done *before* drawing the current frame.
                using (var ds = compositedSurface.CreateDrawingSession())
                {
                    if (previousFrameDisposal == 2) // Value 2: Restore to background (transparent).
                    {
                        ds.FillRectangle(previousFrameRect, Colors.Transparent);
                    }
                    else if (previousFrameDisposal == 3) // Value 3: Restore to previous.
                    {
                        // We draw the backup we saved *before* the previous frame was rendered.
                        ds.DrawImage(previousFrameBackup);
                    }
                }

                // --- 2. Get Properties for the CURRENT frame ---
                var props = await frame.BitmapProperties.GetPropertiesAsync(new[] {
                    "System.Animation.FrameDelay", "/imgdesc/Left", "/imgdesc/Top",
                    "/imgdesc/Width", "/imgdesc/Height", "/grctlext/Disposal"
                });

                double delayMs = DefaultGifDelayMs;
                if (props.TryGetValue("System.Animation.FrameDelay", out var delayValue) && delayValue.Type == PropertyType.UInt16)
                {
                    ushort rawDelay = (ushort)delayValue.Value;
                    delayMs = rawDelay * GifDelayMultiplier;
                    if (delayMs < MinimumGifDelayMs) delayMs = DefaultGifDelayMs;
                }
                durations.Add(TimeSpan.FromMilliseconds(delayMs));

                var frameLeft = props.TryGetValue("/imgdesc/Left", out var l) ? (ushort)l.Value : 0;
                var frameTop = props.TryGetValue("/imgdesc/Top", out var t) ? (ushort)t.Value : 0;
                var frameWidth = props.TryGetValue("/imgdesc/Width", out var w) ? (ushort)w.Value : width;
                var frameHeight = props.TryGetValue("/imgdesc/Height", out var h) ? (ushort)h.Value : height;
                var currentFrameRect = new Rect(frameLeft, frameTop, frameWidth, frameHeight);

                // Get the disposal method for the *current* frame. It will be used in the *next* iteration.
                var currentFrameDisposal = props.TryGetValue("/grctlext/Disposal", out var d) ? (byte)d.Value : (byte)1;

                // --- 3. Prepare for the NEXT frame's disposal ---
                if (currentFrameDisposal == 3) // If the NEXT frame needs to restore to our current state...
                {
                    // ...we must save the canvas state *before* we draw this frame.
                    using var backupDs = previousFrameBackup.CreateDrawingSession();
                    backupDs.DrawImage(compositedSurface);
                }

                // --- 4. Draw the CURRENT frame ---
                var softwareBitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                using (var frameBitmap = CanvasBitmap.CreateFromSoftwareBitmap(canvasDevice, softwareBitmap))
                using (var ds = compositedSurface.CreateDrawingSession())
                {
                    ds.DrawImage(frameBitmap, frameLeft, frameTop);
                }

                // --- 5. Create the Final Frame (Efficient GPU-to-GPU copy) ---
                // This creates a snapshot of the composited surface without involving system RAM.
                var finalFrameForList = new CanvasRenderTarget(canvasDevice, width, height, 96);
                using (var copyDs = finalFrameForList.CreateDrawingSession())
                {
                    copyDs.DrawImage(compositedSurface);
                }
                frames.Add(finalFrameForList);

                // --- 6. Update state for the next loop iteration ---
                previousFrameRect = currentFrameRect;
                previousFrameDisposal = currentFrameDisposal;
            }

            return (true, new DisplayItem(frames, durations, DisplayItem.PreviewSource.FromDisk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load GIF at {0}", inputPath);
            // If an error occurs, we must clean up any frames we've already created.
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
            return (false, null);
        }
        finally
        {
            // Always dispose the main surfaces.
            compositedSurface?.Dispose();
            previousFrameBackup?.Dispose();
        }
    }

    public static async Task<(bool, DisplayItem)> LoadGifAsFile(string inputPath)
    {
        // Basic validation to prevent unnecessary exceptions.
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
        {
            Logger.Warn("Input path is null, empty, or file does not exist: {0}", inputPath);
            return (false, null); 
        }

        try
        {
            // File.ReadAllBytesAsync is the most efficient way to asynchronously 
            // read an entire file into a byte array. It avoids blocking threads.
            byte[] fileBytes = await File.ReadAllBytesAsync(inputPath);
            return (true, new DisplayItem(fileBytes, DisplayItem.PreviewSource.FromDisk)); ;
        }
        catch (Exception ex)
        {
            // Catches potential I/O errors, like permission issues.
            Logger.Error(ex, "Failed to read byte stream from GIF file: {0}", inputPath);
            return (false, null); 
        }
    }
}