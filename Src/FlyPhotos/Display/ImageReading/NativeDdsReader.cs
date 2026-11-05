#nullable enable
using System;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using NLog;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// DDS textures through FlyNativeLibDds (DirectXTex). Reads every DDS layout ImageMagick gets wrong or
/// can't read (BC4-BC7, sRGB, DX10 headers, mask-ordered RGBA - #273), and is several times faster.
/// The native side returns premultiplied BGRA8, so the bytes go straight into a CanvasBitmap.
/// </summary>
internal static class NativeDdsReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The full-size top mip level, capped at the GPU's maximum bitmap size. A DDS is at most 16384 px (the
    /// D3D11 limit), which most GPUs can hold as is; an older GPU limited to 8192 gets a downscaled image
    /// instead of an error. So a DDS is never "oversized" and needs no tiled path (see ImageReader.IsOversized).
    /// </summary>
    public static (bool, HqDisplayItem) GetHq(ICanvasResourceCreatorWithDpi ctrl, string path)
    {
        try
        {
            var image = NativeDdsWrapper.Decode(path, maxDimension: ctrl.Device.MaximumBitmapSizeInPixels);
            return (true, new StaticHqDisplayItem(ToBitmap(ctrl, image), Origin.Disk));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NativeDdsReader.GetHq failed for {0}", path);
            return (false, HqDisplayItem.Empty());
        }
    }

    /// <summary>
    /// A preview whose longer side is at most <paramref name="maxDimension"/>, taken from a stored mip level
    /// when one fits (no full decode), else scaled down natively.
    /// </summary>
    public static (bool, PreviewDisplayItem) GetResized(ICanvasResourceCreatorWithDpi ctrl, string path, int maxDimension = 800)
    {
        try
        {
            var image = NativeDdsWrapper.Decode(path, maxDimension);
            var metadata = new ImageMetadata(image.PrimaryImageWidth, image.PrimaryImageHeight);
            return (true, new PreviewDisplayItem(ToBitmap(ctrl, image), Origin.Disk, metadata));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "NativeDdsReader.GetResized failed for {0}", path);
            return (false, PreviewDisplayItem.Empty());
        }
    }

    private static CanvasBitmap ToBitmap(ICanvasResourceCreatorWithDpi ctrl, NativeDdsWrapper.DdsImage image) =>
        CanvasBitmap.CreateFromBytes(ctrl, image.Pixels, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
}
