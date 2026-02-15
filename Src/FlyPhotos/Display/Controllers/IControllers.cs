using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.Controllers
{
    internal interface ICanvasController : IAsyncDisposable
    {
        Task SetSource(Photo photo, DisplayLevel displayLevel);
    }

    internal interface IThumbnailController : IDisposable
    {
        void CreateThumbnailRibbonOffScreen();
        void RedrawThumbNailsIfNeeded(int key);
        void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews);
        void SetSortedPhotoKeysReference(List<int> sortedPhotoKeys);
    }
}
