using System;
using System.Diagnostics;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The animation on screen, as a slideshow times it: how long one loop runs, and when it started (a
/// <see cref="Stopwatch"/> timestamp). The default is "no animation".
/// </summary>
internal readonly record struct AnimationLoop(TimeSpan Duration, long StartedAt)
{
    /// <summary>Longer loops just get the plain delay.</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>The step comes this much before a loop ends, so the crossfade snapshot is its last frame, not frame 0.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long from <paramref name="now"/> until the next slideshow step: <paramref name="delay"/>, or for an
    /// animation the whole loops that fit in it (at least one). Mid-loop, the loop under way counts as the first.
    /// </summary>
    public TimeSpan SlideshowWait(TimeSpan delay, long now)
    {
        if (Duration <= TimeSpan.Zero || Duration > MaxDuration) return delay;
        long loops = Math.Max(1, delay.Ticks / Duration.Ticks);
        var elapsed = Stopwatch.GetElapsedTime(StartedAt, now);
        long loopsDone = elapsed.Ticks / Duration.Ticks;
        var wait = TimeSpan.FromTicks((loopsDone + loops) * Duration.Ticks) - elapsed - Lead;
        return wait > TimeSpan.Zero ? wait : wait + Duration;
    }
}
