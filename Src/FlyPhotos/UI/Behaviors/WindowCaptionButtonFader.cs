#nullable enable
using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
///     Shows the window caption buttons (minimize, maximize, close) only when the pointer
///     is near the top of the window, and hides them again after a short inactivity delay.
/// </summary>
/// <remarks>
///     Visibility is controlled via <see cref="AppWindowTitleBar.PreferredHeightOption"/>:
///     <see cref="TitleBarHeightOption.Collapsed"/> hides all three caption buttons as a unit;
///     <see cref="TitleBarHeightOption.Standard"/> restores them.
///     <para>
///         Set <see cref="Suspended"/> to <see langword="true"/> (e.g. when the window enters
///         full-screen mode) to suppress all show/hide logic and force the buttons hidden.
///         The caller is responsible for wiring this property to the relevant event.
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
    ///     Gets or sets whether show/hide logic is suspended.
    ///     Setting to <see langword="true"/> immediately hides the buttons; setting it
    ///     back to <see langword="false"/> restores them to the state dictated by
    ///     <see cref="Enabled"/> (forced visible when the fader is off, hidden-until-hover
    ///     when it is on). Typically set by the composer in response to a full-screen toggle event.
    /// </summary>
    internal bool Suspended
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value)
                HideButtons();
            else if (Enabled)
                // Auto-hide is active: resume hidden, reappear on hover near the top.
                HideButtons();
            else
                // Fader is off: caption buttons must stay permanently visible.
                ShowButtons();
        }
    }

    /// <summary>
    ///     Gets or sets whether the fader is active. When <see langword="false"/>, the caption
    ///     buttons are forced visible (Standard titlebar height) and pointer-driven show/hide
    ///     logic is ignored. When toggled back to <see langword="true"/>, the buttons are
    ///     hidden again, matching constructor behavior.
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
                _titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            }
        }
    }

    /// <summary>
    ///     Initialises the fader, wires up the pointer-move listener, and immediately
    ///     hides the caption buttons.
    /// </summary>
    /// <param name="titleBar">
    ///     The <see cref="AppWindowTitleBar"/> whose <see cref="AppWindowTitleBar.PreferredHeightOption"/>
    ///     is toggled to show and hide the caption buttons.
    /// </param>
    /// <param name="rootElement">
    ///     The root <see cref="UIElement"/> of the window (typically the full-window layout grid).
    ///     <see cref="UIElement.PointerMoved"/> is used to track pointer position.
    /// </param>
    /// <param name="enabled">
    ///     Initial value for <see cref="Enabled"/>. When <see langword="false"/>, the fader is
    ///     created but does nothing until <see cref="Enabled"/> is set to <see langword="true"/>.
    /// </param>
    public WindowCaptionButtonFader(AppWindowTitleBar titleBar, UIElement rootElement, bool enabled)
    {
        _titleBar = titleBar;
        _rootElement = rootElement;

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
        if (Suspended) return;

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
    ///     Makes the caption buttons visible by restoring the standard titlebar height.
    ///     No-op if they are already visible.
    /// </summary>
    private void ShowButtons()
    {
        if (_buttonsVisible) return;
        _buttonsVisible = true;
        _titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
    }

    /// <summary>
    ///     Hides the caption buttons by collapsing the titlebar height to zero.
    ///     Also stops the hide timer so it does not fire redundantly.
    /// </summary>
    private void HideButtons()
    {
        _hideTimer.Stop();
        _buttonsVisible = false;
        _titleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
    }
}
