using FlyPhotos.Data;
using ImageMagick;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace FlyPhotos.Readers
{
    internal static class MagickNetWrap
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();


        public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreator resourceCreator, string path, int targetLongestSide = 800) 
        {
            try
            {
                using var image = new MagickImage(path);

                var metadata = new ImageMetadata(image.Width, image.Height);

                // 1. Resize the image within ImageMagick if it's larger than the target.
                // This is the key performance optimization.
                if (image.Width > targetLongestSide || image.Height > targetLongestSide)
                {
                    // The ">" character in the geometry string means "only shrink if larger".
                    // This preserves the original image if it's already small enough.
                    var geometry = new MagickGeometry($"{targetLongestSide}x{targetLongestSide}>");

                    // This preserves the aspect ratio by default.
                    image.Resize(geometry);
                }

                // 2. Write the (now smaller) image to a memory stream.
                // This step is now much faster and uses less memory.
                using var stream = new MemoryStream();
                image.Format = MagickFormat.Jpeg; // Using JPEG for good compression
                image.Quality = 95; // 95 is often a great balance of quality/size for previews
                await image.WriteAsync(stream);
                stream.Position = 0;

                // 3. Create a CanvasBitmap from the smaller MemoryStream.
                // This is also much faster.
                var bitmap = await CanvasBitmap.LoadAsync(resourceCreator, stream.AsRandomAccessStream());

                // Assuming StaticHqDisplayItem is a valid constructor for HqDisplayItem
                return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to load and resize image with ImageMagick: {path}");
                return (false, PreviewDisplayItem.Empty());
            }
        }
        
        public static (bool, HqDisplayItem) GetHq(CanvasControl d2dCanvas, string path)
        {
            try
            {
                // --- 1. Decode PSD/HEIF at full resolution ---
                using var image = new MagickImage(path);
                // Ensure 8-bit depth for compatibility with SoftwareBitmap (BGRA8)
                image.Depth = 8;
                // Ensure alpha channel is included (force RGBA format internally for transparency)
                image.Alpha(AlphaOption.Set);

                // --- 2. Export raw pixels as BGRA (no encoding to PNG/JPEG) ---
                var width = image.Width;
                var height = image.Height;
                // Get raw pixel data in BGRA format (matches SoftwareBitmap)
                var pixelBytes = image.ToByteArray(MagickFormat.Bgra);

                // --- 3. Create SoftwareBitmap from raw pixels ---
                using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    pixelBytes.AsBuffer(),
                    BitmapPixelFormat.Bgra8,
                    (int)width,
                    (int)height,
                    BitmapAlphaMode.Straight);
                using var convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                // --- 4. Convert to CanvasBitmap for Win2D ---
                var bitmap = CanvasBitmap.CreateFromSoftwareBitmap(d2dCanvas, convertedBitmap);
                return (true, new StaticHqDisplayItem(bitmap, Origin.Disk));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, HqDisplayItem.Empty());
            }
        }

        public static async Task<(bool, HqDisplayItem)> GetHq_JPEG_Intermediate(CanvasControl d2dCanvas, string path)
        {
            try
            {
                Debug.WriteLine($"--- Starting image load for: {Path.GetFileName(path)} ---");

                // --- 1. Load original image from disk using ImageMagick ---
                using var image = new MagickImage(path);

                // --- 2. Re-encode the image to a JPEG in a memory stream ---
                using var stream = new MemoryStream();
                image.Format = MagickFormat.Jpeg;
                image.Quality = 100;
                await image.WriteAsync(stream);
                stream.Position = 0; // Reset stream position to the beginning for the next read

                // --- 3. Load the in-memory JPEG stream into a Win2D CanvasBitmap ---
                var bitmap = await CanvasBitmap.LoadAsync(d2dCanvas, stream.AsRandomAccessStream());
                return (true, new StaticHqDisplayItem(bitmap, Origin.Disk)); // Assuming StaticHqDisplayItem constructor
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, HqDisplayItem.Empty());
            }
        }
    }
}
