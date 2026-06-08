#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Behaviors;

// Handles XButton1/XButton2 (mouse back/forward) hold-navigation.
// Lazy: registers only a lightweight sentinel on construction. The timer and
// full press/release handlers are allocated on the first XButton event so
// users without side-button mice pay no ongoing cost.
internal sealed partial class SideButtonNavBehavior
{
    private readonly UIElement _root;
    private readonly Func<NavDirection, Task> _fly;
    private readonly Func<Task> _brake;
    private readonly Func<bool> _isSinglePhoto;
    private readonly Func<bool> _isStepZoomMode;

    private readonly PointerEventHandler _sentinel;
    private PointerEventHandler? _pressedHandler;
    private PointerEventHandler? _releasedHandler;

    private DispatcherTimer? _timer;
    private int _direction;   // -1 = back, 0 = none, 1 = forward
    private bool _inDelay;

    public SideButtonNavBehavior(
        UIElement root,
        Func<NavDirection, Task> fly,
        Func<Task> brake,
        Func<bool> isSinglePhoto,
        Func<bool> isStepZoomMode)
    {
        _root = root;
        _fly = fly;
        _brake = brake;
        _isSinglePhoto = isSinglePhoto;
        _isStepZoomMode = isStepZoomMode;

        _sentinel = OnFirstXButton;
        root.AddHandler(UIElement.PointerPressedEvent, _sentinel, true);
    }

    // Called from window Closed handler.
    public void Detach()
    {
        if (_pressedHandler is not null)
        {
            _timer!.Stop();
            _root.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
            _root.RemoveHandler(UIElement.PointerReleasedEvent, _releasedHandler);
        }
        else
        {
            _root.RemoveHandler(UIElement.PointerPressedEvent, _sentinel);
        }
    }

    // Sentinel: fires for every PointerPressed on the root until the first XButton
    // event, then promotes to full press/release handlers and allocates the timer.
    private void OnFirstXButton(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(_root).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;

        _root.RemoveHandler(UIElement.PointerPressedEvent, _sentinel);

        _pressedHandler = OnPressed;
        _releasedHandler = OnReleased;
        _root.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, true);
        _root.AddHandler(UIElement.PointerReleasedEvent, _releasedHandler, true);

        _timer = new DispatcherTimer();
        _timer.Tick += OnTimerTick;

        HandlePress(kind);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
        => HandlePress(e.GetCurrentPoint(_root).Properties.PointerUpdateKind);

    private void HandlePress(PointerUpdateKind kind)
    {
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
        if (_isSinglePhoto() || _isStepZoomMode()) return;

        var dir = kind == PointerUpdateKind.XButton1Pressed ? NavDirection.Prev : NavDirection.Next;
        _direction = dir == NavDirection.Prev ? -1 : 1;
        _ = _fly(dir);

        _inDelay = true;
        _timer!.Interval = KeyboardDelayInterval();
        _timer.Stop();
        _timer.Start();
    }

    private async void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(_root).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released)) return;
        if (_isStepZoomMode()) return;

        _timer!.Stop();
        _direction = 0;
        await _brake();
    }

    private async void OnTimerTick(object? sender, object e)
    {
        if (_direction == 0) { _timer!.Stop(); return; }
        if (_inDelay)
        {
            // Initial delay elapsed — switch to the OS repeat rate for subsequent ticks.
            _inDelay = false;
            _timer!.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(KeyboardRepeatIntervalMs());
            _timer.Start();
        }
        await _fly(_direction < 0 ? NavDirection.Prev : NavDirection.Next);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    private static TimeSpan KeyboardDelayInterval()
    {
        uint delay = 1;
        SystemParametersInfo(0x0016 /* SPI_GETKEYBOARDDELAY */, 0, ref delay, 0);
        // 0 → 250 ms, 1 → 500 ms, 2 → 750 ms, 3 → 1000 ms
        return TimeSpan.FromMilliseconds((delay + 1) * 250);
    }

    private static int KeyboardRepeatIntervalMs()
    {
        uint speed = 15;
        SystemParametersInfo(0x000A /* SPI_GETKEYBOARDSPEED */, 0, ref speed, 0);
        // speed 0 → ~400 ms (2.5 cps),  speed 31 → ~33 ms (30 cps)
        return (int)(1000.0 / (2.5 + speed * 27.5 / 31.0));
    }
}
