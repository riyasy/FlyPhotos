using FlyPhotos.Data;
using ImageMagick;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using System;
using System.IO;
using System.Threading.Tasks;

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

        public static async Task<(bool, HqDisplayItem)> GetHq(CanvasControl d2dCanvas, string path)
        {
            try
            {
                using var image = new MagickImage(path);
                using var stream = new MemoryStream();
                image.Format = MagickFormat.Jpeg;
                image.Quality = 100;
                await image.WriteAsync(stream);
                stream.Position = 0;

                // Create a CanvasBitmap from the MemoryStream
                var bitmap = await CanvasBitmap.LoadAsync(d2dCanvas, stream.AsRandomAccessStream());
                return (true, new StaticHqDisplayItem(bitmap, Origin.Disk));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, HqDisplayItem.Empty());
            }
        }
    }
}
