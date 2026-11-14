#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FlyPhotos.Services;

namespace FlyPhotos.Core.Model;

/// <summary>
/// One photo in the list: its file and what its format implies, plus its decoded <see cref="Slots"/>.
/// Decoding lives in <c>PhotoDecoder</c>. The list knows a photo by an int key (see <c>PhotoList</c>); the canvas
/// knows it by <see cref="FilePath"/>.
/// </summary>
internal sealed class Photo
{
    /// <summary>The file on disk. Changes only through <see cref="Rename"/>.</summary>
    public string FilePath { get; private set; }

    /// <summary>The decoded preview, HQ and thumbnail; filled and emptied by the prefetch cache.</summary>
    public DisplaySlots Slots { get; } = new();

    /// <summary>The format can carry alpha, so the canvas draws the checkered background behind it.</summary>
    public bool SupportsTransparency { get; }

    /// <summary>An SVG: drawn as vectors, and has no pixel size to show in the filename bar.</summary>
    public bool IsVector { get; }

    /// <summary>A camera RAW file: its HQ is reloaded when the RAW decode settings change.</summary>
    public bool IsRaw { get; }

    /// <summary>Derives the format flags from the extension of <paramref name="selectedFilePath"/>, once.</summary>
    public Photo(string selectedFilePath)
    {
        FilePath = selectedFilePath;
        string extension = Path.GetExtension(FilePath);
        SupportsTransparency = !string.IsNullOrEmpty(extension) && FormatsSupportingTransparency.Contains(extension);
        IsVector = extension.Contains(".svg", StringComparison.OrdinalIgnoreCase);
        IsRaw = !string.IsNullOrEmpty(extension) && CodecDiscovery.Support.Raw(extension);
    }

    /// <summary>
    /// The file was renamed: the photo keeps its key and its loaded images. UI thread (the one that changes the list).
    /// </summary>
    /// <remarks>
    /// The extension is never changed by a rename (the UI keeps it fixed, and the folder watcher reports an
    /// extension change as remove + add), so the format-derived flags above stay valid.
    /// </remarks>
    public void Rename(string newFilePath) => FilePath = newFilePath;

    /// <summary>The extensions behind <see cref="SupportsTransparency"/>.</summary>
    private static readonly HashSet<string> FormatsSupportingTransparency =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".gif",
        ".webp",
        ".tiff",
        ".tif",
        ".svg",
        ".apng",
        ".ico",
        ".jxl",
        ".psd"
    };
}
