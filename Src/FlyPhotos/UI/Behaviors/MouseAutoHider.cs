using System;
using System.Diagnostics;
using System.IO;
using System.Timers;
using FlyPhotos.Infra.Configuration;
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
public partial class CursorHost : Grid
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
public partial class MouseAutoHider : IDisposable
{
    // The UI element whose cursor is being managed.
    private readonly CursorHost _host;

    // A UI-thread timer that triggers when the mouse has been inactive for a set duration.
    private readonly DispatcherTimer _inActivityTimer;
    // A background timer to create a short grace period after hiding the cursor, preventing flickering.
    private readonly Timer _ignoreActivityTimer;

    // State flags.
    private bool _isCursorShown = true;
    private bool _isIgnoringActivity = false;

    // The pre-loaded invisible cursor resource.
    private readonly InputCursor _transparentCursor;
    // The pre-loaded visible cursor resource.
    private readonly InputCursor _defaultCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    // Flag for implementing the IDisposable pattern.
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new instance of the MouseInactivityHelper.
    /// </summary>
    /// <param name="host">The UI element to attach to.</param>
    /// <param name="timeout">The duration of inactivity before the cursor is hidden.</param>
    public MouseAutoHider(CursorHost host, TimeSpan? timeout = null)
    {
        // Resolve the path to the cursor file for both packaged and unpackaged app scenarios.
        var cursorPath = Path.Combine(PathResolver.IsPackagedApp ?
            Windows.ApplicationModel.Package.Current.InstalledLocation.Path : AppContext.BaseDirectory, "Assets", "transparent.cur");
        _transparentCursor = Win32CursorMethods.LoadCursor(cursorPath);

        // Set up the host element and the initial cursor state.
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _host.PublicCursor = _defaultCursor;

        // Configure the inactivity timer.
        _inActivityTimer = new DispatcherTimer { Interval = timeout ?? TimeSpan.FromSeconds(1) };
        _inActivityTimer.Tick += OnInactivityTimeout;

        // Configure the grace period timer to prevent cursor bounce.
        _ignoreActivityTimer = new Timer(TimeSpan.FromMilliseconds(250));
        _ignoreActivityTimer.Elapsed += IgnoreActivityTimeout;
        _ignoreActivityTimer.AutoReset = false; // Ensures the timer only runs once per start.

        // Subscribe to all relevant pointer events to detect user activity.
        _host.PointerMoved += OnPointerActivity;
        _host.PointerPressed += OnPointerActivity;
        _host.PointerReleased += OnPointerActivity;
        _host.PointerWheelChanged += OnPointerActivity;
        _host.Loaded += (_, _) => { ResetInactivityTimer(); };
    }

    /// <summary>
    /// Called when the inactivity timer elapses. Hides the cursor and starts the grace period.
    /// </summary>
    private void OnInactivityTimeout(object sender, object e)
    {
        _inActivityTimer.Stop();
        if (AppConfig.Settings.AutoHideMouse && _isCursorShown)
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

        if (AppConfig.Settings.AutoHideMouse)
            ResetInactivityTimer();
    }

    /// <summary>
    /// Called when the grace period timer elapses. Resumes normal pointer activity tracking.
    /// </summary>
    private void IgnoreActivityTimeout(object sender, object e)
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
            Debug.WriteLine("MOUSE VISIBLE");
            _host.PublicCursor = _defaultCursor;
            _isCursorShown = true;
        }
        else
        {
            Debug.WriteLine("MOUSE HIDDEN");
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
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        // Unsubscribe from events to prevent memory leaks.
        _inActivityTimer.Tick -= OnInactivityTimeout;
        _ignoreActivityTimer.Elapsed -= IgnoreActivityTimeout;

        // Stop timers.
        _inActivityTimer.Stop();
        _ignoreActivityTimer.Stop();
        _ignoreActivityTimer.Dispose(); // Dispose the System.Timers.Timer.

        // Unsubscribe from host pointer events.
        _host.PointerMoved -= OnPointerActivity;
        _host.PointerPressed -= OnPointerActivity;
        _host.PointerReleased -= OnPointerActivity;
        _host.PointerWheelChanged -= OnPointerActivity;

        // Ensure the cursor is visible when the helper is disposed.
        if (!_isCursorShown)
            _host.DispatcherQueue.TryEnqueue(() => SetCursorVisible(true));

        _disposed = true;

        // Suppress finalization as we have cleaned up resources.
        GC.SuppressFinalize(this);
    }
}