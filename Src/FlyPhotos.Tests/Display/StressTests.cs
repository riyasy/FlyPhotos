using System.Diagnostics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// Load, stress and random-session runs over the whole pipeline. The random sessions are stepped and seeded, so a
/// failure names a seed that replays it exactly; the stress runs use the real prefetch thread and thread-pool decodes.
/// </summary>
[TestClass]
public sealed class StressTests
{
    public TestContext TestContext { get; set; } = null!;

    // --- Random sessions (deterministic) ---

    [TestMethod]
    public void Random_sessions_keep_every_invariant()
    {
        for (int seed = 1; seed <= 40; seed++)
        {
            try { RunSession(seed, steps: 250); }
            catch (Exception ex) { throw new AssertFailedException($"seed {seed}: {ex.Message}", ex); }
        }
    }

    private static void RunSession(int seed, int steps)
    {
        var rng = new Random(seed);
        int photos = rng.Next(1, 50);
        using var h = new DisplayHarness(photos, openAt: rng.Next(photos), previewHalf: rng.Next(0, 6), hqHalf: rng.Next(0, 3),
            limits: new JobLimits(rng.Next(1, 4), rng.Next(1, 3), rng.Next(1, 3), rng.Next(1, 3)));
        h.Open();
        h.CheckInvariants();
        h.Decoder.Script = _ => Reply.Pending;
        int added = 0, installs = 0;

        for (int step = 0; step < steps; step++)
        {
            switch (rng.Next(17))
            {
                case 0 or 1 or 2: h.Next(); break;
                case 3 or 4: h.Prev(); break;
                case 5: h.Brake(); break;
                case 6: h.Run(() => h.Controller.FlyBy(rng.Next(-20, 21))); break;
                case 7: h.Run(rng.Next(2) == 0 ? h.Controller.FlyToFirst : h.Controller.FlyToLast); break;
                case 8 or 9 or 10 or 11:
                    for (int n = rng.Next(1, 6); n > 0; n--)
                    {
                        var pending = h.Decoder.Pending;
                        if (pending.Count == 0) break;
                        var d = pending[rng.Next(pending.Count)];
                        d.Complete(RandomReply(rng, d));
                    }
                    break;
                case 12:
                    if (h.Nav.Keys.Count > 1)
                    {
                        var paths = Enumerable.Range(0, rng.Next(1, 4))
                            .Select(_ => h.PhotoAt(rng.Next(h.Nav.Keys.Count)).FilePath).ToArray();
                        h.Remove(paths);
                    }
                    break;
                case 13:
                    h.Add([.. Enumerable.Range(0, rng.Next(1, 3))
                        .Select(_ => Path.Combine(h.PhotoFolder, $"img_{rng.Next(photos + 5):D5}x{added++:D4}.jpg"))
                        .Order(StringComparer.OrdinalIgnoreCase)]);
                    break;
                case 14:
                    if (rng.Next(3) == 0) h.Cache.HoldHqFor(500);
                    h.Advance(rng.Next(0, 600));
                    break;
                case 15:
                    AppConfig.Settings.CacheSizeOneSidePreviews = rng.Next(0, 6);
                    AppConfig.Settings.CacheSizeOneSideHqImages = rng.Next(0, 3);
                    h.Controller.CacheSizeChanged();
                    break;
                case 16:
                    h.Canvas.Fails = rng.Next(3) == 0 ? item => !item.IsErrorOrUndefined() && ++installs % 4 == 0 : null;
                    break;
            }
            if (rng.Next(4) == 0)
            {
                h.StepCache(); // leave it half-way: the next action lands mid-flight
                continue;
            }
            h.Settle();
            h.CheckInvariants();
        }

        // Let everything finish: the photo the session ends on must reach HQ.
        h.Canvas.Fails = null;
        h.Decoder.Script = _ => Reply.Item;
        h.Brake();
        h.Advance(1000); // past any folder-switch quiet period
        for (int round = 0; round < 200 && (h.Decoder.Pending.Count > 0 || h.Cache.HasMessages); round++)
        {
            h.Decoder.CompleteAll();
            h.Settle();
        }
        h.Settle();
        if (h.Nav.Keys.Count > 1)
        {
            h.Run(() => h.Controller.FlyBy(0)); // re-show: a canvas failure leaves the placeholder until then
            h.Settle();
            Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel, "the photo the session ends on reaches HQ");
        }
        h.CheckInvariants();
    }

    private static Reply RandomReply(Random rng, Decode d)
    {
        int roll = rng.Next(100);
        if (roll < 70) return Reply.Item;
        if (roll < 80) return d.Kind == DecodeKind.Hq && d.AllowLarge ? Reply.Item : Reply.Nothing;
        if (roll < 90) return Reply.Throw;
        if (roll < 95) return Reply.Error;
        return d.Kind == DecodeKind.Hq && d.AllowLarge ? Reply.Tiled : Reply.Item;
    }

    // --- Load ---

    [TestMethod]
    public void A_ten_thousand_photo_folder_opens_navigates_and_stays_within_its_windows()
    {
        var sw = Stopwatch.StartNew();
        using var h = new DisplayHarness(photos: 10_000, openAt: 5000, previewHalf: 200, hqHalf: 2);
        TestContext.WriteLine($"made 10,000 files in {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        h.Open();
        TestContext.WriteLine($"opened and filled a 401-photo window in {sw.ElapsedMilliseconds} ms");
        Assert.AreEqual(10_000, h.Nav.Keys.Count);
        Assert.AreEqual(5000, h.Nav.Position);
        h.CheckInvariants();

        h.Run(h.Controller.FlyToLast);
        h.Settle();
        h.CheckInvariants();
        h.Run(h.Controller.FlyToFirst);
        h.Settle();
        h.CheckInvariants();

        var presses = new List<double>();
        for (int i = 0; i < 500; i++)
        {
            var press = Stopwatch.StartNew();
            h.Next();
            presses.Add(press.Elapsed.TotalMilliseconds);
            if (i % 25 == 0) h.Settle();
        }
        h.Brake();
        h.Settle();
        h.CheckInvariants();
        presses.Sort();
        TestContext.WriteLine($"key press: median {presses[presses.Count / 2]:F3} ms, p99 {presses[(int)(presses.Count * 0.99)]:F3} ms");
        Assert.IsTrue(presses[(int)(presses.Count * 0.99)] < 50, "a key press never waits on loading");
        int alive = h.Decoder.Created.Count(i => !i.IsFreed);
        Assert.IsTrue(alive <= 401 + 5 + 1, $"{alive} images alive: no more than the windows hold");
    }

    // --- Stress (real threads) ---

    [TestMethod]
    public void Holding_a_key_through_thousands_of_photos_on_live_threads_keeps_every_rule()
    {
        using var h = new DisplayHarness(photos: 3000, openAt: 0, previewHalf: 50, hqHalf: 2, ownThread: true,
            limits: JobLimits.ForCores(Environment.ProcessorCount));
        h.Open();
        h.Decoder.Script = _ => Reply.Async;

        using var stop = new CancellationTokenSource();
        int stripErrors = 0;
        var strip = Task.Run(() =>
        {
            // The thumbnail strip reads the list and the images on its own thread the whole time.
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var nav = h.Strip.GetNav!();
                    for (int i = Math.Max(0, nav.Position - 10); i < Math.Min(nav.Keys.Count, nav.Position + 10); i++)
                    {
                        var photo = h.Strip.GetPhoto!(nav.Keys[i]);
                        _ = photo?.Slots.Thumbnail;
                        _ = photo?.Slots.GetActualSize();
                    }
                }
                catch (Exception) { Interlocked.Increment(ref stripErrors); }
            }
        });

        var presses = new List<double>();
        for (int i = 0; i < 2000; i++)
        {
            var press = Stopwatch.StartNew();
            h.Next();
            presses.Add(press.Elapsed.TotalMilliseconds);
            Assert.AreSame(h.Current, h.Canvas.HeldPhoto, $"press {i} shows the photo it moved to");
            if (i % 5 == 0) h.Ui.RunPending();
        }
        h.Brake();
        h.RunUntil(() => h.ShownLevel == DisplayLevel.Hq, "the HQ of the photo the key was released on");
        stop.Cancel();
        strip.Wait();
        h.RunUntil(() => h.Decoder.InFlight == 0, "the window to finish loading");
        h.Ui.RunPending();

        presses.Sort();
        TestContext.WriteLine($"key press: median {presses[presses.Count / 2]:F3} ms, p99 {presses[(int)(presses.Count * 0.99)]:F3} ms");
        Assert.AreEqual(0, stripErrors);
        Assert.IsTrue(presses[(int)(presses.Count * 0.99)] < 50, "a key press never waits on loading");
        h.CheckInvariants(expectSettled: false);
    }

    [TestMethod]
    public void A_folder_changing_on_disk_while_the_viewer_moves_stays_consistent()
    {
        using var h = new DisplayHarness(photos: 60, openAt: 30, source: ListSource.Folder, ownThread: true, previewHalf: 10,
            limits: new JobLimits(3, 2, 2, 2));
        h.Open();
        h.Decoder.Script = _ => Reply.Async;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                File.WriteAllBytes(Path.Combine(h.PhotoFolder, $"img_{i * 2:D5}n.jpg"), []);
                if (i % 2 == 0) File.Delete(h.PathOf(i));
                Thread.Sleep(5);
            }
        });
        var rng = new Random(5);
        while (!writer.IsCompleted)
        {
            if (rng.Next(3) == 0) h.Prev(); else h.Next();
            if (rng.Next(4) == 0) h.Brake();
            h.Ui.RunPending();
            Assert.AreSame(h.Current, h.Canvas.HeldPhoto);
            Thread.Sleep(1);
        }
        writer.Wait();
        h.Brake();
        h.RunUntil(() => h.Nav.Keys.Count == 60 + 40 - 20, "the watcher to report every change");
        h.RunUntil(() => h.Decoder.InFlight == 0 && h.ShownLevel == DisplayLevel.Hq, "loading to finish");
        Assert.IsTrue(h.Nav.Keys.Select(k => h.Controller.Photos.GetPhoto(k)!.FilePath).All(File.Exists),
            "the list matches the folder");
        h.CheckInvariants(expectSettled: false);
    }
}
