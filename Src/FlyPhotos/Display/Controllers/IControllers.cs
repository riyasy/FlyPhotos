#nullable enable
using System;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;

namespace FlyPhotos.Display.Controllers;

internal interface ICanvasController : IDisposable
{
    Task SetSource(Photo photo, DisplayItem? item, DisplayLevel displayLevel);
    Task WaitForPanZoomAnimationAsync(int timeoutMs);
    MultiPagePosition? CurrentPage { get; }
    void OnPhotoRenamed(string oldPath, string newPath);
}

internal interface IThumbnailController : IDisposable
{
    void RequestThumbnailRedraw();
    void RedrawThumbNailsIfNeeded(int key);
    /// <summary>Where the strip reads photos and the viewer's position from; both are read on the W2D thread.</summary>
    void SetPhotoSource(Func<int, Photo?> getPhoto, Func<NavState> getNav);
    void BounceAtEdge(NavDirection direction);
}