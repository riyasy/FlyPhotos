#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Data;

internal class Photo(string selectedFileName)
{
    public readonly string FileName = selectedFileName;
    public HqDisplayItem? Hq;
    public PreviewDisplayItem? Preview;
    private static readonly Photo _empty = new(string.Empty);
    public static Photo Empty() => _empty;

    public async Task<bool> LoadPreviewFirstPhoto(CanvasControl device)
    {
        var continueLoadingHq = false;
        DisplayItem? firstDisplay = null;

        await Task.Run(GetInitialPreview);

        switch (firstDisplay)
        {
            case PreviewDisplayItem prev:
                Preview = prev;
                continueLoadingHq = true;
                break;
            case HqDisplayItem hq:
                Hq = hq;
                break;
        }

        return continueLoadingHq;

        async Task GetInitialPreview()
        {
            firstDisplay = await ImageUtil.GetFirstPreviewSpecialHandlingAsync(device, FileName);
        }
    }

    public async Task LoadHqFirstPhoto(CanvasControl device)
    {
        async Task GetHqImage()
        {
            Hq = await ImageUtil.GetHqImage(device, FileName);
        }
        await Task.Run(GetHqImage);
    }

    public void LoadHq(CanvasControl device)
    {
        Hq ??= ImageUtil.GetHqImage(device, FileName).GetAwaiter().GetResult();
    }

    public void LoadPreview(CanvasControl device)
    {
        if (Preview == null || Preview.Origin == Origin.ErrorScreen ||
            Preview.Origin == Origin.Undefined)
        {
            Preview = ImageUtil.GetPreview(device, FileName).GetAwaiter().GetResult();
        }
    }

    public DisplayItem? GetDisplayItemBasedOn(DisplayLevel displayLevel)
    {
        return displayLevel switch
        {
            DisplayLevel.Preview => Preview,
            DisplayLevel.Hq => Hq,
            DisplayLevel.PlaceHolder => ImageUtil.GetLoadingIndicator(),
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
        return ImageUtil.GetLoadingIndicator();
    }

    private static readonly HashSet<string> FormatsSupportingTransparency =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",  // Standard for lossless transparency
        ".gif",  // Supports indexed transparency
        ".webp", // Modern format with excellent transparency support
        ".tiff", // Can contain an alpha channel
        ".tif",  // Alternate extension for TIFF
        ".svg",  // Vector format, fully supports transparency
        ".apng", // Animated PNG, supports transparency
        ".ico",  // Icon format, requires transparency
        //".heic", // High Efficiency Image Format (Apple)
        //".heif", // High Efficiency Image Format
        //".avif", // Modern high-compression format
        ".jxl",  // JPEG XL, a newer format that supports transparency
        ".psd"   // Photoshop document
    };

    public bool SupportsTransparency()
    {
        string extension = Path.GetExtension(FileName);
        return !string.IsNullOrEmpty(extension) && FormatsSupportingTransparency.Contains(extension.ToLower());
    }
}