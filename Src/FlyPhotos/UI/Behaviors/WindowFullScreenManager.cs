#nullable enable
using System;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
///     Manages full-screen toggling and restore for a WinUI 3 window.
/// </summary>
/// <remarks>
///     Raises <see cref="PauseTrackingChanged"/> so that a companion placement
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
            FullScreenToggled?.Invoke(true);
            _wasMaximizedBeforeFullScreen = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            exitFullScreenButton?.Visibility = Visibility.Visible;
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
