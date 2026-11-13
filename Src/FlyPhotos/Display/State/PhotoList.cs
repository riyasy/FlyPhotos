#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.State;

/// <summary>
/// Owns the photo collection: the <see cref="Photo"/> objects keyed by int, and the immutable
/// position-ordered key snapshot used for navigation. Keys are stable identities, NOT positions: Folder
/// mode inserts new keys anywhere, so key values say nothing about order — never compare or binary-search them.
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
/// Position is not owned here: see <see cref="NavState"/>.
/// </para>
/// </summary>
internal sealed class PhotoList
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
        _nextKey = files.Count;
        _keys = keys; // publish the fully populated list atomically
    }

    /// <summary>
    /// Builds the full listing to replace the startup list (the opened photo alone), without publishing it: the
    /// photo at <paramref name="keepKey"/> — already loading or on screen — takes <paramref name="keepPosition"/>
    /// and keeps its key; every other file gets a new photo, added under a key no snapshot holds yet. Returns the
    /// key list for <see cref="Publish"/>. Call once, before any other change to the list.
    /// </summary>
    public IReadOnlyList<int> PrepareListing(IReadOnlyList<string> files, int keepPosition, int keepKey)
    {
        var keys = new List<int>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            if (i == keepPosition)
            {
                keys.Add(keepKey);
                continue;
            }
            int key = _nextKey++;
            _photos[key] = new Photo(files[i]);
            keys.Add(key);
        }
        return keys;
    }

    /// <summary>Publishes a key list from <see cref="PrepareListing"/>, atomically. The thread that owns the list.</summary>
    public void Publish(IReadOnlyList<int> keys) => _keys = keys;

    // Keys are never reused, so a cache entry or in-flight load for a removed key can never be
    // mistaken for a newer photo. Only the thread that mutates the list touches this.
    private int _nextKey;

    /// <summary>
    /// Applies one change batch by the rules of <see cref="ListChange.Apply{T}"/> (removals, renames that keep
    /// the photo and key, sorted additions; the photo at <paramref name="keepKey"/> survives a batch that would
    /// empty the list). New photos enter the dictionary before the new snapshot is published. Removed photos
    /// stay in the dictionary until the caller calls <see cref="Forget"/>, so it can still decide where to
    /// navigate. Returns the removed (key, photo) pairs. Call from the one thread that mutates the list.
    /// <paramref name="onRenamed"/> gets each applied rename as (the photo's exact old path, its new path).
    /// </summary>
    public List<(int Key, Photo Photo)> ApplyChanges(ListChangeBatch batch, int keepKey,
        Action<string, string>? onRenamed = null)
    {
        var result = ListChange.Apply(_keys, key => _photos[key].FilePath, batch.IsRemoved, batch.Renamed, batch.Added,
            batch.Order,
            create: path =>
            {
                int key = _nextKey++;
                _photos[key] = new Photo(path);
                return key;
            },
            rename: (key, path) =>
            {
                var photo = _photos[key];
                var oldPath = photo.FilePath;
                photo.Rename(path);
                onRenamed?.Invoke(oldPath, path);
            },
            isKeep: key => key == keepKey);

        if (result.Changed) _keys = result.Items; // publish atomically
        return result.Removed.ConvertAll(key => (key, _photos[key]));
    }

    /// <summary>Drops photos returned by <see cref="ApplyChanges"/> from the dictionary. The caller owns disposal.</summary>
    public void Forget(List<(int Key, Photo Photo)> removed)
    {
        foreach (var (key, _) in removed)
            _photos.TryRemove(key, out _);
    }
}
