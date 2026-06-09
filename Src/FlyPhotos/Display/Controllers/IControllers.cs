using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;

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
    void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews);
    void SetSortedPhotoKeysProvider(Func<IReadOnlyList<int>> provider);
}