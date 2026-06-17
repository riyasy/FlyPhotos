#nullable enable
using System;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;

namespace FlyPhotos.Display.Controllers;

internal interface ICanvasController : IDisposable
{
    Task SetSource(Photo photo, DisplayLevel displayLevel);
    Task WaitForPanZoomAnimationAsync(int timeoutMs);
}

internal interface IThumbnailController : IDisposable
{
    void CreateThumbnailRibbonOffScreen();
    void RedrawThumbNailsIfNeeded(int key);
    void SetPhotoProvider(IPhotoProvider provider);
    void SetPreviewLoadedProbe(Func<int, bool> isPreviewLoaded);
}