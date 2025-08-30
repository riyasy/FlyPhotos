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
    public DisplayItem? Hq;
    public DisplayItem? Preview;

    public Photo(string selectedFileName)
    {
        FileName = selectedFileName;
    }

    public async Task<bool> LoadPreviewFirstPhoto(CanvasControl device)
    {
        var continueLoadingHq = true;
        DisplayItem? preview = null;

        async Task GetInitialPreview()
        {
            (preview, continueLoadingHq) =
                await ImageUtil.GetFirstPreviewSpecialHandlingAsync(device, FileName);
        }

        await Task.Run(GetInitialPreview);

        if (continueLoadingHq)
            Preview = preview;
        else
            Hq = preview;
        return continueLoadingHq;
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
        if (Hq == null || Hq.PreviewFrom == PreviewSource.ErrorScreen ||
            Hq.PreviewFrom == PreviewSource.Undefined)
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
                return DisplayItem.Empty();
        }
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