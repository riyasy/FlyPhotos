#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FlyPhotos.Services;

namespace FlyPhotos.Core.Model;

/// <summary>
/// One photo in the list: its file and what its format implies, plus its decoded <see cref="Images"/>.
/// Decoding lives in <c>PhotoDecoder</c>.
/// </summary>
internal sealed class Photo
{
    public string FilePath { get; private set; }

    public PhotoImages Images { get; } = new();

    public bool SupportsTransparency { get; }

    public bool IsVector { get; }

    public bool IsRaw { get; }

    public Photo(string selectedFilePath)
    {
        FilePath = selectedFilePath;
        string extension = Path.GetExtension(FilePath);
        SupportsTransparency = !string.IsNullOrEmpty(extension) && FormatsSupportingTransparency.Contains(extension);
        IsVector = extension.Contains(".svg", StringComparison.OrdinalIgnoreCase);
        IsRaw = !string.IsNullOrEmpty(extension) && CodecDiscovery.Support.Raw(extension);
    }

    // Extension is never changed by a rename (the UI keeps it fixed, and the folder watcher reports an
    // extension change as remove + add), so the format-derived flags above stay valid.
    public void Rename(string newFilePath) => FilePath = newFilePath;

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
