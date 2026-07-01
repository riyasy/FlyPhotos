#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace FlyPhotos.Core.Model;

internal partial class Photo : IDisposable
{
    public readonly string FilePath;
    public HqDisplayItem? Hq { get; private set; }
    public PreviewDisplayItem? Preview { get; private set; }
    public Thumbnail? Thumbnail { get; private set; }

    public bool SupportsTransparency { get; }

    public bool IsVector { get; }

    public bool IsRaw { get; }

    public Photo(string selectedFilePath)
    {
        FilePath = selectedFilePath;
        string extension = Path.GetExtension(FilePath);
        SupportsTransparency = !string.IsNullOrEmpty(extension) && FormatsSupportingTransparency.Contains(extension);
        IsVector = extension.Contains(".svg", StringComparison.OrdinalIgnoreCase);
        IsRaw = !string.IsNullOrEmpty(extension) && CodecDiscovery.IsRawFile(extension);
    }

    private static readonly Photo _empty = new(string.Empty);
    public static Photo Empty() => _empty;

    public async Task<bool> LoadPreviewFirstPhoto(ICanvasResourceCreatorWithDpi device)
    {
        var continueLoadingHq = false;
        DisplayItem? firstDisplay = null;

        await Task.Run(GetInitialPreview);

        switch (firstDisplay)
        {
            case PreviewDisplayItem prev:
                Preview = prev;
                GenerateThumbnail(device, prev);
                continueLoadingHq = true;
                break;
            case HqDisplayItem hq:
                Hq = hq;
                break;
        }

        return continueLoadingHq;

        async Task GetInitialPreview()
        {
            firstDisplay = await ImageReader.GetFirstPreviewSpecialHandlingAsync(device, FilePath);
        }
    }

    public async Task LoadHqFirstPhoto(ICanvasResourceCreatorWithDpi device)
    {
        async Task GetHqImage()
        {
            Hq = await ImageReader.GetHqImage(device, FilePath);
        }
        await Task.Run(GetHqImage);
    }

    public async Task LoadHq(ICanvasResourceCreatorWithDpi device)
    {
        Hq ??= await ImageReader.GetHqImage(device, FilePath);
    }

    public async Task LoadPreview(ICanvasResourceCreatorWithDpi device)
    {
        if (Preview == null || Preview.Origin == Origin.ErrorScreen ||
            Preview.Origin == Origin.Undefined)
        {
            Preview = await ImageReader.GetPreview(device, FilePath);
            if (Thumbnail == null && Preview != null && !Preview.IsErrorOrUndefined())
                GenerateThumbnail(device, Preview);
        }
    }

    public DisplayItem? GetDisplayItemBasedOn(DisplayLevel displayLevel)
    {
        return displayLevel switch
        {
            DisplayLevel.Preview => Preview,
            DisplayLevel.Hq => Hq,
            DisplayLevel.PlaceHolder => ImageReader.GetLoadingIndicator(),
            _ => null
        };
    }

    public (double, double) GetActualSize()
    {
        if (Hq?.Bitmap != null)
        {
            return (Hq.Bitmap.SizeInPixels.Width, Hq.Bitmap.SizeInPixels.Height);
        }
        if (Preview != null)
        {
            if (Preview.Metadata != null && Preview.Metadata.FullWidth != 0 && Preview.Metadata.FullHeight != 0)
            {
                return (Preview.Metadata.FullWidth, Preview.Metadata.FullHeight);
            }
            return (Preview.Bitmap.SizeInPixels.Width, Preview.Bitmap.SizeInPixels.Height);
        }
        return (100, 100);
    }

    public static DisplayItem GetLoadingIndicator()
    {
        return ImageReader.GetLoadingIndicator();
    }

    private static readonly HashSet<string> FormatsSupportingTransparency =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".gif",
        ".webp",
        ".tiff",
        ".tif",
        ".svg",
        ".apng",
        ".ico",
        ".jxl",
        ".psd"
    };

    public bool IsErrorScreen(DisplayLevel currentDisplayLevel)
    {
        var dispItem = GetDisplayItemBasedOn(currentDisplayLevel);
        return dispItem == null || dispItem.IsErrorOrUndefined();
    }

    private void GenerateThumbnail(ICanvasResourceCreatorWithDpi device, PreviewDisplayItem preview)
    {
        if (preview.Bitmap == null) return;
        try
        {
            var src = preview.Bitmap;
            float srcW = src.SizeInPixels.Width;
            float srcH = src.SizeInPixels.Height;
            float cropSize = Math.Min(srcW, srcH);
            int size = Constants.ThumbnailPixelBufferSize;
            using var rt = new CanvasRenderTarget(device, size, size, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                if (preview.Rotation != 0)
                {
                    var center = new System.Numerics.Vector2(size / 2f, size / 2f);
                    ds.Transform = System.Numerics.Matrix3x2.CreateRotation(
                        (float)(preview.Rotation * Math.PI / 180.0), center);
                }
                ds.DrawImage(src,
                    new Rect(0, 0, size, size),
                    new Rect((srcW - cropSize) / 2, (srcH - cropSize) / 2, cropSize, cropSize),
                    1f, CanvasImageInterpolation.MultiSampleLinear);
            }
            Thumbnail = new Thumbnail(rt.GetPixelBytes());
        }
        catch { }
    }

    public void Dispose()
    {
        Hq?.Dispose();
        Preview?.Dispose();
        Thumbnail?.Dispose();
        Thumbnail = null;
    }

    public void DisposeHqOnly()
    {
        Hq?.Dispose();
        Hq = null;
    }

    public void DisposePreviewOnly()
    {
        Preview?.Dispose();
        Preview = null;
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}