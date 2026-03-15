using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
///     A static reader class responsible for routing HEIC/AVIF files to the correct rendering path.
///     If a file is animated, it decodes the first frame and sets up an <see cref="AnimatedHqDisplayItem" />.
///     Otherwise, it falls back to the static <see cref="NativeHeifReader" /> for single-frame 8K+ viewing.
/// </summary>
internal static class NativeAvifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     Parses an AVIF/HEIC file from disk asynchronously, probes it for animation, and returns the appropriate
    ///     DisplayItem.
    ///     By extracting the file into a Byte Array immediately, we ensure the file is unlocked from the filesystem for
    ///     subsequent deletion.
    /// </summary>
    /// <param name="ctrl">The CanvasControl surface context used for creating Win2D bitmaps.</param>
    /// <param name="inputPath">The absolute path to the .avif or .heic file.</param>
    /// <returns>A tuple of (success, HqDisplayItem).</returns>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl canvas, string inputPath)
    {
        try
        {
            byte[] fileData = await File.ReadAllBytesAsync(inputPath);

            var heifImage = NativeHeifWrapper.DecodePrimaryImageFromMemory(fileData, out bool isAnimated);

            if (heifImage == null || heifImage.Pixels == null || heifImage.Pixels.Length == 0)
            {
                Logger.Warn("Failed to extract primary image or AVIF structure from byte array.");
                return (false, HqDisplayItem.Empty());
            }

            var firstFrameBitmap = CanvasBitmap.CreateFromBytes(
                canvas, heifImage.Pixels, heifImage.Width, heifImage.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized
            );

            if (isAnimated)
                return (true, new AnimatedHqDisplayItem(firstFrameBitmap, Origin.Disk, fileData));
            else
                return (true, new StaticHqDisplayItem(firstFrameBitmap, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read Avif High Quality {0}", inputPath);
            return (false, HqDisplayItem.Empty());
        }
    }
}