using FlyPhotos.Data;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FlyPhotos.Controllers
{
    internal interface IPhotoDisplayController : IDisposable
    {
        event EventHandler<StatusUpdateEventArgs> StatusUpdated;
        Task LoadFirstPhoto();
        Task CopyFileToClipboardAsync();
        Task Fly(NavDirection direction);
        Task Brake();
        Task FlyBy(int shiftBy);
        bool IsSinglePhoto();
        string GetFullPathCurrentFile();
    }

    internal interface ICanvasController : IAsyncDisposable
    {
        Task SetSource(Photo photo, DisplayLevel displayLevel);
        void SetHundredPercent(bool animateChange);
        void ZoomOutOnExit(double exitAnimationDuration);
        void ZoomByKeyboard(ZoomDirection zoomDirection);
        void PanByKeyboard(double dx, double dy);
        void RotateCurrentPhotoBy90(bool clockWise);
        bool IsPressedOnImage(Point position);
    }

    internal interface IThumbnailController : IDisposable
    {
        void CreateThumbnailRibbonOffScreen();
        void RedrawThumbNailsIfNeeded(int index);
        void SetPreviewCacheReference(ConcurrentDictionary<int, Photo> cachedPreviews);
        void ShowThumbnailBasedOnSettings();
        event Action<int> ThumbnailClicked;
    }
}
