#nullable enable
using System;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
///     Manages full-screen toggling and restore for a WinUI 3 window.
/// </summary>
/// <remarks>
///     Raises <see cref="FullScreenToggled"/> so that a companion placement
///     manager can suspend geometry capture while the window is in full-screen mode,
///     without either class holding a reference to the other.
/// </remarks>
internal sealed class WindowFullScreenManager
{
    private readonly Window _window;
    private AppWindow AppWindow => _window.AppWindow;

    private bool _wasMaximizedBeforeFullScreen;

    /// <summary>
    ///     Raised when full-screen is entered (<see langword="true" />) or exited (<see langword="false" />).
    /// </summary>
    internal event Action<bool>? FullScreenToggled;

    internal WindowFullScreenManager(Window window)
    {
        _window = window;
    }

    // .Kind discriminates without a memberless cast; { State: ... } reads a member to root the
    // OverlappedPresenter projection under NativeAOT+Release. See the full note in Restore().
    internal bool IsMaximizedOrFullScreen =>
        AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ||
        (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped &&
         AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized });

    internal void Maximize()
    {
        // { State: ... } reads a member to root the projection under NativeAOT+Release. See Restore().
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped &&
            AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } op)
            op.Maximize();
    }

    /// <summary>
    ///     Restores the window from a maximized or full-screen state to its normal overlapped (windowed) state.
    /// </summary>
    /// <param name="exitFullScreenButton">
    ///     An optional UI element (e.g., an 'Exit Full Screen' button) to collapse when
    ///     restoring from full-screen mode.
    /// </param>
    internal void Restore(UIElement? exitFullScreenButton = null)
    {
        // --- WinRT presenter checks under NativeAOT + Release ---
        // Discriminate the presenter via .Kind (a plain enum). A *memberless* type check such as
        // `is OverlappedPresenter` / `as OverlappedPresenter` can silently return false/null under
        // NativeAOT + Release: CsWinRT may not retain the projected type's vtable / type-mapping, so
        // the QueryInterface yields nothing. Debug and non-AOT builds are unaffected, which makes it
        // an easy regression to miss. See CsWinRT#1930 and microsoft-ui-xaml#10471 (both still open).
        //
        // The `is OverlappedPresenter { State: ... }` patterns used below ARE safe precisely because
        // they read a member (.State / .Restore): that member reference roots the projection mapping,
        // which is the same effect as the documented `Presenter.As<OverlappedPresenter>()` workaround.
        // Do NOT "simplify" these into a memberless cast — that reintroduces the silent AOT failure.
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } op)
                op.Restore();
        }
        else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            FullScreenToggled?.Invoke(false);
            exitFullScreenButton?.Visibility = Visibility.Collapsed;
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            (AppWindow.Presenter as OverlappedPresenter)?.Restore();
        }
    }

    /// <summary>
    /// Restores the window and makes its client area match the requested screen-space rectangle.
    /// </summary>
    /// <param name="clientRect">The desired client-area rectangle in physical screen pixels.</param>
    /// <param name="exitFullScreenButton">The optional button to hide when leaving full-screen mode.</param>
    internal void RestoreToClientRect(RectInt32 clientRect, UIElement? exitFullScreenButton = null)
    {
        var hwnd = WindowNative.GetWindowHandle(_window);

        // The image has already been prepared for the destination client rect. Suppress the
        // DWM resize transition so Windows does not move the photo during this transition.
        int transitionsDisabled = 1;
        Win32Methods.DwmSetWindowAttribute(
            hwnd,
            Win32Methods.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref transitionsDisabled,
            sizeof(int));

        try
        {
            // Seed the hidden normal placement with the requested rectangle so the presenter switch
            // reveals the window near its destination instead of at the old bounds. This is only a
            // flicker hint — the exact geometry is applied by AlignClientRect below, because the real
            // non-client offsets cannot be derived from system metrics on a window that extends its
            // content into the title bar (they are asymmetric: ~0 at the top, a resize border
            // elsewhere) and cannot be measured at all while the full-screen presenter is live.
            Win32Methods.GetWindowPlacement(hwnd, out var placement);
            placement.rcNormalPosition = new Win32Methods.RECT
            {
                Left = clientRect.X,
                Top = clientRect.Y,
                Right = clientRect.X + clientRect.Width,
                Bottom = clientRect.Y + clientRect.Height
            };
            placement.showCmd = Win32Methods.SW_SHOWNORMAL;

            var wasFullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

            Win32Methods.SetWindowPlacement(hwnd, in placement);

            if (wasFullScreen)
            {
                exitFullScreenButton?.Visibility = Visibility.Collapsed;
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _wasMaximizedBeforeFullScreen = false;
                FullScreenToggled?.Invoke(false);
            }

            AlignClientRect(hwnd, clientRect);
        }
        finally
        {
            transitionsDisabled = 0;
            Win32Methods.DwmSetWindowAttribute(
                hwnd,
                Win32Methods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref transitionsDisabled,
                sizeof(int));
        }
    }

    /// <summary>
    /// Positions the window so its client area matches <paramref name="targetClientRect"/> exactly,
    /// using measured (not estimated) non-client offsets.
    /// </summary>
    private static void AlignClientRect(nint hwnd, RectInt32 targetClientRect)
    {
        if (!Win32Methods.GetWindowRect(hwnd, out var windowRect) ||
            !Win32Methods.GetClientRect(hwnd, out var clientRect))
            return;

        var clientOrigin = new Win32Methods.POINT();
        if (!Win32Methods.ClientToScreen(hwnd, ref clientOrigin))
            return;

        var extraLeft = clientOrigin.X - windowRect.Left;
        var extraTop = clientOrigin.Y - windowRect.Top;
        var extraWidth = windowRect.Right - windowRect.Left - clientRect.Right;
        var extraHeight = windowRect.Bottom - windowRect.Top - clientRect.Bottom;

        var desiredLeft = targetClientRect.X - extraLeft;
        var desiredTop = targetClientRect.Y - extraTop;
        var desiredWidth = targetClientRect.Width + extraWidth;
        var desiredHeight = targetClientRect.Height + extraHeight;

        if (desiredLeft == windowRect.Left && desiredTop == windowRect.Top &&
            desiredWidth == windowRect.Right - windowRect.Left &&
            desiredHeight == windowRect.Bottom - windowRect.Top)
            return;

        Win32Methods.SetWindowPos(hwnd, 0, desiredLeft, desiredTop, desiredWidth, desiredHeight,
            Win32Methods.SWP_NOACTIVATE | Win32Methods.SWP_NOZORDER);
    }

    /// <summary>
    ///     Toggles the window between full-screen mode and the normal overlapped state.
    ///     Tracks previous maximized state to avoid flickering when returning from full-screen.
    /// </summary>
    /// <param name="exitFullScreenButton">
    ///     An optional UI element (e.g., an 'Exit Full Screen' button) to show during
    ///     full-screen mode and hide otherwise.
    /// </param>
    internal void ToggleFullScreen(UIElement? exitFullScreenButton = null)
    {
        // .Kind discriminates the presenter; the `is OverlappedPresenter { State: ... }` capture below
        // reads .State, which roots the projection so the cast resolves under NativeAOT + Release.
        // See the full note in Restore(). Refs: CsWinRT#1930, microsoft-ui-xaml#10471.
        // Do NOT reduce the State read to a memberless `is/as OverlappedPresenter`.
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
        {
            _wasMaximizedBeforeFullScreen = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            exitFullScreenButton?.Visibility = Visibility.Visible;
            // Fires last so a subscribed caption-button fader's hide decision (if auto-hide is
            // enabled) is the one that sticks, instead of being clobbered by the Visible set above.
            FullScreenToggled?.Invoke(true);
        }
        else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            FullScreenToggled?.Invoke(false);
            exitFullScreenButton?.Visibility = Visibility.Collapsed;
            // When exiting full screen, and the window was previously maximized,
            // the window will briefly go to restored window state and
            // then go to maximized. This causes a flicker. This happens because
            // the OverlappedPresenter goes to Restored state internally
            // when we go fullscreen instead of keeping state as maximized.
            if (_wasMaximizedBeforeFullScreen)
                NoFlickerMaximize(_window);
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _wasMaximizedBeforeFullScreen = false;
        }
    }

    /// <summary>
    ///     Maximizes the specified window using Win32 PInvoke, minimizing visual flicker during the transition.
    /// </summary>
    private static void NoFlickerMaximize(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        Win32Methods.GetWindowPlacement(hwnd, out var placement);
        placement.showCmd = Win32Methods.SW_SHOWMAXIMIZED;
        Win32Methods.SetWindowPlacement(hwnd, in placement);
    }
}
