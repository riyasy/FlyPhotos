#nullable enable
using System.Collections.Generic;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.State;

/// <summary>
/// Read-only view of the photo collection: resolve a key to its <see cref="Photo"/>, and read the
/// ordered key snapshot. Implemented by <see cref="PhotoList"/> and consumed by readers that need to
/// see the collection but never mutate it (the thumbnail strip).
/// </summary>
internal interface IPhotoProvider
{
    /// <summary>The photo for <paramref name="key"/>, or null if absent.</summary>
    Photo? GetPhoto(int key);

    /// <summary>The current immutable key snapshot (capture once and index; never mutated in place).</summary>
    IReadOnlyList<int> Keys { get; }
}
