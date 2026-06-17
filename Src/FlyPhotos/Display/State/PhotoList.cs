#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.State;

/// <summary>
/// Owns the photo collection: the <see cref="Photo"/> objects keyed by int, and the immutable
/// sorted-key snapshot used for positional navigation.
/// <para>
/// The concurrency model is preserved verbatim from when these were two fields on
/// <c>PhotoDisplayController</c>:
/// <list type="bullet">
///   <item>the dictionary is a <see cref="ConcurrentDictionary{TKey,TValue}"/> — lock-free reads on
///     ThreadPool threads (the prefetch cache, the thumbnail strip) while the UI thread mutates it on
///     delete;</item>
///   <item>the key list is a <b>volatile copy-on-write snapshot</b> — readers capture the reference
///     and iterate without a lock; mutation publishes a new list, never edits in place.</item>
/// </list>
/// Position is intentionally NOT owned here — it stays in <c>PhotoSessionState</c>.
/// </para>
/// </summary>
internal sealed class PhotoList : IPhotoProvider
{
    private readonly ConcurrentDictionary<int, Photo> _photos = new();
    private volatile IReadOnlyList<int> _keys = Array.Empty<int>();

    /// <summary>
    /// The current immutable key snapshot. Capture once into a local and index it; it is never mutated
    /// in place, so a captured reference stays consistent even if the list is swapped concurrently.
    /// </summary>
    public IReadOnlyList<int> Keys => _keys;

    /// <summary>Number of photos currently in the collection.</summary>
    public int Count => _photos.Count;

    /// <summary>The photo for <paramref name="key"/>, or null if absent. Lock-free.</summary>
    public Photo? GetPhoto(int key) => _photos.TryGetValue(key, out var photo) ? photo : null;

    /// <summary>The photo for <paramref name="key"/>; throws if absent (matches the old indexer use).</summary>
    public Photo this[int key] => _photos[key];

    /// <summary>Whether the collection contains <paramref name="key"/>.</summary>
    public bool Contains(int key) => _photos.ContainsKey(key);

    /// <summary>
    /// Builds the collection from <paramref name="files"/> (keys 0..n-1) and publishes the key snapshot
    /// atomically once fully populated. Mirrors the original startup population loop.
    /// </summary>
    public void Initialize(IReadOnlyList<string> files)
    {
        var keys = new List<int>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            keys.Add(i);
            _photos[i] = new Photo(files[i]);
        }
        _keys = keys; // publish the fully populated list atomically
    }

    /// <summary>Replaces (or adds) the photo at <paramref name="key"/> without touching the snapshot.</summary>
    public void Set(int key, Photo photo) => _photos[key] = photo;

    /// <summary>
    /// Removes the photo at <paramref name="position"/> in the key snapshot: drops it from the
    /// dictionary and publishes a new snapshot with that position removed. Returns the removed photo
    /// (the caller owns disposal), or null if <paramref name="position"/> is out of range.
    /// <para>
    /// ThreadPool readers that captured the old snapshot continue to see a consistent (stale) list and
    /// pick up the new one on their next read of <see cref="Keys"/> — the same copy-on-write contract
    /// the controller relied on.
    /// </para>
    /// </summary>
    public Photo? RemoveAt(int position)
    {
        var current = _keys;
        if (position < 0 || position >= current.Count) return null;
        int key = current[position];
        _photos.TryRemove(key, out var removed);
        var updated = new List<int>(current);
        updated.RemoveAt(position);
        _keys = updated; // publish atomically
        return removed;
    }
}
