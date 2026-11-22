using System;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.ImageRendering;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.Canvas;

/// <summary>The renderer for each kind of display item. UI thread: renderers are made there and then used on the W2D thread.</summary>
internal static class RendererFactory
{
    /// <param name="canvas">The control the renderer draws into; renderers create their bitmaps on its device.</param>
    /// <param name="photo">The photo the item belongs to; the PDF renderer reads its path and borrows its preview.</param>
    /// <param name="item">The leased item to draw. The renderer draws from it but doesn't own the lease.</param>
    /// <param name="invalidate">Wakes the render loop; renderers call it when they have something new to draw.</param>
    /// <exception cref="ArgumentOutOfRangeException">An item kind with no renderer.</exception>
    public static IRenderer Create(CanvasAnimatedControl canvas, Photo photo, DisplayItem item, Action invalidate) =>
        // The order matters: the specific HQ kinds derive from HqDisplayItem, so they come before it.
        item switch
        {
            // The first frame, at once; CanvasController swaps in the animator once it is built.
            AnimatedHqDisplayItem a => new StaticImageRenderer(canvas, a.Bitmap, invalidate),
            MultiPageHqDisplayItem m => new MultiPageRenderer(canvas, m.Bitmap, m.FileAsByteArray, invalidate, m.PageOrder),
            TiledHqDisplayItem t => new TiledImageRenderer(canvas, t, invalidate),
            SvgHqDisplayItem s => new SvgVectorRenderer(canvas, s, invalidate),
            // The PDF item holds no pixels: the renderer is lent the preview (its own lease) until its first page lands.
            // It reads the path whenever it opens the file, so a rename is followed.
            PdfHqDisplayItem p => new PdfRenderer(canvas, p, () => photo.FilePath, LeasePreview(photo), invalidate),
            // A single-frame HQ: the renderer builds a mip chain for smooth zoomed-out drawing.
            HqDisplayItem h => new StaticImageRenderer(canvas, h.Bitmap, invalidate, mipChainOwner: h),
            // A preview: small and on screen briefly, so no mip chain.
            PreviewDisplayItem p => new StaticImageRenderer(canvas, p.Bitmap, invalidate),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.GetType().Name, "no renderer for this item")
        };

    /// <summary>A new lease on the photo's preview for the PDF renderer to release, or null when there is none.</summary>
    private static PreviewDisplayItem LeasePreview(Photo photo) =>
        photo.Slots.Get(DisplayLevel.Preview) is PreviewDisplayItem preview && preview.TryAcquire() ? preview : null;
}
