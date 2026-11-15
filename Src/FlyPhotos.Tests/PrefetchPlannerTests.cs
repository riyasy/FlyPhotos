using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.State;

namespace FlyPhotos.Tests;

/// <summary>
/// The prefetch rules (CONTEXT.md): what loads first, what waits, what is held during a burst, and what is
/// unloaded or cancelled when the window moves. Keys here are 100 + position, to keep keys and positions apart.
/// </summary>
[TestClass]
public sealed class PrefetchPlannerTests
{
    private static readonly int[] Keys = Enumerable.Range(0, 20).Select(p => 100 + p).ToArray();
    private static readonly JobLimits Wide = new(Previews: 50, Heavy: 4, Hq: 8, DiskWrites: 8);

    private readonly Dictionary<int, SlotState> _slots = [];
    private readonly List<RunningJob> _running = [];
    private readonly HashSet<int> _loaded = [];

    private PrefetchPlan Plan(int centre, bool holdHq = false, int previewHalf = 3, int hqHalf = 1, JobLimits? limits = null)
    {
        var plan = new PrefetchPlan();
        PrefetchPlanner.Make(plan, new NavState(Keys, centre), _loaded, k => _slots.GetValueOrDefault(k), _running, holdHq,
            previewHalf, hqHalf, limits ?? Wide);
        return plan;
    }

    private static List<int> Started(PrefetchPlan plan, LoadKind kind) =>
        plan.Start.Where(s => s.Kind == kind).Select(s => s.Key).ToList();

    private void Have(int key, SlotState slot)
    {
        _slots[key] = slot;
        if (slot.HasPreview || slot.HasHq) _loaded.Add(key);
    }

    [TestMethod]
    public void AnHqWindowWiderThanThePreviewWindowKeepsTheHqsItLoads()
    {
        // A hand-edited setting: previews 0 per side, HQs 2. A neighbour's HQ must not be unloaded and reloaded forever.
        Have(105, new SlotState(HasPreview: true, HasHq: true));
        Have(106, new SlotState(HasPreview: false, HasHq: true));
        var plan = Plan(centre: 5, previewHalf: 0, hqHalf: 2);
        Assert.AreEqual(0, plan.EvictAll.Count);
        Assert.AreEqual(0, plan.EvictHq.Count);
        Assert.IsFalse(Started(plan, LoadKind.Hq).Contains(106));
    }

    [TestMethod]
    public void TheCentresHqStartsFirstAndAloneMayBeLarge()
    {
        var plan = Plan(centre: 5);
        CollectionAssert.AreEqual(new[] { 105 }, Started(plan, LoadKind.Hq));
        Assert.IsTrue(plan.Start.Single(s => s.Kind == LoadKind.Hq).AllowLarge);
    }

    [TestMethod]
    public void TheCentresHqStartsEvenWhenSlowDecodesLeftBehindFillEverySlot()
    {
        // Two slow decodes of photos the viewer has stepped away from, cancelled but not yet returned.
        _running.Add(new RunningJob(100, LoadKind.Hq));
        _running.Add(new RunningJob(101, LoadKind.Hq));
        var plan = Plan(centre: 5, limits: Wide with { Hq = 2 });
        CollectionAssert.AreEqual(new[] { 105 }, Started(plan, LoadKind.Hq)); // the centre only, no neighbours

        // At twice the limit even the centre waits.
        _running.Add(new RunningJob(102, LoadKind.Hq));
        _running.Add(new RunningJob(108, LoadKind.Hq));
        Assert.AreEqual(0, Started(Plan(centre: 5, limits: Wide with { Hq = 2 }), LoadKind.Hq).Count);
    }

    [TestMethod]
    public void NeighbourHqsWaitWhileTheCentreLoads()
    {
        _running.Add(new RunningJob(105, LoadKind.Hq, true));
        Assert.AreEqual(0, Started(Plan(centre: 5), LoadKind.Hq).Count);
    }

    [TestMethod]
    public void NeighbourHqsStartOnceTheCentreHasItsHqNearestFirstAndNeverLarge()
    {
        Have(105, new SlotState(HasPreview: true, HasHq: true));
        var plan = Plan(centre: 5, hqHalf: 2);
        CollectionAssert.AreEqual(new[] { 104, 106, 103, 107 }, Started(plan, LoadKind.Hq));
        Assert.IsTrue(plan.Start.Where(s => s.Kind == LoadKind.Hq).All(s => !s.AllowLarge));
    }

    [TestMethod]
    public void AFailedCentreDoesNotHoldItsNeighboursBack()
    {
        Have(105, new SlotState(HasPreview: true, HasHq: false, HqParked: true));
        CollectionAssert.AreEqual(new[] { 104, 106 }, Started(Plan(centre: 5), LoadKind.Hq));
    }

    [TestMethod]
    public void ABurstHoldsAllHqButNotPreviews()
    {
        var plan = Plan(centre: 5, holdHq: true);
        Assert.AreEqual(0, Started(plan, LoadKind.Hq).Count);
        Assert.AreEqual(7, Started(plan, LoadKind.PreviewLight).Count);
    }

    [TestMethod]
    public void PreviewsLoadNearestFirstWithinTheBudget()
    {
        var plan = Plan(centre: 5, limits: Wide with { Previews = 3 });
        CollectionAssert.AreEqual(new[] { 105, 104, 106 }, Started(plan, LoadKind.PreviewLight));
    }

    [TestMethod]
    public void RunningPreviewsCountAgainstTheBudget()
    {
        _running.Add(new RunningJob(105, LoadKind.PreviewLight, false));
        _running.Add(new RunningJob(104, LoadKind.PreviewHeavy, false));
        var plan = Plan(centre: 5, limits: Wide with { Previews = 3 });
        CollectionAssert.AreEqual(new[] { 106 }, Started(plan, LoadKind.PreviewLight));
    }

    [TestMethod]
    public void PreviewsNeedingAFullDecodeGoToTheCappedHeavyLane()
    {
        foreach (int key in Keys) Have(key, new SlotState(HasPreview: false, HasHq: false, NeedsHeavy: true));
        var plan = Plan(centre: 5, limits: Wide with { Heavy = 2 });
        CollectionAssert.AreEqual(new[] { 105, 104 }, Started(plan, LoadKind.PreviewHeavy));
        Assert.AreEqual(0, Started(plan, LoadKind.PreviewLight).Count);
    }

    [TestMethod]
    public void DiskWritesWaitUntilNoLightPreviewIsWaiting()
    {
        Have(105, new SlotState(HasPreview: true, HasHq: true, NeedsDiskWrite: true));
        Assert.AreEqual(0, Started(Plan(centre: 5), LoadKind.DiskWrite).Count); // neighbours' previews still to load

        foreach (int key in Keys) Have(key, new SlotState(HasPreview: true, HasHq: true, NeedsDiskWrite: key == 105));
        CollectionAssert.AreEqual(new[] { 105 }, Started(Plan(centre: 5), LoadKind.DiskWrite));
    }

    [TestMethod]
    public void PhotosLeavingTheWindowAreUnloaded()
    {
        Have(100, new SlotState(HasPreview: true, HasHq: false));          // beyond the preview window
        Have(107, new SlotState(HasPreview: true, HasHq: true));           // inside previews, beyond HQ
        Have(106, new SlotState(HasPreview: true, HasHq: true));           // inside both
        var plan = Plan(centre: 5);
        CollectionAssert.AreEqual(new[] { 100 }, plan.EvictAll);
        CollectionAssert.AreEqual(new[] { 107 }, plan.EvictHq);
    }

    [TestMethod]
    public void OnlyTheCentreKeepsATiledImage()
    {
        Have(106, new SlotState(HasPreview: true, HasHq: true, TiledHq: true));
        Have(105, new SlotState(HasPreview: true, HasHq: true, TiledHq: true));
        CollectionAssert.AreEqual(new[] { 106 }, Plan(centre: 5).EvictHq);
    }

    [TestMethod]
    public void JobsLeavingTheWindowAndALargeDecodeLeavingTheCentreAreCancelled()
    {
        _running.Add(new RunningJob(100, LoadKind.PreviewLight, false)); // left the preview window
        _running.Add(new RunningJob(104, LoadKind.Hq, true));            // the old centre's large decode
        _running.Add(new RunningJob(106, LoadKind.Hq, false));           // a neighbour, still in the window
        _running.Add(new RunningJob(100, LoadKind.DiskWrite, false));    // writes always finish
        CollectionAssert.AreEqual(new[] { 0, 1 }, Plan(centre: 5).Cancel);
    }

    [TestMethod]
    public void NothingRunsTwiceAndParkedWorkWaits()
    {
        _running.Add(new RunningJob(104, LoadKind.PreviewLight, false));
        Have(106, new SlotState(HasPreview: false, HasHq: false, PreviewParked: true));
        var started = Started(Plan(centre: 5, previewHalf: 1), LoadKind.PreviewLight);
        CollectionAssert.AreEqual(new[] { 105 }, started);
    }

    [TestMethod]
    public void NoCentreMeansNoPlan()
    {
        var plan = Plan(centre: -1);
        Assert.AreEqual(0, plan.Start.Count + plan.Cancel.Count + plan.EvictAll.Count + plan.EvictHq.Count);
    }
}
