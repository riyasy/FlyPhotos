using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
///     A static reader class responsible for routing HEIC/AVIF files to the correct rendering path.
///     If a file is animated, it decodes the first frame and sets up an <see cref="AnimatedHqDisplayItem" />.
///     Otherwise, it falls back to the static <see cref="NativeHeifReader" /> for single-frame 8K+ viewing.
/// </summary>
internal static class NativeAvifReader
{
    /// <summary>
    ///     Parses an AVIF/HEIC file from disk asynchronously, probes it for animation, and returns the appropriate
    ///     DisplayItem.
    ///     By extracting the file into a Byte Array immediately, we ensure the file is unlocked from the filesystem for
    ///     subsequent deletion.
    /// </summary>
    /// <param name="canvas">The ICanvasResourceCreatorWithDpi surface context used for creating Win2D bitmaps.</param>
    /// <param name="file">The image file.</param>
    /// <returns>The HQ item. Throws on failure.</returns>
    public static async Task<HqDisplayItem> GetHq(ICanvasResourceCreatorWithDpi canvas, ImageFile file)
    {
        byte[] fileData = await file.GetBytesAsync();

        using var heifImage = NativeHeifWrapper.DecodePrimaryImageFromMemory(fileData, out bool isAnimated)
                              ?? throw new InvalidDataException("No primary image could be decoded");

        var firstFrameBitmap = heifImage.ToBitmap(canvas);

        if (isAnimated)
            return new AnimatedHqDisplayItem(firstFrameBitmap, Origin.Disk, fileData, ImageFormat.Avif);
        return new StaticHqDisplayItem(firstFrameBitmap, Origin.Disk);
    }
}
