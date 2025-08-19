#nullable enable
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using static FlyPhotos.Controllers.PhotoDisplayController;

namespace FlyPhotos.Data;

internal class Photo
{
    public static int CurrentDisplayIndex;
    public static DisplayLevel CurrentDisplayLevel;
    public static int PhotosCount;

    public readonly string FileName;
    public DisplayItem? Hq;
    public DisplayItem? Preview;

    public Photo(string selectedFileName)
    {
        FileName = selectedFileName;
    }

    public static CanvasControl D2dCanvas { get; set; }

    public async Task<bool> LoadPreviewFirstPhoto()
    {
        var continueLoadingHq = true;
        DisplayItem? preview = null;

        async Task GetInitialPreview()
        {
            (preview, continueLoadingHq) =
                await ImageUtil.GetFirstPreviewSpecialHandlingAsync(D2dCanvas, App.SelectedFileName);
        }

        await Task.Run(GetInitialPreview);

        if (continueLoadingHq)
            Preview = preview;
        else
            Hq = preview;
        return continueLoadingHq;
    }

    public async Task LoadHqFirstPhoto()
    {
        async Task GetHqImage()
        {
            Hq = await ImageUtil.GetHqImage(D2dCanvas, App.SelectedFileName);
        }
        await Task.Run(GetHqImage);
    }

    public void LoadHq()
    {
        if (Hq == null || Hq.PreviewFrom == DisplayItem.PreviewSource.ErrorScreen ||
            Hq.PreviewFrom == DisplayItem.PreviewSource.Undefined)
        {
            Hq = ImageUtil.GetHqImage(D2dCanvas, FileName).GetAwaiter().GetResult();
        }
    }

    public void LoadPreview()
    {
        if (Preview == null || Preview.PreviewFrom == DisplayItem.PreviewSource.ErrorScreen ||
            Preview.PreviewFrom == DisplayItem.PreviewSource.Undefined)
        {
            Preview = ImageUtil.GetPreview(D2dCanvas, FileName).GetAwaiter().GetResult();
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
        ".heic", // High Efficiency Image Format (Apple)
        ".heif", // High Efficiency Image Format
        ".avif", // Modern high-compression format
        ".jxl",  // JPEG XL, a newer format that supports transparency
        ".psd"   // Photoshop document
    };

    public bool SupportsTransparency()
    {
        string extension = Path.GetExtension(FileName);
        return !string.IsNullOrEmpty(extension) && FormatsSupportingTransparency.Contains(extension.ToLower());
    }
}