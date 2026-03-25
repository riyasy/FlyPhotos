#nullable enable
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using WinUIEx.Messaging;

namespace FlyPhotos.UI.Behaviors;

// ---------------------------------------------------------------------------
// Internal data model  (hidden from external consumers)
// ---------------------------------------------------------------------------

/// <summary>
///     Internal snapshot of window placement.
/// </summary>
/// <param name="X">The X coordinate (left edge) of the window.</param>
/// <param name="Y">The Y coordinate (top edge) of the window.</param>
/// <param name="Width">The width of the window.</param>
/// <param name="Height">The height of the window.</param>
/// <param name="IsMaximized">Indicates whether the window is currently in a maximized state.</param>
internal readonly record struct WindowStateData(
    int X,
    int Y,
    int Width,
    int Height,
    bool IsMaximized);

// ---------------------------------------------------------------------------
// WindowSizeManager
// ---------------------------------------------------------------------------

/// <summary>
///     Manages saving and restoring a WinUI 3 window's placement (position, size,
///     maximized/restored state) across application launches, with automatic
///     monitor-layout change detection.
/// </summary>
/// <remarks>
///     <para>
///         The constructor accepts an opaque <see langword="string" /> that was
///         previously read from the <see cref="Data" /> property. The serialisation format
///         is an implementation detail; external callers only see flat strings.
///     </para>
///     <para>
///         After construction the manager listens for the <c>WM_SHOWWINDOW</c> message
///         using a <see cref="WinUIEx.Messaging.WindowMessageMonitor" />. The saved
///         placement is applied synchronously before the first paint, eliminating
///         restored-to-maximized visual flicker.
///     </para>
///     <para>
///         Whenever the placement changes (move, resize), the internal state is updated.
///         Callers can retrieve the latest serialised string via the <see cref="Data" />
///         property (typically during window close or deferral).
///     </para>
///     <para>
///         If the window was closed while in full-screen presenter mode the saved
///         string reflects the last known <em>overlapped</em> geometry so that on
///         relaunch the window always opens safely as a normal overlapped window.
///     </para>
///     <para>
///         If the connected monitors have changed (count, bounds, or resolution)
///         the saved placement is safely discarded to prevent off-screen windows.
///     </para>
/// </remarks>
public sealed partial class WindowSizeManager : IDisposable
{
    // Fields

    /// <summary>The internal AppWindow instance being managed.</summary>
    private AppWindow AppWindow => _window.AppWindow;

    /// <summary>The WinUI 3 Window instance being managed.</summary>
    private readonly Window _window;

    /// <summary>The native Win32 window handle (HWND).</summary>
    private readonly nint _hwnd;

    /// <summary>Current authoritative snapshot (always reflects overlapped geometry).</summary>
    private WindowStateData? _state;

    /// <summary>The message monitor used to intercept Win32 window messages before they reach the window procedure.</summary>
    private readonly WindowMessageMonitor _monitor;

    /// <summary>Indicates whether the WM_SHOWWINDOW message has fired and initial placement has been applied.</summary>
    private bool _isInitialized;

    /// <summary>
    ///     Suppresses WinUI's WM_DPICHANGED auto-resize behavior during the brief window when placement is being
    ///     restored.
    /// </summary>
    private bool _restoringPlacement;

    /// <summary>Indicates whether the manager has been disposed to prevent further event handling.</summary>
    private bool _isDisposed;

    // Win32 show-command constants supplementary to Win32Methods

    /// <summary>SW_SHOWNORMAL – activate and show in original size/position.</summary>
    private const uint SW_SHOWNORMAL = 1;

    /// <summary>SW_SHOWMINIMIZED – window is minimised (used to detect minimise state).</summary>
    private const uint SW_SHOWMINIMIZED = 2;

    /// <summary>
    ///     The separator character used to delimit fields in the serialized string.
    /// </summary>
    private const char DataSeparator = '|';

    /// <summary>
    ///     The current serialised window state, or <see langword="null" /> if no
    ///     valid state exists yet.  Serialisation is deferred to this getter so
    ///     no string allocation occurs during resize/move tracking.
    /// </summary>
    public string? Data => Serialize();

    /// <summary>
    ///     <see langword="true" /> if the monitor layout encoded in the loaded data
    ///     did not match the current monitor configuration – meaning the saved
    ///     placement was discarded and the window uses OS-default positioning.
    /// </summary>
    public bool MonitorLayoutChanged { get; }

    /// <summary>
    ///     Gets or sets a value indicating whether window geometry tracking is temporarily paused.
    /// </summary>
    public bool PauseTracking { get; set; }

    /// <summary>
    ///     Initialises the manager.
    /// </summary>
    /// <param name="window">The WinUI 3 window to manage.</param>
    /// <param name="serialisedData">
    ///     The opaque string previously received from <see cref="Data" />,
    ///     or <see langword="null" /> / empty to use OS-default placement.
    /// </param>
    public WindowSizeManager(Window window, string? serialisedData)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _hwnd = WindowNative.GetWindowHandle(_window);

        var (loadedState, loadedHash) = Deserialize(serialisedData);
        var currentHash = BuildMonitorLayoutHash();

        if (loadedState != null && loadedHash == currentHash)
        {
            _state = loadedState;
            MonitorLayoutChanged = false;
        }
        else
        {
            // Placement discarded – monitors differ (or no saved data).
            _state = null;
            MonitorLayoutChanged = loadedState != null; // true only when there WAS data
        }

        // WM_SHOWWINDOW fires before the first paint, so placement is applied
        // in one shot with no visible flicker.
        _monitor = new WindowMessageMonitor(_window);
        _monitor.WindowMessageReceived += OnWindowMessage;
        AppWindow.Changed += OnAppWindowChanged;
    }

    /// <summary>
    ///     Event handler for intercepted Win32 window messages.
    ///     Sets initial placement when the window is first shown and temporarily intercepts DPI changes during restoration.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The window message event arguments containing message details.</param>
    private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
    {
        // WM_SHOWWINDOW (0x0018), wParam == 1 → window is about to be shown.
        // This fires synchronously before the first paint, so we can set the
        // placement here and Windows sizes/positions the window correctly in
        // a single step — no visible restored-then-maximised flash.
        if (e.Message is { MessageId: 0x0018, WParam: 1 })
            if (!_isInitialized)
            {
                _isInitialized = true;
                ApplySavedPlacement();
            }

        // WM_DPICHANGED (0x0024): WinUI reacts to this by rescaling the window
        // size, which would undo the placement we just set via SetWindowPlacement.
        // Suppress it for the brief window during restore.
        if (e.Message.MessageId == 0x0024 && _restoringPlacement)
            e.Handled = true;
    }

    /// <summary>
    ///     Applies the saved window placement to the native window using Win32 API calls.
    /// </summary>
    private void ApplySavedPlacement()
    {
        if (_state is not { } state) return;

        Win32Methods.GetWindowPlacement(_hwnd, out var wp);
        wp.length = (uint)Marshal.SizeOf<Win32Methods.WINDOWPLACEMENT>();

        wp.rcNormalPosition = new Win32Methods.RECT
        {
            Left = state.X,
            Top = state.Y,
            Right = state.X + state.Width,
            Bottom = state.Y + state.Height
        };

        // Always restore as an overlapped window – never back into full-screen.
        wp.showCmd = state.IsMaximized
            ? Win32Methods.SW_SHOWMAXIMIZED
            : SW_SHOWNORMAL;

        // Bracket SetWindowPlacement with the flag so that WM_DPICHANGED fired
        // by moving the window to a different-DPI monitor is suppressed above.
        _restoringPlacement = true;
        Win32Methods.SetWindowPlacement(_hwnd, in wp);
        _restoringPlacement = false;
    }

    /// <summary>
    ///     Event handler triggered when the AppWindow's size, position, or presenter state changes.
    ///     Updates the authoritative state snapshot if tracking is active.
    /// </summary>
    /// <param name="sender">The AppWindow that triggered the event.</param>
    /// <param name="args">Arguments containing details about what changed.</param>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isDisposed) return;
        // Capture the new geometry if the size or position changed,
        // or if the presenter changed is overlapped
        if (args.DidSizeChange || args.DidPositionChange ||
            (args.DidPresenterChange && AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped))
            CaptureOverlappedGeometry();
    }

    /// <summary>
    ///     Reads the current <see cref="Win32Methods.WINDOWPLACEMENT" /> and updates
    ///     the internal state.  Only called while in the overlapped presenter.
    /// </summary>
    private void CaptureOverlappedGeometry()
    {
        if (PauseTracking) return;
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) return;
        if (!Win32Methods.GetWindowPlacement(_hwnd, out var wp)) return;

        var rc = wp.rcNormalPosition;
        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;

        // Ignore degenerate geometry (e.g. partially-minimised transitions).
        if (w <= 0 || h <= 0) return;

        // When the window is minimised the showCmd changes to SW_SHOWMINIMIZED.
        // In that case inherit the IsMaximized flag from the previous snapshot
        // so we don't accidentally clear it.
        bool isMaximized = wp.showCmd == Win32Methods.SW_SHOWMAXIMIZED
                           || (wp.showCmd == SW_SHOWMINIMIZED && _state?.IsMaximized == true);

        var newState = new WindowStateData(
            rc.Left,
            rc.Top,
            w,
            h,
            isMaximized);

        _state = newState;
    }

    /// <summary>
    ///     Serialises the current window state and monitor layout hash to a string.
    /// </summary>
    /// <returns>A serialized string representation, or null if no state exists.</returns>
    private string? Serialize()
    {
        if (_state is not { } state) return null;
        return string.Join(DataSeparator,
            state.X.ToString(CultureInfo.InvariantCulture),
            state.Y.ToString(CultureInfo.InvariantCulture),
            state.Width.ToString(CultureInfo.InvariantCulture),
            state.Height.ToString(CultureInfo.InvariantCulture),
            state.IsMaximized.ToString(CultureInfo.InvariantCulture),
            BuildMonitorLayoutHash());
    }

    /// <summary>
    ///     Parses a string previously produced by <see cref="Data" />.
    /// </summary>
    /// <param name="raw">The serialized string data to parse.</param>
    /// <returns>
    ///     A deserialized <see cref="WindowStateData" /> object and monitor hash, or <see langword="null" /> upon any
    ///     format error.
    /// </returns>
    private static (WindowStateData? State, string? MonitorHash) Deserialize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        try
        {
            var parts = raw.Split(DataSeparator);
            if (parts.Length < 6) return (null, null);

            var state = new WindowStateData(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture),
                int.Parse(parts[3], CultureInfo.InvariantCulture),
                bool.Parse(parts[4]));

            // Rejoin in case MonitorHash itself contained a DataSeparator — unlikely but safe
            var hash = string.Join(DataSeparator, parts[5..]);
            return (state, hash);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    ///     Builds a compact string that uniquely identifies the current physical
    ///     arrangement of all connected monitors (count, full bounds of each).
    /// </summary>
    /// <remarks>
    ///     Uses <see cref="DisplayArea.OuterBounds" /> (the full display rectangle,
    ///     equivalent to Win32 <c>MONITORINFO.rcMonitor</c>) rather than
    ///     <c>WorkArea</c>.  WorkArea excludes the taskbar, so a taskbar resize or
    ///     auto-hide toggle would change the hash and discard a still-valid saved
    ///     placement.  OuterBounds is stable against taskbar changes and only differs
    ///     when a monitor is physically connected, disconnected, repositioned, or
    ///     its resolution is changed — exactly the cases where we must not restore.
    /// </remarks>
    /// <returns>A string representing the outer bounds of all detected monitors.</returns>
    private static string BuildMonitorLayoutHash()
    {
        try
        {
            var allMonitors = DisplayArea.FindAll();

            // Fast path for the 90% case (single monitor)
            if (allMonitors.Count == 1)
            {
                var r = allMonitors[0].OuterBounds;
                return $"{r.X},{r.Y},{r.Width},{r.Height};";
            }

            var hash = string.Empty;
            // Do NOT use foreach / LINQ on this list – it can crash.
            // See github.com/microsoft/microsoft-ui-xaml/issues/6454
            for (int i = 0; i < allMonitors.Count; i++)
            {
                var r = allMonitors[i].OuterBounds;
                hash += $"{r.X},{r.Y},{r.Width},{r.Height};";
            }

            return hash;
        }
        catch { return string.Empty; }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _monitor.WindowMessageReceived -= OnWindowMessage;
        _monitor.Dispose();
        AppWindow.Changed -= OnAppWindowChanged;
    }

    // ---------------------------------------------------------------------------
    // FullScreen Related Functions
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     Tracks whether the window was in a maximized state before entering full-screen mode, to correctly re-maximize
    ///     it upon exit.
    /// </summary>
    private bool _wasMaximizedBeforeFullScreen = false;

    /// <summary>
    ///     Restores the window from a maximized or full-screen state to its normal overlapped (windowed) state.
    /// </summary>
    /// <param name="exitFullScreenButton">
    ///     An optional UI element (e.g., an 'Exit Full Screen' button) to collapse when
    ///     restoring from full-screen mode.
    /// </param>
    public void Restore(UIElement? exitFullScreenButton = null)
    {
        // Use .Kind (a plain enum) instead of `is OverlappedPresenter` / `is FullScreenPresenter`
        // type-pattern checks. The `is T` form goes through WinRT COM QueryInterface, which the
        // Release-build trimmer strips — causing the check to silently return false in Release.
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } op)
                op.Restore();
        }
        else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
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
    public void ToggleFullScreen(UIElement? exitFullScreenButton = null)
    {
        // Use .Kind (a plain enum) instead of `is OverlappedPresenter` / `is FullScreenPresenter`
        // type-pattern checks. The `is T` form goes through WinRT COM QueryInterface, which the
        // Release-build trimmer strips — causing the check to silently return false in Release.
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
        {
            PauseTracking = true;
            _wasMaximizedBeforeFullScreen = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            exitFullScreenButton?.Visibility = Visibility.Visible;
        }
        else if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            PauseTracking = true;
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
    /// <remarks>
    ///     This method applies the maximized window placement and includes a brief delay to ensure the
    ///     change is fully applied before any subsequent actions. This helps provide a smoother visual experience when
    ///     maximizing the window.
    /// </remarks>
    /// <param name="window">The window to maximize. This parameter cannot be null.</param>
    private static void NoFlickerMaximize(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        Win32Methods.GetWindowPlacement(hwnd, out var placement);
        placement.showCmd = Win32Methods.SW_SHOWMAXIMIZED;
        Win32Methods.SetWindowPlacement(hwnd, in placement);
    }
}