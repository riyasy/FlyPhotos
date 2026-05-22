using System.Collections.Generic;

namespace FlyPhotos.Infra.Utils;

internal static class CollectionExtensions
{
    /// <summary>
    /// Binary search on <see cref="IReadOnlyList{T}"/>, replicating <c>List&lt;T&gt;.BinarySearch</c> semantics.
    /// Returns the index of <paramref name="value"/> if found, or the bitwise complement of the
    /// insertion point if not found — identical contract to <c>List&lt;int&gt;.BinarySearch</c>.
    /// The list must be sorted in ascending order.
    /// </summary>
    internal static int BinarySearch(this IReadOnlyList<int> list, int value)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = list[mid].CompareTo(value);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }
}
