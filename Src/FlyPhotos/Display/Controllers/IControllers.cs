#nullable enable
using System;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;

namespace FlyPhotos.Display.Controllers;

/// <summary>The main canvas, as <see cref="PhotoDisplayController"/> drives it. UI thread. Tests use a recording fake.</summary>
internal interface ICanvasController : IDisposable
{
    /// <summary>
    /// Shows <paramref name="item"/>, the photo's image at <paramref name="displayLevel"/>. The caller has leased the
    /// item; the canvas owns that lease from here and releases it when the next item replaces it, or when this
    /// throws (nothing was installed).
    /// </summary>
    Task SetSource(Photo photo, DisplayItem? item, DisplayLevel displayLevel);

    /// <summary>Completes when the next pan/zoom animation finishes, or after <paramref name="timeoutMs"/>.</summary>
    Task WaitForPanZoomAnimationAsync(int timeoutMs);

    /// <summary>The page on screen of a multi-page photo, or null. Read on any thread.</summary>
    MultiPagePosition? CurrentPage { get; }

    /// <summary>A photo was renamed: the canvas knows photos by path, so it follows the new one.</summary>
    void OnPhotoRenamed(string oldPath, string newPath);
}

/// <summary>The thumbnail strip, as <see cref="PhotoDisplayController"/> drives it.</summary>
internal interface IThumbnailController : IDisposable
{
    /// <summary>Rebuilds the whole strip (on its render thread): the list or position changed. Any thread.</summary>
    void RequestThumbnailRedraw();

    /// <summary>A thumbnail became available for <paramref name="key"/>; redraws only if it is in view. Any thread.</summary>
    void RedrawThumbNailsIfNeeded(int key);

    /// <summary>Where the strip reads photos and the viewer's position from. Both are lock-free and read on any thread.</summary>
    void SetPhotoSource(Func<int, Photo?> getPhoto, Func<NavState> getNav);

    /// <summary>Kicks the strip and springs it back: navigation hit the end of the list in <paramref name="direction"/>.</summary>
    void BounceAtEdge(NavDirection direction);
}
