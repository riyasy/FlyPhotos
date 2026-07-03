#nullable enable
using System;
using System.Collections.Generic;

namespace FlyPhotos.Core.Model;

public readonly record struct ExifField(string Label, string Value);

public sealed record ExifFieldGroup(string Category, IReadOnlyList<ExifField> Fields);

public sealed record ExifData(IReadOnlyList<ExifField> Summary, Lazy<IReadOnlyList<ExifFieldGroup>> All)
{
    public static readonly ExifData Empty = new([], new Lazy<IReadOnlyList<ExifFieldGroup>>(() => []));
}
