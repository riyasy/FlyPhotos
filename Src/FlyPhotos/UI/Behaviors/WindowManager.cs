#nullable enable
using FlyPhotos.Infra.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Globalization;
using System.Text;
using WinRT.Interop;
using WinUIEx.Messaging;

namespace FlyPhotos.UI.Behaviors;

// ---------------------------------------------------------------------------
// Internal data model  (hidden from external consumers)
// ---------------------------------------------------------------------------

/// <summary>
/// Internal snapshot of window placement + monitor-layout fingerprint.
/// Serialised to/from a compact pipe-delimited string so the caller never
/// needs to understand the structure.
/// </summary>
/// <param name="X">The X coordinate (left edge) of the window.</param>
/// <param name="Y">The Y coordinate (top edge) of the window.</param>
/// <param name="Width">The width of the window.</param>
/// <param name="Height">The height of the window.</param>
/// <param name="IsMaximized">Indicates whether the window is currently in a maximized state.</param>
/// <param name="MonitorLayoutHash">A hash string representing the physical layout and resolution of all connected monitors.</param>
/// <remarks>
/// Format: <c>X|Y|W|H|IsMaximized|MonitorHash</c>
/// Example: <c>100|200|1280|800|False|0,0,1920,1040;</c>
/// </remarks>
sealed record WindowStateData(
    int X,
    int Y,
    int Width,
    int Height,
    bool IsMaximized,
    string MonitorLayoutHash)
{
    /// <summary>
    /// The separator character used to delimit fields in the serialized string.
    /// </summary>
    private const char Sep = '|';

    /// <summary>Serialises this record to a compact string.</summary>
    /// <returns>A pipe-delimited serialized representation of the window state.</returns>
    public string Serialize()
    {
        return string.Join(Sep,
            X.ToString(CultureInfo.InvariantCulture),
            Y.ToString(CultureInfo.InvariantCulture),
            Width.ToString(CultureInfo.InvariantCulture),
            Height.ToString(CultureInfo.InvariantCulture),
            IsMaximized.ToString(CultureInfo.InvariantCulture),
            MonitorLayoutHash);
    }

    /// <summary>
    /// Parses a string produced by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="raw">The serialized string data to parse.</param>
    /// <returns>A deserialized <see cref="WindowStateData"/> object, or <see langword="null"/> on any format error.</returns>
    public static WindowStateData? Deserialize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var parts = raw.Split(Sep);
            if (parts.Length < 6) return null;

            return new WindowStateData(
                X: int.Parse(parts[0], CultureInfo.InvariantCulture),
                Y: int.Parse(parts[1], CultureInfo.InvariantCulture),
                Width: int.Parse(parts[2], CultureInfo.InvariantCulture),
                Height: int.Parse(parts[3], CultureInfo.InvariantCulture),
                IsMaximized: bool.Parse(parts[4]),
                // Rejoin in case MonitorHash itself contained a '|' — unlikely but safe
                MonitorLayoutHash: string.Join(Sep, parts[5..]));
        }
        catch
        {
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// WindowManager
// ---------------------------------------------------------------------------

/// <summary>
/// Manages saving and restoring a WinUI 3 window's placement (position, size,
/// maximized/restored state) across application launches, with automatic
/// monitor-layout change detection.
/// </summary>
/// <remarks>
/// <para>
/// The constructor accepts an opaque <see langword="string"/> that was
/// previously read from the <see cref="Data"/> property. The serialisation format
/// is an implementation detail; external callers only see flat strings.
/// </para>
/// <para>
/// After construction the manager listens for the <c>WM_SHOWWINDOW</c> message
/// using a <see cref="WinUIEx.Messaging.WindowMessageMonitor"/>. The saved
/// placement is applied synchronously before the first paint, eliminating
/// restored-to-maximized visual flicker.
/// </para>
/// <para>
/// Whenever the placement changes (move, resize), the internal state is updated.
/// Callers can retrieve the latest serialised string via the <see cref="Data"/>
/// property (typically during window close or deferral).
/// </para>
/// <para>
/// If the window was closed while in full-screen presenter mode the saved
/// string reflects the last known <em>overlapped</em> geometry so that on
/// relaunch the window always opens safely as a normal overlapped window.
/// </para>
/// <para>
/// If the connected monitors have changed (count, bounds, or resolution) since
/// <paramref name="serialisedData"/> was captured, the saved placement is safely
/// discarded to prevent off-screen windows.
/// </para>
/// </remarks>
public sealed class WindowManager : IDisposable
{
    // Fields

    /// <summary>The internal AppWindow instance being managed.</summary>
    private readonly AppWindow _appWindow;

    /// <summary>The native Win32 window handle (HWND).</summary>
    private readonly nint _hwnd;

    /// <summary>Current authoritative snapshot (always reflects overlapped geometry).</summary>
    private WindowStateData? _state;

    /// <summary>The message monitor used to intercept Win32 window messages before they reach the window procedure.</summary>
    private readonly WindowMessageMonitor _monitor;

    /// <summary>Indicates whether the WM_SHOWWINDOW message has fired and initial placement has been applied.</summary>
    private bool _isInitialized;

    /// <summary>Suppresses WinUI's WM_DPICHANGED auto-resize behavior during the brief window when placement is being restored.</summary>
    private bool _restoringPlacement;

    /// <summary>Indicates whether the manager has been disposed to prevent further event handling.</summary>
    private bool _isDisposed;

    // Win32 show-command constants supplementary to Win32Methods

    /// <summary>SW_SHOWNORMAL – activate and show in original size/position.</summary>
    private const uint SW_SHOWNORMAL = 1;

    /// <summary>SW_SHOWMINIMIZED – window is minimised (used to detect minimise state).</summary>
    private const uint SW_SHOWMINIMIZED = 2;

    /// <summary>
    /// The current serialised window state, or <see langword="null"/> if no
    /// valid state exists yet.  Serialisation is deferred to this getter so
    /// no string allocation occurs during resize/move tracking.
    /// </summary>
    public string? Data => _state?.Serialize();

    /// <summary>
    /// <see langword="true"/> if the monitor layout encoded in the loaded data
    /// did not match the current monitor configuration – meaning the saved
    /// placement was discarded and the window uses OS-default positioning.
    /// </summary>
    public bool MonitorLayoutChanged { get; }

    /// <summary>
    /// Gets or sets a value indicating whether window geometry tracking is temporarily paused.
    /// </summary>
    public bool PauseTracking { get; set; }

    /// <summary>
    /// Initialises the manager.
    /// </summary>
    /// <param name="window">The WinUI 3 window to manage.</param>
    /// <param name="serialisedData">
    ///   The opaque string previously received from <see cref="Data"/>,
    ///   or <see langword="null"/> / empty to use OS-default placement.
    /// </param>
    public WindowManager(Window window, string? serialisedData)
    {
        var window1 = window ?? throw new ArgumentNullException(nameof(window));
        _appWindow = window.AppWindow;
        _hwnd = WindowNative.GetWindowHandle(window);

        var loaded = WindowStateData.Deserialize(serialisedData);
        var currentHash = BuildMonitorLayoutHash();

        if (loaded != null && loaded.MonitorLayoutHash == currentHash)
        {
            _state = loaded;
            MonitorLayoutChanged = false;
        }
        else
        {
            // Placement discarded – monitors differ (or no saved data).
            _state = null;
            MonitorLayoutChanged = loaded != null; // true only when there WAS data
        }

        // WM_SHOWWINDOW fires before the first paint, so placement is applied
        // in one shot with no visible flicker.
        _monitor = new WindowMessageMonitor(window1);
        _monitor.WindowMessageReceived += OnWindowMessage;
        _appWindow.Changed += OnAppWindowChanged;
    }

    /// <summary>
    /// Event handler for intercepted Win32 window messages.
    /// Sets initial placement when the window is first shown and temporarily intercepts DPI changes during restoration.
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
        {
            if (!_isInitialized)
            {
                _isInitialized = true;
                ApplySavedPlacement();
            }
        }
        // WM_DPICHANGED (0x0024): WinUI reacts to this by rescaling the window
        // size, which would undo the placement we just set via SetWindowPlacement.
        // Suppress it for the brief window during restore.
        if (e.Message.MessageId == 0x0024 && _restoringPlacement)
            e.Handled = true;
    }

    /// <summary>
    /// Applies the saved window placement to the native window using Win32 API calls.
    /// </summary>
    private void ApplySavedPlacement()
    {
        if (_state == null) return;

        Win32Methods.GetWindowPlacement(_hwnd, out var wp);
        wp.length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Methods.WINDOWPLACEMENT>();

        wp.rcNormalPosition = new Win32Methods.RECT
        {
            Left = _state.X,
            Top = _state.Y,
            Right = _state.X + _state.Width,
            Bottom = _state.Y + _state.Height
        };

        // Always restore as an overlapped window – never back into full-screen.
        wp.showCmd = _state.IsMaximized
            ? Win32Methods.SW_SHOWMAXIMIZED
            : SW_SHOWNORMAL;

        // Bracket SetWindowPlacement with the flag so that WM_DPICHANGED fired
        // by moving the window to a different-DPI monitor is suppressed above.
        _restoringPlacement = true;
        Win32Methods.SetWindowPlacement(_hwnd, in wp);
        _restoringPlacement = false;
    }

    /// <summary>
    /// Event handler triggered when the AppWindow's size, position, or presenter state changes.
    /// Updates the authoritative state snapshot if tracking is active.
    /// </summary>
    /// <param name="sender">The AppWindow that triggered the event.</param>
    /// <param name="args">Arguments containing details about what changed.</param>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isDisposed) return;
        if (args is { DidPositionChange: false, DidSizeChange: false, DidPresenterChange: false }) return;

        if (args.DidPresenterChange)
            HandlePresenterChange();
        else
            CaptureOverlappedGeometry();
    }

    /// <summary>
    /// Handles transitions between different window presentation modes (e.g., Overlapped to FullScreen).
    /// Ensures we do not erroneously save full-screen dimensions as normal window bounds.
    /// </summary>
    private void HandlePresenterChange()
    {
        if (_appWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped)
        {
            // Returned to overlapped: capture fresh geometry.
            CaptureOverlappedGeometry();
        }
    }

    /// <summary>
    /// Reads the current <see cref="Win32Methods.WINDOWPLACEMENT"/> and updates
    /// the internal state.  Only called while in the overlapped presenter.
    /// </summary>
    private void CaptureOverlappedGeometry()
    {
        if (PauseTracking) return;
        if (_appWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) return;
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
            X: rc.Left,
            Y: rc.Top,
            Width: w,
            Height: h,
            IsMaximized: isMaximized,
            MonitorLayoutHash: BuildMonitorLayoutHash());

        _state = newState;
    }

    /// <summary>
    /// Builds a compact string that uniquely identifies the current physical
    /// arrangement of all connected monitors (count, full bounds of each).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="DisplayArea.OuterBounds"/> (the full display rectangle,
    /// equivalent to Win32 <c>MONITORINFO.rcMonitor</c>) rather than
    /// <c>WorkArea</c>.  WorkArea excludes the taskbar, so a taskbar resize or
    /// auto-hide toggle would change the hash and discard a still-valid saved
    /// placement.  OuterBounds is stable against taskbar changes and only differs
    /// when a monitor is physically connected, disconnected, repositioned, or
    /// its resolution is changed — exactly the cases where we must not restore.
    /// </remarks>
    /// <returns>A string representing the outer bounds of all detected monitors.</returns>
    private static string BuildMonitorLayoutHash()
    {
        try
        {
            var allMonitors = DisplayArea.FindAll();
            var sb = new StringBuilder();

            // Do NOT use foreach / LINQ on this list – it can crash.
            // See github.com/microsoft/microsoft-ui-xaml/issues/6454
            for (int i = 0; i < allMonitors.Count; i++)
            {
                var r = allMonitors[i].OuterBounds;
                sb.Append($"{r.X},{r.Y},{r.Width},{r.Height};");
            }
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _monitor.WindowMessageReceived -= OnWindowMessage;
        _monitor.Dispose();
        _appWindow.Changed -= OnAppWindowChanged;
        GC.SuppressFinalize(this);
    }
}