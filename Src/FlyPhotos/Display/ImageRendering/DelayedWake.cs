using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Wakes the canvas once after a delay, so it can sleep until then; one wake pending at a time. Any thread. The timer
/// is coarse (about 16 ms), so it suits "redraw when settled" and "the next frame is a while off".
/// </summary>
internal sealed class DelayedWake(Action wake)
{
    private int _pending;
    private volatile bool _cancelled;

    public void After(int ms)
    {
        if (_cancelled || Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Delay(Math.Max(1, ms)).ContinueWith(_ =>
        {
            Volatile.Write(ref _pending, 0);
            if (!_cancelled) wake();
        }, TaskScheduler.Default);
    }

    /// <summary>The renderer is gone: no more wakes.</summary>
    public void Cancel() => _cancelled = true;
}
