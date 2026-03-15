using System;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Animators;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
///     A static reader class responsible for routing HEIC/AVIF files to the correct rendering path.
///     If a file is animated, it decodes the first frame and sets up an <see cref="AnimatedHqDisplayItem" />.
///     Otherwise, it falls back to the static <see cref="NativeHeifReader" /> for single-frame 8K+ viewing.
/// </summary>
internal static class AvifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Parses an AVIF/HEIC file from disk asynchronously, probes it for animation, and returns the appropriate DisplayItem.
    /// By extracting the file into a Byte Array immediately, we ensure the file is unlocked from the filesystem for subsequent deletion.
    /// </summary>
    /// <param name="ctrl">The CanvasControl surface context used for creating Win2D bitmaps.</param>
    /// <param name="inputPath">The absolute path to the .avif or .heic file.</param>
    /// <returns>A tuple of (success, HqDisplayItem).</returns>
    public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl ctrl, string inputPath)
    {
        try
        {
            // 1. Immediately extract the file to a byte array so it gets completely detached from the filesystem.
            //    This immediately frees the OS file lock, allowing the user to delete the file during playback.
            byte[] fileData = await System.IO.File.ReadAllBytesAsync(inputPath);

            // 2. Try to open the animation context to check frame count.
            //    We use the background thread via Task.Run to prevent blocking the UI thread during native parsing.
            bool isAnimated = await Task.Run(() => AvifAnimator.IsAnimated(fileData));

            if (!isAnimated) return NativeHeifReader.GetHq(ctrl, inputPath);

            // It's animated! Decode the first frame to use as the placeholder surface
            var firstFrameResult = NativeHeifReader.GetHq(ctrl, inputPath);
            if (firstFrameResult.Item1 && firstFrameResult.Item2 is StaticHqDisplayItem staticItem)
            {
                // Pass the pre-loaded byte array to the Animator.
                return (true, new AnimatedHqDisplayItem(staticItem.Bitmap, Origin.Disk, fileData));
            }

            // Fallback to static HEIF/AVIF reading
            return NativeHeifReader.GetHq(ctrl, inputPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to process AVIF file at {inputPath}");
            return (false, HqDisplayItem.Empty());
        }
    }
}