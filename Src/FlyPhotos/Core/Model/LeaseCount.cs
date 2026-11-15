using System.Diagnostics;
using System.Threading;

namespace FlyPhotos.Core.Model;

/// <summary>
/// A thread-safe count of leases on something that must be freed exactly once, after its last holder lets go.
/// Starts with one lease (the creator's). Keep it in a mutable field and call it in place — never copy it.
/// </summary>
internal struct LeaseCount()
{
    // Leases still held. Only changed with Interlocked; 0 means freed, and once 0 it never rises again (TryAcquire
    // refuses), so a freed thing can't be brought back to life by a late reader.
    private int _count = 1;

    /// <summary>Takes a lease. False once the count has reached zero: the thing is freed and must not be touched.</summary>
    public bool TryAcquire()
    {
        int seen = Volatile.Read(ref _count);
        while (seen > 0)
        {
            int was = Interlocked.CompareExchange(ref _count, seen + 1, seen);
            if (was == seen) return true;
            seen = was;
        }
        return false;
    }

    /// <summary>Whether the last lease has ended (the thing is freed). A snapshot: only final once true.</summary>
    public readonly bool IsReleased => Volatile.Read(in _count) <= 0;

    /// <summary>Ends a lease. True for the last one: the caller frees the thing.</summary>
    public bool Release()
    {
        int left = Interlocked.Decrement(ref _count);
        Debug.Assert(left >= 0, "released more often than leased");
        return left == 0;
    }
}
