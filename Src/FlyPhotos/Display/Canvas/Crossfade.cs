using System;
using System.Diagnostics;
using System.Numerics;
using FlyPhotos.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// One crossfade, W2D thread: a snapshot of the old frame, then the new one fading in. A straight weighted mix
/// (<see cref="CrossFadeEffect"/>) of two targets, because two alpha-blended draws let the backdrop show through midway.
/// </summary>
internal sealed class Crossfade : IDisposable
{
    public static readonly long FadeTicks = Stopwatch.Frequency * Constants.CrossfadeMs / 1000;
    public static readonly long MaxWaitTicks = Stopwatch.Frequency * Constants.CrossfadeMaxWaitMs / 1000;

    private readonly CanvasRenderTarget _old;
    private readonly CanvasRenderTarget _new;
    private readonly CrossFadeEffect _mix;

    /// <summary>When the fade begins (a Stopwatch timestamp): the latest it may wait for the new image, until it lands.</summary>
    private long _startsAt;

    /// <summary>Snapshots what <paramref name="drawFrame"/> draws now, at <paramref name="pixels"/>.</summary>
    public Crossfade(ICanvasResourceCreator device, Size pixels, Action<CanvasDrawingSession> drawFrame, long now)
    {
        _old = Target(device, pixels);
        _new = Target(device, pixels);
        using (var session = _old.CreateDrawingSession()) drawFrame(session);
        _mix = new CrossFadeEffect { Source1 = _old, Source2 = _new }; // CrossFade 0 shows Source1, 1 Source2
        _startsAt = now + MaxWaitTicks;
    }

    /// <summary>96 DPI, so one DIP is one pixel and the frame draws exactly as on screen in pixel units.</summary>
    private static CanvasRenderTarget Target(ICanvasResourceCreator device, Size pixels) =>
        new(device, (float)pixels.Width, (float)pixels.Height, 96);

    /// <summary>The new image is up: the fade runs from now. Later calls (preview → HQ) don't restart it.</summary>
    public void Start(long now) => _startsAt = Math.Min(_startsAt, now);

    /// <summary>Still holding the old frame for the new image.</summary>
    public bool IsWaiting(long now) => now < _startsAt;

    public bool IsDone(long now) => now - _startsAt >= FadeTicks;

    /// <summary>Draws the mix of old and new onto <paramref name="screen"/>; just the old one while it waits.</summary>
    public void Draw(CanvasDrawingSession screen, Action<CanvasDrawingSession> drawFrame, long now)
    {
        screen.Clear(Colors.Transparent);
        screen.Units = CanvasUnits.Pixels;
        screen.Transform = Matrix3x2.Identity;
        if (now <= _startsAt)
        {
            screen.DrawImage(_old);
            return;
        }
        using (var session = _new.CreateDrawingSession()) drawFrame(session);
        var t = Math.Min(1f, (float)(now - _startsAt) / FadeTicks);
        _mix.CrossFade = t * t * (3 - 2 * t); // ease in and out
        screen.DrawImage(_mix);
    }

    public void Dispose()
    {
        _mix.Dispose();
        _old.Dispose();
        _new.Dispose();
    }
}
