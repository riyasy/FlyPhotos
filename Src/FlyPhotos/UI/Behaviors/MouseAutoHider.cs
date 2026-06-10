using System;
using System.Diagnostics;
using System.IO;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
/// A custom Grid that exposes the ProtectedCursor property for easy access.
/// </summary>
public sealed partial class CursorHost : Grid
{
    public InputCursor PublicCursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}

/// <summary>
/// Manages mouse cursor visibility, hiding it after a period of inactivity
/// and showing it again on any pointer activity.
/// </summary>
/// <remarks>
/// Must be created, used, and disposed on the UI thread. All timers are
/// <see cref="DispatcherTimer"/>s, so the entire class is single-threaded.
/// </remarks>
public partial class MouseAutoHider : IDisposable
{
    // The UI element whose cursor is being managed.
    private readonly CursorHost _host;

    // Triggers when the mouse has been inactive for a set duration.
    private readonly DispatcherTimer _inActivityTimer;
    // A short grace period after hiding the cursor, preventing flicker from
    // the synthetic pointer move that a cursor change can generate.
    private readonly DispatcherTimer _ignoreActivityTimer;

    // State flags. All accessed only on the UI thread.
    private bool _isCursorShown = true;
    private bool _isIgnoringActivity;

    // The pre-loaded invisible cursor resource. May be null if the asset fails to load.
    private readonly InputCursor _transparentCursor;
    // The pre-loaded visible cursor resource.
    private readonly InputCursor _defaultCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    // Stored handler so it can be unsubscribed in Dispose.
    private readonly RoutedEventHandler _onHostLoaded;

    // Flag for implementing the IDisposable pattern.
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the MouseAutoHider.
    /// </summary>
    /// <param name="host">The UI element to attach to.</param>
    /// <param name="enabled">Whether auto-hiding is active from the start.</param>
    /// <param name="timeout">The duration of inactivity before the cursor is hidden.</param>
    public MouseAutoHider(CursorHost host, bool enabled, TimeSpan? timeout = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        var cursorPath = Path.Combine(PathResolver.IsPackagedApp
            ? Windows.ApplicationModel.Package.Current.InstalledLocation.Path
            : AppContext.BaseDirectory, "Assets", "transparent.cur");
        _transparentCursor = Win32CursorMethods.LoadCursor(cursorPath);
        if (_transparentCursor == null)
            Debug.WriteLine($"MouseAutoHider: failed to load transparent cursor from '{cursorPath}'. Cursor hiding will be a no-op.");

        _host.PublicCursor = _defaultCursor;

        _inActivityTimer = new DispatcherTimer { Interval = timeout ?? TimeSpan.FromSeconds(1) };
        _inActivityTimer.Tick += OnInactivityTimeout;

        _ignoreActivityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ignoreActivityTimer.Tick += OnIgnoreActivityTimeout;

        _onHostLoaded = (_, _) => ResetInactivityTimer();

        Enabled = enabled;
    }

    /// <summary>
    ///     Gets or sets whether auto-hiding is active. When <see langword="false"/>, all pointer
    ///     event subscriptions and timers are torn down and the cursor is restored.
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
                _host.PointerMoved += OnPointerActivity;
                _host.PointerPressed += OnPointerActivity;
                _host.PointerReleased += OnPointerActivity;
                _host.PointerWheelChanged += OnPointerActivity;
                if (_host.IsLoaded)
                    ResetInactivityTimer();
                else
                    _host.Loaded += _onHostLoaded;
            }
            else
            {
                _host.PointerMoved -= OnPointerActivity;
                _host.PointerPressed -= OnPointerActivity;
                _host.PointerReleased -= OnPointerActivity;
                _host.PointerWheelChanged -= OnPointerActivity;
                _host.Loaded -= _onHostLoaded;
                _inActivityTimer.Stop();
                _ignoreActivityTimer.Stop();
                _isIgnoringActivity = false;
                if (!_isCursorShown)
                    SetCursorVisible(true);
            }
        }
    }

    /// <summary>
    /// Called when the inactivity timer elapses. Hides the cursor and starts the grace period.
    /// </summary>
    private void OnInactivityTimeout(object sender, object e)
    {
        _inActivityTimer.Stop();
        if (_isCursorShown)
        {
            SetCursorVisible(false);
            // Start ignoring subsequent pointer events for a short duration.
            _isIgnoringActivity = true;
            _ignoreActivityTimer.Start();
        }
    }

    /// <summary>
    /// Called on any pointer activity. Makes the cursor visible and resets the inactivity timer.
    /// </summary>
    private void OnPointerActivity(object sender, PointerRoutedEventArgs e)
    {
        if (_isIgnoringActivity) return; // Do nothing if we're in the grace period.

        if (!_isCursorShown)
            SetCursorVisible(true);

        ResetInactivityTimer();
    }

    /// <summary>
    /// Called when the grace period timer elapses. Resumes normal pointer activity tracking.
    /// </summary>
    private void OnIgnoreActivityTimeout(object sender, object e)
    {
        _ignoreActivityTimer.Stop();
        _isIgnoringActivity = false;
    }

    /// <summary>
    /// Sets the cursor to be either the standard arrow (visible) or a transparent cursor (hidden).
    /// </summary>
    private void SetCursorVisible(bool visible)
    {
        if (visible)
        {
            _host.PublicCursor = _defaultCursor;
            _isCursorShown = true;
        }
        else
        {
            // If the transparent cursor failed to load, leave the default cursor in place
            // rather than assigning null (which would show the default cursor anyway).
            if (_transparentCursor == null) return;
            _host.PublicCursor = _transparentCursor;
            _isCursorShown = false;
        }
    }

    /// <summary>
    /// Helper method to restart the inactivity timer countdown.
    /// </summary>
    private void ResetInactivityTimer()
    {
        _inActivityTimer.Stop();
        _inActivityTimer.Start();
    }

    /// <summary>
    /// Cleans up resources by stopping timers and unsubscribing from events.
    /// Must be called on the UI thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe timer handlers and stop timers.
        _inActivityTimer.Tick -= OnInactivityTimeout;
        _ignoreActivityTimer.Tick -= OnIgnoreActivityTimeout;
        _inActivityTimer.Stop();
        _ignoreActivityTimer.Stop();

        // Unsubscribe from host pointer events.
        _host.PointerMoved -= OnPointerActivity;
        _host.PointerPressed -= OnPointerActivity;
        _host.PointerReleased -= OnPointerActivity;
        _host.PointerWheelChanged -= OnPointerActivity;
        _host.Loaded -= _onHostLoaded;

        // Detach our cursor from the host BEFORE disposing the cursor resources,
        // so the host never holds a disposed InputCursor. null restores the
        // default (visible) cursor, so no separate "show" step is needed.
        _host.PublicCursor = null;

        _transparentCursor?.Dispose();
        _defaultCursor?.Dispose();

        GC.SuppressFinalize(this);
    }
}