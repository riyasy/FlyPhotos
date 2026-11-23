using System;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using NLog;
using PhotoSauce.MagicScaler;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Display.ImageReading;

internal static class MagicScalerWrap
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    static MagicScalerWrap()
    {
        CodecManager.Configure(codecs =>
        {
            codecs.Clear();
            codecs.UseWicCodecs(WicCodecPolicy.All);
        });
    }

    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreatorWithDpi ctrl, ImageFile file, int maxDimension = 800)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return await Task.Run(Action);
        return await Action();
        async Task<(bool, PreviewDisplayItem)> Action()
        {
            try
            {
                var fileInfo = ImageFileInfo.Load(file.Path);
                int originalWidth = fileInfo.Frames[0].Width;
                int originalHeight = fileInfo.Frames[0].Height;
                var metadata = new ImageMetadata(originalWidth, originalHeight);
                var settings = new ProcessImageSettings { Width = maxDimension, Height = maxDimension, ResizeMode = CropScaleMode.Max, HybridMode = HybridScaleMode.Turbo };

                CanvasBitmap canvasBitmap;
                if (originalWidth <= maxDimension && originalHeight <= maxDimension)
                {
                    // Small enough already: load it without resizing
                    var stream = await file.GetStreamAsync();
                    canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                }
                else
                {
                    // Pulled here, not through WriteOutput: there a codec error is thrown inside a WIC callback,
                    // which no catch can reach, and the process dies.
                    using var pipeline = MagicImageProcessor.BuildPipeline(file.Path, settings);
                    var source = pipeline.PixelSource;
                    int w = source.Width, h = source.Height;
                    int bpp = source.Format == PixelFormats.Bgra32bpp ? 4 : source.Format == PixelFormats.Bgr24bpp ? 3
                        : source.Format == PixelFormats.Grey8bpp ? 1
                        : throw new NotSupportedException($"MagicScaler pixel format {source.Format}");
                    var raw = new byte[w * h * bpp];
                    source.CopyPixels(new System.Drawing.Rectangle(0, 0, w, h), w * bpp, raw);
                    var pixels = ToPremultipliedBgra(raw, bpp);
                    canvasBitmap = CanvasBitmap.CreateFromBytes(ctrl, pixels, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                }

                return (true, new PreviewDisplayItem(canvasBitmap, Origin.Disk, metadata));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return (false, PreviewDisplayItem.Empty());
            }
        }
    }

    // MagicScaler hands out Grey8, Bgr24 or straight-alpha Bgra32; Win2D wants premultiplied BGRA.
    internal static byte[] ToPremultipliedBgra(byte[] src, int bpp)
    {
        int n = src.Length / bpp;
        var dst = bpp == 4 ? src : new byte[n * 4];
        for (int i = 0, s = 0, d = 0; i < n; i++, s += bpp, d += 4)
        {
            if (bpp == 1)
            {
                dst[d] = dst[d + 1] = dst[d + 2] = src[s];
                dst[d + 3] = 255;
            }
            else if (bpp == 3)
            {
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = 255;
            }
            else if (dst[d + 3] is var a and < 255)
            {
                dst[d] = (byte)((dst[d] * a + 127) / 255);
                dst[d + 1] = (byte)((dst[d + 1] * a + 127) / 255);
                dst[d + 2] = (byte)((dst[d + 2] * a + 127) / 255);
            }
        }
        return dst;
    }
}