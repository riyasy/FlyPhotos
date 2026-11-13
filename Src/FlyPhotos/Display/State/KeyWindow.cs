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
    /// The key nearest to <paramref name="centre"/>, within <paramref name="half"/>, that <paramref name="wanted"/>
    /// accepts; null when none does. At equal distance the one before the centre wins, the order the window
    /// sync queues keys in.
    /// </summary>
    public static int? Nearest(IReadOnlyList<int> keys, int centre, int half, Func<int, bool> wanted)
    {
        if (centre < 0 || centre >= keys.Count) return null;
        for (int rank = 0; rank <= 2 * half; rank++)
            if (NthNearest(keys.Count, centre, rank) is var pos and >= 0 && wanted(keys[pos]))
                return keys[pos];
        return null;
    }

    /// <summary>
    /// The position <paramref name="rank"/> steps out from <paramref name="centre"/> in nearest-first order — rank 0
    /// is the centre, then before and after at distance 1, 2, … (before wins a tie) — or -1 when it lies outside a list
    /// of <paramref name="count"/>. A window of half-width h covers ranks 0..2h.
    /// </summary>
    public static int NthNearest(int count, int centre, int rank)
    {
        int distance = (rank + 1) / 2;
        int pos = rank == 0 ? centre : rank % 2 == 1 ? centre - distance : centre + distance;
        return pos >= 0 && pos < count ? pos : -1;
    }
}
