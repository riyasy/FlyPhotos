#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Infra.Interop;
using NLog;

namespace FlyPhotos.UI.Behaviors;

internal enum SlideshowState { Stopped, Playing, Paused }

/// <summary>
/// The slideshow's states and timer, UI thread; it knows nothing of photos. Its owner calls <see cref="Rearm"/> when
/// a new image is up, so the wait counts from when the image is on screen, not from the step.
/// </summary>
internal sealed class Slideshow : IDisposable
{
    /// <summary>A held show whose image never reports ready (a page that fails to load) moves on after this much more.</summary>
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(30);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly Func<TimeSpan> _wait;
    private readonly Func<Task<bool>> _step;
    private readonly TimeProvider _clock;
    private readonly SynchronizationContext _ui;

    // One timer per wait, numbered, so a tick already posted by a cancelled one is told apart and dropped.
    private ITimer? _timer;
    private int _timerId;

    /// <param name="wait">How long the image now on screen stays up.</param>
    /// <param name="step">Moves to the next photo or page; false at the end of the show.</param>
    /// <param name="clock">The clock; tests pass their own.</param>
    /// <param name="ui">The UI thread, where ticks run.</param>
    public Slideshow(Func<TimeSpan> wait, Func<Task<bool>> step, TimeProvider clock, SynchronizationContext ui)
    {
        (_wait, _step, _clock, _ui) = (wait, step, clock, ui);
    }

    public SlideshowState State { get; private set; }

    public event Action? StateChanged;

    public void Start()
    {
        if (State == SlideshowState.Stopped) Go(SlideshowState.Playing);
    }

    public void Stop()
    {
        if (State != SlideshowState.Stopped) Go(SlideshowState.Stopped);
    }

    public void Pause()
    {
        if (State == SlideshowState.Playing) Go(SlideshowState.Paused);
    }

    /// <summary>Pauses, or resumes with a full wait for the photo on screen.</summary>
    public void TogglePause()
    {
        if (State != SlideshowState.Stopped) Go(State == SlideshowState.Playing ? SlideshowState.Paused : SlideshowState.Playing);
    }

    /// <summary>The new photo or page is on screen: its wait counts from now.</summary>
    public void Rearm()
    {
        if (State == SlideshowState.Playing) Arm();
    }

    /// <summary>A new photo is coming (a step, a delete): wait for <see cref="Rearm"/>, so it isn't cut short.</summary>
    public void Hold()
    {
        if (State == SlideshowState.Playing) Arm(StuckAfter);
    }

    private void Go(SlideshowState state)
    {
        State = state;
        if (state == SlideshowState.Playing) Arm();
        else Disarm();
        KeepDisplayOn(state == SlideshowState.Playing);
        StateChanged?.Invoke();
    }

    private void Arm(TimeSpan extra = default)
    {
        Disarm();
        var id = _timerId;
        _timer = _clock.CreateTimer(_ => _ui.Post(OnTick, id), null, _wait() + extra, Timeout.InfiniteTimeSpan);
    }

    private void Disarm()
    {
        _timerId++;
        _timer?.Dispose();
        _timer = null;
    }

    private async void OnTick(object? id)
    {
        if ((int)id! != _timerId || State != SlideshowState.Playing) return;
        Hold(); // a page step raises no PhotoChanging
        try
        {
            if (!await _step()) Stop();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Slideshow step failed");
            Stop();
        }
    }

    private static void KeepDisplayOn(bool on) =>
        Win32Methods.SetThreadExecutionState(Win32Methods.ES_CONTINUOUS | (on ? Win32Methods.ES_DISPLAY_REQUIRED : 0));

    public void Dispose()
    {
        Disarm();
        KeepDisplayOn(false);
    }
}
