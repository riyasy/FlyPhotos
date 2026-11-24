using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
///     Handles reading and decoding of GIF image files (both static and animated) into
///     display items suitable for rendering via Win2D.
/// </summary>
/// <remarks>
///     For static GIF, a <see cref="StaticHqDisplayItem" /> is returned containing a single
///     <see cref="CanvasBitmap" /> decoded with EXIF orientation applied.
///     For animated GIF (<see cref="BitmapDecoder.FrameCount" /> &gt; 1), an
///     <see cref="AnimatedHqDisplayItem" /> is returned, carrying both the first-frame bitmap
///     (for immediate display before animation begins) and the raw file bytes (consumed by
///     <see cref="FlyPhotos.Display.Animators.GifAnimator" /> for frame-accurate playback).
/// </remarks>
internal static class GifReader
{
    /// <summary>
    ///     Loads the first frame of the GIF file at full resolution as a lightweight preview.
    ///     Used during initial thumbnail/preview loading before the high-quality decode is ready.
    /// </summary>
    /// <param name="ctrl">The Win2D <see cref="ICanvasResourceCreatorWithDpi" /> that owns the GPU device.</param>
    /// <param name="file">The image file.</param>
    /// <returns>The preview. Throws on failure.</returns>
    public static async Task<PreviewDisplayItem> GetFirstFrameFullSize(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, await file.GetStreamAsync());
        var metaData = new ImageMetadata(canvasBitmap.SizeInPixels.Width, canvasBitmap.SizeInPixels.Height);
        return new PreviewDisplayItem(canvasBitmap, Origin.Disk, metaData);
    }

    /// <summary>
    ///     Decodes the GIF file at full quality and returns either a static or animated display item.
    /// </summary>
    /// <param name="ctrl">The Win2D <see cref="ICanvasResourceCreatorWithDpi" /> that owns the GPU device.</param>
    /// <param name="file">The image file.</param>
    /// <returns>
    ///     <list type="bullet">
    ///         <item><see cref="StaticHqDisplayItem" /> — for single-frame GIF files.</item>
    ///         <item>
    ///             <see cref="AnimatedHqDisplayItem" /> — for multi-frame (animated) GIF files,
    ///             carrying the first decoded frame and the raw file bytes for the animator.
    ///         </item>
    ///     </list>
    ///     Throws on failure.
    /// </returns>
    /// <remarks>
    ///     Frame 0 is decoded by <see cref="WicFrame.DecodeAsync" /> on the same decoder instance used for the
    ///     frame-count check — no second decode pass needed.
    ///     For animated files, the raw file bytes are read for the
    ///     animator; this is cheaper than a second full pixel decode.
    /// </remarks>
    public static async Task<HqDisplayItem> GetHq(ICanvasResourceCreatorWithDpi ctrl, ImageFile file)
    {
        var stream = await file.GetStreamAsync();

        // One decoder, one stream read.
        // Frame 0 is decoded from the decoder we already have, into a SoftwareBitmap that Win2D
        // uploads directly, with no managed copy of the pixels.
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);
        using var softwareBitmap = await WicFrame.DecodeAsync(await decoder.GetFrameAsync(0));

        // An animation's raw bytes for the animator (a memcpy, far cheaper than a second decode): read after the
        // decoder is done with the shared stream, and before the GPU bitmap, so a failed read leaves none behind.
        var bytes = decoder.FrameCount > 1 ? await file.GetBytesAsync() : null;
        var firstFrame = softwareBitmap.ToCanvasBitmap(ctrl);

        return bytes != null
            ? new AnimatedHqDisplayItem(firstFrame, Origin.Disk, bytes, ImageFormat.Gif)
            : new StaticHqDisplayItem(firstFrame, Origin.Disk);
    }
}