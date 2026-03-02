using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using PhotoSauce.MagicScaler;

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

    public static async Task<(bool, PreviewDisplayItem)> GetResized(CanvasControl ctrl, string inputPath, int maxDimension = 800)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return await Task.Run(Action);
        return await Action();
        async Task<(bool, PreviewDisplayItem)> Action()
        {
            try
            {
                var fileInfo = ImageFileInfo.Load(inputPath);
                int originalWidth = fileInfo.Frames[0].Width;
                int originalHeight = fileInfo.Frames[0].Height;
                var metadata = new ImageMetadata(originalWidth, originalHeight);
                var settings = new ProcessImageSettings { Width = maxDimension, Height = maxDimension, ResizeMode = CropScaleMode.Max, HybridMode = HybridScaleMode.Turbo };
                // Force JPEG output — a static format cannot carry animation, so MagicScaler
                // processes only frame 0 instead of all frames of an animated GIF.
                settings.TrySetEncoderFormat(ImageMimeTypes.Jpeg);

                CanvasBitmap canvasBitmap;
                if (originalWidth <= maxDimension && originalHeight <= maxDimension)
                {
                    // Load directly from file path without resizing
                    using var stream = await StorageOps.GetWin2DPerformantStream(inputPath);
                    canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                }
                else
                {
                    // Create pipeline for resizing
                    using var pipeline = MagicImageProcessor.BuildPipeline(inputPath, settings);
                    using var ms = new MemoryStream(256 * 1024); // pre-size to avoid repeated buffer doublings
                    pipeline.WriteOutput(ms);
                    canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, ms.AsRandomAccessStream());
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
}