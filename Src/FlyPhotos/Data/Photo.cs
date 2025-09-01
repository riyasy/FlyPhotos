#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Data;

internal class Photo
{
    public readonly string FileName;
    public HqDisplayItem? Hq;
    public PreviewDisplayItem? Preview;

    public Photo(string selectedFileName)
    {
        FileName = selectedFileName;
    }

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
        if (Hq == null)
        {
            Hq = ImageUtil.GetHqImage(device, FileName).GetAwaiter().GetResult();
        }
    }

    public void LoadPreview(CanvasControl device)
    {
        if (Preview == null || Preview.PreviewFrom == PreviewSource.ErrorScreen ||
            Preview.PreviewFrom == PreviewSource.Undefined)
        {
            Preview = ImageUtil.GetPreview(device, FileName).GetAwaiter().GetResult();
        }
    }

    public DisplayItem? GetDisplayItemBasedOn(DisplayLevel displayLevel)
    {
        switch (displayLevel)
        {
            case DisplayLevel.Preview:
                return Preview;
            case DisplayLevel.Hq:
                return Hq;
            case DisplayLevel.PlaceHolder:
                return ImageUtil.GetLoadingIndicator();
            case DisplayLevel.None:
            default:
                return null;
        }
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

    public static Photo Empty()
    {
        return new Photo(string.Empty);
    }


}