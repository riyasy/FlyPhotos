using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Services;
using ImageMagick;
using ImageMagick.Formats;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Display.ImageReading;

internal static class MagickNetWrap
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();


    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreator resourceCreator, string path, uint targetLongestSide = 800)
    {
        try
        {
            using var image = new MagickImage(path);
            image.AutoOrient();
            image.Depth = 8;

            var metadata = new ImageMetadata(image.Width, image.Height);

            CanvasBitmap bitmap;
            if (image.Width <= targetLongestSide && image.Height <= targetLongestSide)
            {
                // Small image: pixels are already decoded in memory.
                // No alpha preservation needed — output is cached as JPEG.
                // Hand the raw BGRA buffer straight to Win2D, no encode/decode round-trip.
                var pixelBytes = image.ToByteArray(MagickFormat.Bgra);
                bitmap = CanvasBitmap.CreateFromBytes(
                    resourceCreator, pixelBytes,
                    (int)image.Width, (int)image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
            }
            else
            {
                // Large image: resize first, then encode to a compact JPEG stream.
                // Q80 is indistinguishable from Q95 at thumbnail sizes and produces
                // a much smaller buffer for CanvasBitmap.LoadAsync to decode.
                var geometry = new MagickGeometry(targetLongestSide, targetLongestSide) { Greater = true };
                image.Resize(geometry);

                using var stream = new MemoryStream(256 * 1024); // pre-size to avoid repeated doublings
                image.Format = MagickFormat.Jpeg;
                image.Quality = 80;
                await image.WriteAsync(stream);
                stream.Position = 0;

                bitmap = await CanvasBitmap.LoadAsync(resourceCreator, stream.AsRandomAccessStream());
            }

            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load and resize image with ImageMagick: {path}");
            return (false, PreviewDisplayItem.Empty());
        }
    }
        
    public static async Task<(bool, PreviewDisplayItem)> GetEmbeddedForRawFile(ICanvasResourceCreator resourceCreator, string path)
    {
        try
        {
            using var image = new MagickImage();
            // Setup DNG read defines to request the embedded thumbnail
            var defines = new DngReadDefines { ReadThumbnail = true };
            image.Settings.SetDefines(defines);

            // Ping reads only metadata — much faster than a full decode
            image.Ping(path);

            var verticalOrientation = image.Orientation == OrientationType.RightTop ||
                                      image.Orientation == OrientationType.LeftBottom ||
                                      image.Orientation == OrientationType.RightBottom ||
                                      image.Orientation == OrientationType.LeftTop;

            var width = verticalOrientation ? image.Height : image.Width;
            var height = verticalOrientation ? image.Width : image.Height;
            var metadata = new ImageMetadata(width, height);

            // The dng:thumbnail profile contains the raw embedded preview bytes
            // (virtually always JPEG; CanvasBitmap.LoadAsync handles other WIC formats too).
            // Load them directly — no Magick decode/re-encode round-trip needed.
            var thumbnailData = image.GetProfile("dng:thumbnail")?.ToByteArray();
            if (thumbnailData == null)
                return (false, PreviewDisplayItem.Empty());

            using var stream = new MemoryStream(thumbnailData);
            var bitmap = await CanvasBitmap.LoadAsync(resourceCreator, stream.AsRandomAccessStream());
            return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to extract embedded thumbnail with ImageMagick: {path}");
            return (false, PreviewDisplayItem.Empty());
        }
    }

    public static (bool, HqDisplayItem) GetHq(CanvasControl d2dCanvas, string path)
    {
        try
        {
            // --- 1. Decode at full resolution ---
            using var image = new MagickImage(path);
            image.AutoOrient();
            image.Depth = 8;
            image.Alpha(AlphaOption.Set);

            // --- 2. Premultiply alpha inside Magick, then export raw BGRA pixels.
            // This avoids the two SoftwareBitmap copies that straight→premultiplied
            // conversion would otherwise require (saves ~2× full-res frame in managed memory).
            image.Alpha(AlphaOption.Associate);
            var pixelBytes = image.ToByteArray(MagickFormat.Bgra);

            // --- 3. Upload directly to Win2D — no SoftwareBitmap intermediaries needed ---
            var bitmap = CanvasBitmap.CreateFromBytes(
                d2dCanvas, pixelBytes,
                (int)image.Width, (int)image.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
            return (true, new StaticHqDisplayItem(bitmap, Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return (false, HqDisplayItem.Empty());
        }
    }
}