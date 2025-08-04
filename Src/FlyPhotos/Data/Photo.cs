#nullable enable
using System.Threading.Tasks;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI.Xaml;
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
}