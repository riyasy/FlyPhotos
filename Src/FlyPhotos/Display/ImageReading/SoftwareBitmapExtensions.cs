using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Uploads a decoded WIC frame (a <see cref="SoftwareBitmap"/>) to Win2D. The GIF, WebP and TIFF readers and the
/// multi-page renderer use it instead of GetPixelDataAsync + DetachPixelData + CreateFromBytes, which made an
/// extra full-size managed copy of every frame.
/// </summary>
internal static class SoftwareBitmapExtensions
{
    /// <summary>
    /// Uploads a decoded Bgra8 premultiplied frame to Win2D without a managed copy of its pixels.
    /// CreateFromSoftwareBitmap takes the bitmap's DPI, which is the file's (72, 300, ...) and would change the
    /// bitmap's size in DIPs and so its layout. This method therefore sets <paramref name="softwareBitmap"/>'s
    /// DpiX and DpiY to 96 (the DPI CreateFromBytes gave these bitmaps) before the upload.
    /// </summary>
    public static CanvasBitmap ToCanvasBitmap(this SoftwareBitmap softwareBitmap, ICanvasResourceCreator resourceCreator)
    {
        softwareBitmap.DpiX = 96;
        softwareBitmap.DpiY = 96;
        return CanvasBitmap.CreateFromSoftwareBitmap(resourceCreator, softwareBitmap);
    }
}
