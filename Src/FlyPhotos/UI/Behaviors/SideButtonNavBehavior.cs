#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Behaviors;

// Handles XButton1/XButton2 (mouse back/forward) hold-navigation.
// Lazy: registers only a lightweight sentinel on construction. The full
// press/release handlers are allocated on the first XButton event so
// users without side-button mice pay no ongoing cost.
internal sealed partial class SideButtonNavBehavior
{
    private readonly UIElement _root;
    private readonly DispatcherQueue _dispatcherQueue; // captured on UI thread; safe to use from pool thread
    private readonly Func<NavDirection, Task> _fly;
    private readonly Func<Task> _brake;
    private readonly Func<bool> _isStepZoomMode;

    private readonly PointerEventHandler _sentinel;
    private PointerEventHandler? _pressedHandler;
    private PointerEventHandler? _releasedHandler;

    private CancellationTokenSource? _repeatCts;

    public SideButtonNavBehavior(
        UIElement root,
        Func<NavDirection, Task> fly,
        Func<Task> brake,
        Func<bool> isStepZoomMode)
    {
        _root = root;
        _dispatcherQueue = root.DispatcherQueue; // ctor runs on the UI thread
        _fly = fly;
        _brake = brake;
        _isStepZoomMode = isStepZoomMode;

        _sentinel = OnFirstXButton;
        root.AddHandler(UIElement.PointerPressedEvent, _sentinel, true);
    }

    // Called from window Closed handler (UI thread).
    public void Detach()
    {
        var cts = _repeatCts;
        _repeatCts = null;
        cts?.Cancel();
        cts?.Dispose();

        if (_pressedHandler is not null)
        {
            _root.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
            _root.RemoveHandler(UIElement.PointerReleasedEvent, _releasedHandler);
        }
        else
        {
            _root.RemoveHandler(UIElement.PointerPressedEvent, _sentinel);
        }
    }

    // Sentinel: fires for every PointerPressed on the root until the first XButton
    // event, then promotes to full press/release handlers.
    private void OnFirstXButton(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(_root).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;

        _root.RemoveHandler(UIElement.PointerPressedEvent, _sentinel);

        _pressedHandler = OnPressed;
        _releasedHandler = OnReleased;
        _root.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, true);
        _root.AddHandler(UIElement.PointerReleasedEvent, _releasedHandler, true);

        HandlePress(kind);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
        => HandlePress(e.GetCurrentPoint(_root).Properties.PointerUpdateKind);

    private void HandlePress(PointerUpdateKind kind)
    {
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
        if (_isStepZoomMode()) return;

        var dir = kind == PointerUpdateKind.XButton1Pressed ? NavDirection.Prev : NavDirection.Next;

        FlySafe(dir);

        var old = _repeatCts;
        _repeatCts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();

        // Read keyboard timings once per hold; they can't change mid-press,
        // and this keeps the P/Invokes off the repeat loop.
        _ = RunRepeatAsync(dir, KeyboardDelayMs(), KeyboardRepeatMs(), _repeatCts.Token);
    }

    private async void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var kind = e.GetCurrentPoint(_root).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Released or PointerUpdateKind.XButton2Released)) return;

        var cts = _repeatCts;
        _repeatCts = null;
        cts?.Cancel();
        cts?.Dispose();

        try { await _brake(); } catch { /* prevent async void crash */ }
    }

    // Runs on the ThreadPool so Task.Delay timing is independent of UI thread load.
    // Navigations are posted to the DispatcherQueue, where they queue up just like
    // keyboard WM_KEYDOWN messages queue in the Windows message pump.
    private async Task RunRepeatAsync(NavDirection dir, int delayMs, int repeatMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    // Re-check on the UI thread: cancellation (release/detach) may have
                    // landed after the loop's check but before this delegate runs,
                    // which would otherwise post a stray step after _brake().
                    if (!token.IsCancellationRequested) FlySafe(dir);
                });
                await Task.Delay(repeatMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // Fire-and-forget _fly with the same fault-swallowing posture as _brake,
    // so a faulted navigation Task doesn't go unobserved.
    private async void FlySafe(NavDirection dir)
    {
        try { await _fly(dir); } catch { /* prevent async void crash */ }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    private static int KeyboardDelayMs()
    {
        uint delay = 1;
        SystemParametersInfo(0x0016 /* SPI_GETKEYBOARDDELAY */, 0, ref delay, 0);
        // 0 → 250 ms, 1 → 500 ms, 2 → 750 ms, 3 → 1000 ms
        return (int)((delay + 1) * 250);
    }

    private static int KeyboardRepeatMs()
    {
        uint speed = 15;
        SystemParametersInfo(0x000A /* SPI_GETKEYBOARDSPEED */, 0, ref speed, 0);
        // speed 0 → ~400 ms (2.5 cps),  speed 31 → ~33 ms (30 cps)
        return (int)(1000.0 / (2.5 + speed * 27.5 / 31.0));
    }
}
