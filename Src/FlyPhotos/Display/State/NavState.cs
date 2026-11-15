#nullable enable
using System;
using System.Collections.Generic;

namespace FlyPhotos.Display.State;

/// <summary>
/// Where the viewer is: a photo list's key snapshot and the position of the photo on screen in it. Immutable and
/// published as one reference, so a reader on any thread always gets a position that belongs to its list.
/// </summary>
internal sealed record NavState(IReadOnlyList<int> Keys, int Position)
{
    /// <summary>Before the list is known (the first photo is loading): nothing to navigate.</summary>
    public static readonly NavState None = new(Array.Empty<int>(), -1);

    /// <summary>The key of the photo on screen, or -1 for <see cref="None"/>.</summary>
    public int Key => Position >= 0 ? Keys[Position] : -1;
}
