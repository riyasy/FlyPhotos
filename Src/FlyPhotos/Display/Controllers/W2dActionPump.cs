using System;
using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.Controllers;

/// <summary>
/// Shared UI-thread -> W2D-thread handoff for a single CanvasAnimatedControl: a lock-free action
/// queue plus the pause/wake bookkeeping every W2D Update loop in this app needs. Each controller
/// owns its own instance and its own control — this is not a shared/singleton pump.
/// </summary>
internal sealed class W2dActionPump(ICanvasAnimatedControl control)
{
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>Posts an action to the W2D thread and wakes the render loop. Safe from any thread.</summary>
    public void Enqueue(Action action)
    {
        _pending.Enqueue(action);
        control.Paused = false; // thread-safe; Win2D documents this as safe to call from any thread
    }

    /// <summary>Wakes the render loop for at least one more Update+Draw without posting any work.</summary>
    public void Wake() => control.Paused = false;

    /// <summary>W2D thread, top of Update: apply all queued requests.</summary>
    public void Drain()
    {
        while (_pending.TryDequeue(out var action))
            action();
    }

    /// <summary>
    /// W2D thread, bottom of Update. Pauses when the queue is empty AND the host reports no other
    /// pending frame work. Re-checks the queue after pausing: an item enqueued during the pause
    /// decision stays queued until drained, so this closes the enqueue/pause lost-wakeup race
    /// without a lock.
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
