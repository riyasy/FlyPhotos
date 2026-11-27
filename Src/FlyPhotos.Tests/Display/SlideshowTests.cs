using System.Diagnostics;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Tests.Display.Harness;
using FlyPhotos.UI.Behaviors;

namespace FlyPhotos.Tests.Display;

/// <summary>
/// The slideshow (#182): its steps through the real controller, the whole-loop timing of
/// animations, and the timer's states with a hand-moved clock.
/// </summary>
[TestClass]
public sealed class SlideshowTests
{
    private static bool Step(DisplayHarness h, bool loop = true)
    {
        AppConfig.Settings.SlideshowLoop = loop;
        bool moved = false;
        h.Run(async () => moved = await h.Controller.SlideshowNext());
        h.Settle();
        return moved;
    }

    private static int Count(DisplayHarness h, string step) => h.Canvas.Steps.Count(s => s == step);

    [TestMethod]
    public void Slideshow_steps_crossfade_and_manual_ones_do_not()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 0).Open();
        Step(h);
        var steps = h.Canvas.Steps;
        Assert.AreEqual(1, Count(h, "fade"), "once per step, not per upgrade");
        Assert.AreEqual("img_00001.jpg", steps[steps.IndexOf("fade") + 1], "asked for just before the step's install");
        h.Next();
        Assert.AreEqual(1, Count(h, "fade"));
    }

    [TestMethod]
    public void With_every_page_shown_a_multi_page_photo_steps_through_its_pages_first()
    {
        using var h = new DisplayHarness(photos: 3, openAt: 0).Open();
        AppConfig.Settings.SlideshowAllPages = true;
        h.Canvas.CurrentPage = new MultiPagePosition(h.Current.FilePath, 0, 3, 10, 10);

        Assert.IsTrue(Step(h));
        Assert.AreEqual("img_00000.jpg", h.CurrentName, "a page step, not a photo step");
        CollectionAssert.AreEqual(new[] { "fade", "page Next" }, h.Canvas.Steps.TakeLast(2).ToArray());

        h.Canvas.CurrentPage = h.Canvas.CurrentPage with { Index = 2 }; // the last page
        Step(h);
        Assert.AreEqual("img_00001.jpg", h.CurrentName);
        Assert.AreEqual(1, Count(h, "page Next"));
    }

    [TestMethod]
    public void A_page_step_by_hand_is_reported_and_not_crossfaded()
    {
        using var h = new DisplayHarness(photos: 3).Open();
        int manual = 0;
        h.Controller.UserNavigated += () => manual++;
        h.Controller.ChangePage(NavDirection.Prev);
        Assert.AreEqual(1, manual);
        Assert.AreEqual(1, Count(h, "page Prev"));
        Assert.AreEqual(0, Count(h, "fade"));
    }

    // --- Steps through the controller ---

    [TestMethod]
    public void Steps_go_through_the_list_and_loop_back_to_the_first()
    {
        using var h = new DisplayHarness(photos: 3, openAt: 1).Open();

        Assert.IsTrue(Step(h));
        Assert.AreEqual("img_00002.jpg", h.CurrentName);
        Assert.IsTrue(Step(h));
        Assert.AreEqual("img_00000.jpg", h.CurrentName, "loops");
        Assert.AreEqual(0, h.Strip.Bounces.Count, "a slideshow step never bounces");
        h.CheckInvariants();
    }

    [TestMethod]
    public void Without_loop_the_last_photo_ends_the_show_and_stays_up()
    {
        using var h = new DisplayHarness(photos: 3, openAt: 2).Open();

        Assert.IsFalse(Step(h, loop: false));
        Assert.AreEqual("img_00002.jpg", h.CurrentName);
        h.CheckInvariants();
    }

    [TestMethod]
    public void A_single_photo_has_no_show()
    {
        using var h = new DisplayHarness(photos: 1).Open();
        Assert.IsFalse(Step(h));
    }

    [TestMethod]
    public void Hq_shown_is_raised_once_the_hq_is_up()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 0).Open();
        int shown = 0;
        h.Controller.HqShown += () => shown++;

        Step(h);

        Assert.AreEqual(DisplayLevel.Hq, h.ShownLevel);
        Assert.IsTrue(shown >= 1, "the next photo's HQ reported");
    }

    [TestMethod]
    public void Manual_navigation_is_reported_but_slideshow_steps_are_not()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 0).Open();
        int manual = 0;
        h.Controller.UserNavigated += () => manual++;

        Step(h);
        Assert.AreEqual(0, manual);
        h.Next();
        h.Run(() => h.Controller.FlyBy(1));
        Assert.AreEqual(2, manual);
    }

    [TestMethod]
    public void A_page_that_fails_to_load_is_not_retried()
    {
        using var h = new DisplayHarness(photos: 3, openAt: 0).Open();
        AppConfig.Settings.SlideshowAllPages = true;
        h.Canvas.CurrentPage = new MultiPagePosition(h.Current.FilePath, 0, 3, 10, 10);
        Step(h); // asks for page 1, which never lands
        Step(h);
        Assert.AreEqual("img_00001.jpg", h.CurrentName);
        Assert.AreEqual(1, Count(h, "page Next"));
    }

    [TestMethod]
    public void Photo_changing_is_raised_for_any_new_photo_on_screen()
    {
        using var h = new DisplayHarness(photos: 5, openAt: 2).Open();
        h.Controller.DeleteFile = _ => Task.FromResult(new DeleteResult(true, false));
        int changes = 0;
        h.Controller.PhotoChanging += () => changes++;
        Step(h);
        h.Run(async () => await h.Controller.DeleteCurrentPhoto());
        Assert.AreEqual(2, changes, "a step and a delete");
    }

    // --- Whole animation loops ---

    private static TimeSpan Wait(double loopSeconds, double delaySeconds, double elapsedSeconds = 0)
    {
        var now = Stopwatch.GetTimestamp();
        var started = now - (long)(elapsedSeconds * Stopwatch.Frequency);
        return new AnimationLoop(TimeSpan.FromSeconds(loopSeconds), started)
            .SlideshowWait(TimeSpan.FromSeconds(delaySeconds), now);
    }

    private static void AreClose(double expectedSeconds, TimeSpan actual) =>
        Assert.AreEqual(expectedSeconds, actual.TotalSeconds, 0.001, $"waited {actual.TotalSeconds:F3}s");

    [TestMethod]
    public void An_animation_plays_the_number_of_whole_loops_closest_to_the_delay()
    {
        AreClose(30 - 0.05, Wait(3, 30));  // 10 loops
        AreClose(32 - 0.05, Wait(4, 30));  // 7.5 rounds up to 8 loops
        AreClose(12 - 0.05, Wait(12, 5));  // longer than the delay: one loop
        AreClose(10 - 0.05, Wait(10, 4));
        AreClose(6 - 0.05, Wait(3, 5));    // 2 loops (6 s) are closer to 5 s than 1 (3 s)
        AreClose(5.2 - 0.05, Wait(2.6, 5));
        AreClose(5 - 0.05, Wait(2.5, 5));  // fits exactly: 2 loops
        AreClose(60 - 0.05, Wait(60, 5));  // the longest loop still played whole
    }

    [TestMethod]
    public void A_still_photo_or_an_overlong_loop_gets_the_plain_delay()
    {
        AreClose(5, new AnimationLoop().SlideshowWait(TimeSpan.FromSeconds(5), Stopwatch.GetTimestamp()));
        AreClose(5, Wait(90, 5));
        AreClose(5, Wait(60.001, 5));
    }

    [TestMethod]
    public void Mid_loop_the_loop_under_way_counts_only_while_less_than_half_has_played()
    {
        AreClose(7 - 0.05, Wait(10, 4, elapsedSeconds: 3));   // counts: finish it
        AreClose(11 - 0.05, Wait(10, 4, elapsedSeconds: 9));  // nearly done: finish it, then one more
        AreClose(31 - 0.05, Wait(3, 30, elapsedSeconds: 5));  // finish loop 2 at 6 s, then 10 more
        AreClose(2.96, Wait(3, 3, elapsedSeconds: 2.99));
    }

    // --- The crossfade (headless Win2D) ---

    private static readonly Windows.UI.Color Red = Windows.UI.Color.FromArgb(255, 255, 0, 0), Blue = Windows.UI.Color.FromArgb(255, 0, 0, 255);

    [TestMethod]
    public void The_crossfade_holds_the_old_frame_then_mixes_to_the_new_one_without_a_dip()
    {
        Action<Microsoft.Graphics.Canvas.CanvasDrawingSession> Fill(Windows.UI.Color c) => s => s.Clear(c);
        using var screen = new Microsoft.Graphics.Canvas.CanvasRenderTarget(TestGpu.Device, 4, 4, 96);
        Windows.UI.Color Shown(Crossfade fade, long now)
        {
            using (var s = screen.CreateDrawingSession()) fade.Draw(s, Fill(Blue), now);
            return screen.GetPixelColors()[5];
        }

        long t0 = Stopwatch.GetTimestamp(), fadeTicks = Crossfade.FadeTicks;
        using var fade = new Crossfade(TestGpu.Device, new Size(4, 4), Fill(Red), t0);

        Assert.AreEqual(Red, Shown(fade, t0 + fadeTicks), "held until the new image lands");
        fade.Start(t0);
        Assert.IsFalse(fade.IsWaiting(t0));
        var mid = Shown(fade, t0 + fadeTicks / 2);
        Assert.AreEqual(255, mid.A, "opaque midway: the backdrop never shows through");
        Assert.IsTrue(mid.R is > 100 and < 155 && mid.B is > 100 and < 155, $"an even mix midway, got {mid}");
        Assert.IsFalse(fade.IsDone(t0 + fadeTicks - 1));
        Assert.AreEqual(Blue, Shown(fade, t0 + fadeTicks));
        Assert.IsTrue(fade.IsDone(t0 + fadeTicks));
    }

    [TestMethod]
    public void A_crossfade_waiting_too_long_for_the_new_image_fades_anyway()
    {
        long t0 = Stopwatch.GetTimestamp();
        using var fade = new Crossfade(TestGpu.Device, new Size(4, 4), s => s.Clear(Red), t0);
        Assert.IsTrue(fade.IsWaiting(t0 + Crossfade.MaxWaitTicks - 1), "a step lands meanwhile: it fades into it");
        Assert.IsFalse(fade.IsWaiting(t0 + Crossfade.MaxWaitTicks), "fading: a step by hand cuts it");
        Assert.IsFalse(fade.IsDone(t0 + Crossfade.MaxWaitTicks));
        Assert.IsTrue(fade.IsDone(t0 + Crossfade.MaxWaitTicks + Crossfade.FadeTicks));
    }

    // --- The timer ---

    private sealed class Show : IDisposable
    {
        public readonly ManualUi Ui = new();
        public readonly ManualClock Clock = new();
        public readonly Slideshow Slideshow;
        public int Steps;
        public bool HasNext = true;

        public Show() =>
            Slideshow = new Slideshow(() => TimeSpan.FromSeconds(5), () => { Steps++; return Task.FromResult(HasNext); }, Clock, Ui);

        public void Advance(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            Ui.RunPending();
        }

        public void Dispose()
        {
            Slideshow.Dispose();
            Ui.Dispose();
        }
    }

    [TestMethod]
    public void It_steps_after_each_wait_counted_from_the_image_being_ready()
    {
        using var s = new Show();
        s.Slideshow.Start();
        s.Advance(4.9);
        Assert.AreEqual(0, s.Steps);
        s.Advance(0.1);
        Assert.AreEqual(1, s.Steps);

        s.Advance(3);
        s.Slideshow.Rearm(); // the new photo's HQ is up only now
        s.Advance(4.9);
        Assert.AreEqual(1, s.Steps, "the wait counts from the image being ready");
        s.Advance(0.1);
        Assert.AreEqual(2, s.Steps);
    }

    [TestMethod]
    public void Pause_holds_the_photo_and_resume_gives_it_a_full_wait()
    {
        using var s = new Show();
        var states = new List<SlideshowState>();
        s.Slideshow.StateChanged += () => states.Add(s.Slideshow.State);
        s.Slideshow.Start();
        s.Advance(4);
        s.Slideshow.TogglePause();
        s.Advance(60);
        s.Slideshow.Rearm(); // ignored while paused
        s.Advance(60);
        Assert.AreEqual(0, s.Steps);

        s.Slideshow.TogglePause();
        s.Advance(4.9);
        Assert.AreEqual(0, s.Steps);
        s.Advance(0.1);
        Assert.AreEqual(1, s.Steps);
        CollectionAssert.AreEqual(new[] { SlideshowState.Playing, SlideshowState.Paused, SlideshowState.Playing }, states);
    }

    [TestMethod]
    public void The_end_of_the_show_stops_it()
    {
        using var s = new Show { HasNext = false };
        s.Slideshow.Start();
        s.Advance(5);
        Assert.AreEqual(SlideshowState.Stopped, s.Slideshow.State);
        s.Advance(60);
        Assert.AreEqual(1, s.Steps);
    }

    [TestMethod]
    public void A_step_whose_image_never_reports_ready_still_moves_on()
    {
        using var s = new Show();
        s.Slideshow.Start();
        s.Advance(5);
        s.Advance(34.9);
        Assert.AreEqual(1, s.Steps);
        s.Advance(0.1);
        Assert.AreEqual(2, s.Steps);
    }

    [TestMethod]
    public void A_held_show_waits_for_the_new_photo_to_be_ready()
    {
        using var s = new Show();
        s.Slideshow.Start();
        s.Advance(3);
        s.Slideshow.Hold(); // a delete put up another photo, at preview level
        s.Advance(10);
        Assert.AreEqual(0, s.Steps, "the old photo's wait no longer applies");
        s.Slideshow.Rearm();
        s.Advance(5);
        Assert.AreEqual(1, s.Steps);
    }

    [TestMethod]
    public void A_tick_posted_before_a_stop_is_dropped()
    {
        using var s = new Show();
        s.Slideshow.Start();
        s.Clock.Advance(TimeSpan.FromSeconds(5)); // the tick is posted, not yet run
        s.Slideshow.Stop();
        s.Ui.RunPending();
        Assert.AreEqual(0, s.Steps);
    }
}
