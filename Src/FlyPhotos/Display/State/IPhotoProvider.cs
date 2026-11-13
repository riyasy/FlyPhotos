#nullable enable
using System.Collections.Generic;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.State;

/// <summary>
/// Read-only view of the photo collection for readers on other threads (the thumbnail strip): resolve a key to
/// its <see cref="Photo"/>, and read where the viewer is.
/// </summary>
internal interface IPhotoProvider
{
    /// <summary>The photo for <paramref name="key"/>, or null if absent.</summary>
    Photo? GetPhoto(int key);

    /// <summary>Where the viewer is: the key snapshot and the position on screen, one consistent pair.</summary>
    NavState Nav { get; }
}
