#nullable enable
using System;
using System.Collections.Generic;

namespace FlyPhotos.Display.State;

/// <summary>
/// Lookups in a position-ordered key snapshot (<see cref="PhotoList.Keys"/>) around a centre position. Keys are
/// identities, not positions, and are not ordered, so a key is found by scanning a bounded span of positions.
/// </summary>
internal static class KeyWindow
{
    /// <summary>The positions within <paramref name="half"/> of <paramref name="centre"/>, clamped to the list.</summary>
    public static (int Lo, int Hi) Range(int count, int centre, int half) =>
        (Math.Max(0, centre - half), Math.Min(count - 1, centre + half));

    /// <summary>The position of <paramref name="key"/> within <paramref name="half"/> of <paramref name="centre"/>, or -1.</summary>
    public static int IndexOf(IReadOnlyList<int> keys, int key, int centre, int half)
    {
        var (lo, hi) = Range(keys.Count, centre, half);
        for (int pos = lo; pos <= hi; pos++)
            if (keys[pos] == key) return pos;
        return -1;
    }

    /// <summary>
    /// The positions within <paramref name="half"/> of <paramref name="centre"/> in a list of
    /// <paramref name="count"/>, nearest first: the centre, then before and after at distance 1, 2, … (at equal
    /// distance the one before the centre wins). Positions outside the list are skipped.
    /// </summary>
    public static IEnumerable<int> NearestFirst(int count, int centre, int half)
    {
        if (centre < 0 || centre >= count) yield break;
        for (int rank = 0; rank <= 2 * half; rank++)
            if (NthNearest(count, centre, rank) is var pos and >= 0)
                yield return pos;
    }

    // The position rank steps out from centre in nearest-first order, or -1 outside the list.
    private static int NthNearest(int count, int centre, int rank)
    {
        int distance = (rank + 1) / 2;
        int pos = rank == 0 ? centre : rank % 2 == 1 ? centre - distance : centre + distance;
        return pos >= 0 && pos < count ? pos : -1;
    }
}
