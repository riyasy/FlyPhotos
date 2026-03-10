using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using FlyPhotos.Services;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Handles reading and decoding of WebP image files (both static and animated) into
/// display items suitable for rendering via Win2D.
/// </summary>
/// <remarks>
/// For static WebP, a <see cref="StaticHqDisplayItem"/> is returned containing a single
/// <see cref="CanvasBitmap"/> decoded with EXIF orientation applied.
///
/// For animated WebP (<see cref="BitmapDecoder.FrameCount"/> &gt; 1), an
/// <see cref="AnimatedHqDisplayItem"/> is returned, carrying both the first-frame bitmap
/// (for immediate display before animation begins) and the raw file bytes (consumed by
/// <see cref="FlyPhotos.Display.Animators.WebpAnimator"/> for frame-accurate playback).
/// </remarks>
internal static class WebpReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Identity transform reused across calls — avoids a COM object allocation per decode.
    private static readonly BitmapTransform IdentityTransform = new();

    /// <summary>
    /// Loads the first frame of the WebP file at full resolution as a lightweight preview.
    /// Used during initial thumbnail/preview loading before the high-quality decode is ready.
    /// </summary>
    /// <param name="ctrl">The Win2D <see cref="CanvasControl"/> that owns the GPU device.</param>
    /// <param name="inputPath">Absolute path to the .webp file on disk.</param>
    /// <returns>
    /// A tuple of (<c>success</c>, <see cref="PreviewDisplayItem"/>).
    /// On failure, returns <c>(false, PreviewDisplayItem.Empty())</c> and logs the error.
    /// </returns>
    public static async Task<(bool, PreviewDisplayItem)> GetFirstFrameFullSize(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
            var metaData = new ImageMetadata(canvasBitmap.SizeInPixels.Width, canvasBitmap.SizeInPixels.Height);
            return (true, new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "WebpReader - GetFirstFrameFullSize failed for {0}", inputPath);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    /// <summary>
    /// Decodes the WebP file at full quality and returns either a static or animated display item.
    /// </summary>
    /// <param name="ctrl">The Win2D <see cref="CanvasControl"/> that owns the GPU device.</param>
    /// <param name="inputPath">Absolute path to the .webp file on disk.</param>
    /// <returns>
    /// A tuple of (<c>success</c>, <see cref="HqDisplayItem"/>):
    /// <list type="bullet">
    ///   <item><see cref="StaticHqDisplayItem"/> — for single-frame WebP files.</item>
    ///   <item>
    ///     <see cref="AnimatedHqDisplayItem"/> — for multi-frame (animated) WebP files,
    ///     carrying the first decoded frame and the raw file bytes for the animator.
    ///   </item>
    /// </list>
    /// On failure, returns <c>(false, HqDisplayItem.Empty())</c> and logs the error.
    /// </returns>
    /// <remarks>
    /// Frame 0 pixels are decoded via <see cref="BitmapFrame.GetPixelDataAsync"/> on the
    /// same decoder instance used for the frame-count check — no second decode pass needed.
    /// EXIF orientation is applied so the resulting bitmap is always correctly oriented.
    /// For animated files, the stream is rewound and re-read as a raw byte array for the
    /// animator; this is cheaper than a second full pixel decode.
    /// </remarks>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);

            // One decoder, one stream read.
            // GetPixelDataAsync extracts frame 0 pixels directly from the decoder we already
            // have — no second BitmapDecoder or second CanvasBitmap.LoadAsync needed.
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.WebpDecoderId, stream);
            var frame0 = await decoder.GetFrameAsync(0);
            var pixelProvider = await frame0.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                IdentityTransform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            byte[] pixels = pixelProvider.DetachPixelData(); // DetachPixelData releases the internal buffer

            // OrientedPixelWidth/Height account for any EXIF rotation applied above.
            var firstFrame = CanvasBitmap.CreateFromBytes(
                ctrl, pixels,
                (int)decoder.OrientedPixelWidth,
                (int)decoder.OrientedPixelHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);

            if (decoder.FrameCount > 1) // Animated WebP
            {
                // Seeking back to read the raw bytes is a simple memcpy — far cheaper
                // than a second pixel decode pass.
                stream.Seek(0);
                var bytes = await StorageOps.GetInMemByteArray(stream);
                return (true, new AnimatedHqDisplayItem(firstFrame, Origin.Disk, bytes));
            }

            return (true, new StaticHqDisplayItem(firstFrame, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to process WebP file at {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}
