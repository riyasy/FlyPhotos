using System;
using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// The one way into a CanvasAnimatedControl's W2D thread: a lock-free queue of work, applied at the top of each
/// Update, and the pause/wake bookkeeping, so the control sleeps when there is nothing to do.
/// </summary>
internal sealed class W2dQueue<T>(ICanvasAnimatedControl control, Action<T> apply)
{
    private readonly ConcurrentQueue<T> _pending = new();

    /// <summary>Queues <paramref name="work"/> and wakes the control. Any thread.</summary>
    public void Enqueue(T work)
    {
        _pending.Enqueue(work);
        control.Paused = false; // Win2D documents Paused as safe from any thread
    }

    /// <summary>Wakes the control for at least one more Update and Draw. Any thread.</summary>
    public void Wake() => control.Paused = false;

    /// <summary>W2D thread, top of Update.</summary>
    public void Drain()
    {
        while (_pending.TryDequeue(out var work))
            apply(work);
    }

    /// <summary>
    /// W2D thread, end of Update: pauses unless there is queued or other work. Work queued while pausing is seen by the
    /// second check, so a wake-up can't be lost (no lock needed).
    /// </summary>
    public void PauseIfIdle(bool hostHasWork)
    {
        if (_pending.IsEmpty && !hostHasWork)
        {
            control.Paused = true;
            if (!_pending.IsEmpty)
                control.Paused = false;
        }
    }
}
