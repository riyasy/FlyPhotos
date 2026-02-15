using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;
using PhotoSauce.MagicScaler;

namespace FlyPhotos.Display.ImageReading
{
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

        public static async Task<(bool, PreviewDisplayItem)> GetResized(CanvasControl ctrl, string inputPath)
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
                    var settings = new ProcessImageSettings { Width = 800, Height = 800, ResizeMode = CropScaleMode.Max, HybridMode = HybridScaleMode.Turbo };

                    CanvasBitmap canvasBitmap;
                    if (originalWidth <= 800 && originalHeight <= 800)
                    {
                        // Load directly from file path without resizing
                        using var stream = await ReaderUtil.GetWin2DPerformantStream(inputPath);
                        canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, stream);
                    }
                    else
                    {
                        // Create pipeline for resizing
                        using var pipeline = MagicImageProcessor.BuildPipeline(inputPath, settings);
                        using var ms = new MemoryStream();
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
}
