using FlyPhotos.Core.Model;

namespace FlyPhotos.Tests;

/// <summary>
/// The lease count that decides when a photo's bitmap (DisplayItem) is freed: never while the slot, the canvas or
/// a reader still holds it, exactly once after the last of them lets go, in whatever order they do.
/// </summary>
[TestClass]
public sealed class LeaseCountTests
{
    [TestMethod]
    public void TheCreatorsReleaseIsTheLast()
    {
        var leases = new LeaseCount();
        Assert.IsTrue(leases.Release());
    }

    [TestMethod]
    public void TheSlotLettingGoFirstKeepsItForTheCanvas()
    {
        var leases = new LeaseCount();
        Assert.IsTrue(leases.TryAcquire());  // the canvas
        Assert.IsFalse(leases.Release());    // the slot (window move, retire)
        Assert.IsTrue(leases.Release());     // the renderer swap frees it
    }

    [TestMethod]
    public void AFreedThingCannotBeLeasedAgain()
    {
        var leases = new LeaseCount();
        Assert.IsTrue(leases.Release());
        Assert.IsFalse(leases.TryAcquire());
    }

    [TestMethod]
    public void RacingLeasesEndInExactlyOneLastRelease()
    {
        for (int round = 0; round < 500; round++)
        {
            var box = new Box();
            int lasts = 0;
            Parallel.For(0, 8, i =>
            {
                if (i == 0)
                {
                    if (box.Leases.Release()) Interlocked.Increment(ref lasts); // the creator's
                    return;
                }
                if (!box.Leases.TryAcquire()) return;
                if (box.Leases.Release()) Interlocked.Increment(ref lasts);
            });
            Assert.AreEqual(1, lasts);
            Assert.IsFalse(box.Leases.TryAcquire());
        }
    }

    private sealed class Box { public LeaseCount Leases = new(); }
}
