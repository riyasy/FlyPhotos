#nullable enable
using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
///     Shows the window caption buttons only when the pointer is near the top of the window,
///     and hides them again after a short inactivity delay.
/// </summary>
/// <remarks>
///     In windowed mode this targets the native caption buttons (minimize, maximize, close)
///     via <see cref="AppWindowTitleBar.PreferredHeightOption"/>: <see cref="TitleBarHeightOption.Collapsed"/>
///     hides all three as a unit, <see cref="TitleBarHeightOption.Standard"/> restores them.
///     <para>
///         While <see cref="IsFullScreen"/> is <see langword="true"/> (the window is in full-screen
///         mode, where the native title bar doesn't exist), the same hover show/hide logic instead
///         targets the custom full-screen close button passed to the constructor, if any. The
///         caller is responsible for wiring <see cref="IsFullScreen"/> to the relevant event.
///     </para>
/// </remarks>
internal sealed class WindowCaptionButtonFader
{
    /// <summary>
    ///     Height of the hover zone at the top of the window, in logical pixels.
    ///     Slightly larger than the physical titlebar height (28 px) to give a comfortable
    ///     target area that accounts for small pointer overshoots.
    /// </summary>
    private const int TitlebarZoneHeight = 40;

    /// <summary>
    ///     Delay after the pointer leaves the titlebar zone before the buttons are hidden.
    /// </summary>
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(1000);

    private readonly AppWindowTitleBar _titleBar;
    private readonly UIElement _rootElement;
    private readonly UIElement? _fullScreenCloseButton;

    /// <summary>
    ///     Fires <see cref="HideButtons"/> after <see cref="HideDelay"/> of pointer inactivity
    ///     outside the titlebar zone.
    /// </summary>
    private readonly DispatcherTimer _hideTimer;

    /// <summary>
    ///     <see langword="true"/> while the caption buttons are currently visible.
    /// </summary>
    private bool _buttonsVisible;

    /// <summary>
    ///     Gets or sets whether the window is in full-screen mode, where the hover logic targets
    ///     <see cref="_fullScreenCloseButton"/> instead of the native title bar. Typically set by
    ///     the composer in response to a full-screen toggle event. While <see cref="Enabled"/> is
    ///     <see langword="false"/>, changing this is a no-op — both targets are already forced
    ///     permanently visible and stay that way.
    /// </summary>
    internal bool IsFullScreen
    {
        private get;
        set
        {
            if (field == value) return;
            field = value;
            if (Enabled) HideButtons();
        }
    }

    /// <summary>
    ///     Gets or sets whether the fader is active. When <see langword="false"/>, both the native
    ///     caption buttons and the full-screen close button (if any) are forced permanently visible
    ///     and pointer-driven show/hide logic is ignored. When toggled back to <see langword="true"/>,
    ///     the currently relevant target is hidden again, matching constructor behavior.
    /// </summary>
    internal bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value)
            {
                _rootElement.PointerMoved += OnPointerMoved;
                HideButtons();
            }
            else
            {
                _rootElement.PointerMoved -= OnPointerMoved;
                _hideTimer.Stop();
                _buttonsVisible = true;
                // Force both targets to their "always visible" state directly rather than going
                // through ApplyVisibility, which only touches whichever one is current per
                // IsFullScreen and would leave the other stale if disabled mid-full-screen.
                // The title bar is always Standard when windowed; the full-screen close button is
                // only shown while actually in full screen, else it must stay collapsed.
                _titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
                if (_fullScreenCloseButton is not null)
                    _fullScreenCloseButton.Visibility = IsFullScreen ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    ///     Initialises the fader, wires up the pointer-move listener, and immediately
    ///     hides the caption buttons.
    /// </summary>
    /// <param name="titleBar">
    ///     The <see cref="AppWindowTitleBar"/> whose <see cref="AppWindowTitleBar.PreferredHeightOption"/>
    ///     is toggled to show and hide the native caption buttons.
    /// </param>
    /// <param name="rootElement">
    ///     The root <see cref="UIElement"/> of the window (typically the full-window layout grid).
    ///     <see cref="UIElement.PointerMoved"/> is used to track pointer position.
    /// </param>
    /// <param name="enabled">
    ///     Initial value for <see cref="Enabled"/>. When <see langword="false"/>, the fader is
    ///     created but does nothing until <see cref="Enabled"/> is set to <see langword="true"/>.
    /// </param>
    /// <param name="fullScreenCloseButton">
    ///     Optional custom close button shown only in full-screen mode. When set, it is faded
    ///     the same way the native caption buttons are while <see cref="IsFullScreen"/> is <see langword="true"/>.
    /// </param>
    public WindowCaptionButtonFader(AppWindowTitleBar titleBar, UIElement rootElement, bool enabled, UIElement? fullScreenCloseButton = null)
    {
        _titleBar = titleBar;
        _rootElement = rootElement;
        _fullScreenCloseButton = fullScreenCloseButton;

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) => HideButtons();

        Enabled = enabled;
    }

    /// <summary>
    ///     Called on every pointer-move event across the window.
    ///     Shows the buttons when the pointer enters the top <see cref="TitlebarZoneHeight"/>
    ///     pixels; starts the hide timer once it leaves.
    /// </summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint((UIElement)sender).Position.Y;
        if (y <= TitlebarZoneHeight)
        {
            _hideTimer.Stop();
            ShowButtons();
        }
        else if (_buttonsVisible)
        {
            // Start is a no-op if the timer is already running, so repeated
            // pointer-move firings outside the zone do not reset the countdown.
            _hideTimer.Start();
        }
    }

    /// <summary>
    ///     Makes the currently relevant button(s) visible. No-op if already visible.
    /// </summary>
    private void ShowButtons()
    {
        if (_buttonsVisible) return;
        _buttonsVisible = true;
        ApplyVisibility(true);
    }

    /// <summary>
    ///     Hides the currently relevant button(s).
    ///     Also stops the hide timer so it does not fire redundantly.
    /// </summary>
    private void HideButtons()
    {
        _hideTimer.Stop();
        _buttonsVisible = false;
        ApplyVisibility(false);
    }

    /// <summary>
    ///     Applies <paramref name="visible"/> to whichever target is currently relevant:
    ///     the full-screen close button while <see cref="IsFullScreen"/>, otherwise the native
    ///     title bar caption buttons.
    /// </summary>
    private void ApplyVisibility(bool visible)
    {
        if (IsFullScreen)
        {
            if (_fullScreenCloseButton is not null)
                _fullScreenCloseButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _titleBar.PreferredHeightOption = visible ? TitleBarHeightOption.Standard : TitleBarHeightOption.Collapsed;
        }
    }
}
