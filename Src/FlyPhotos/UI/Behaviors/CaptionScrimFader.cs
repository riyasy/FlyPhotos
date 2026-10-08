#nullable enable
using System;
using FlyPhotos.Infra.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FlyPhotos.UI.Behaviors;

/// <summary>
///     Fades in a gradient scrim behind the window caption buttons while the pointer is over it, so
///     the glyphs stay legible when a pure white or black photo area is panned underneath (#168).
///     The scrim is tinted to contrast with <see cref="GlyphColor"/>.
/// </summary>
/// <remarks>
///     XAML gets no pointer moves over the title bar drag region or the caption buttons, so once
///     shown the scrim stays up there. It hides when the pointer comes back down below the scrim,
///     or when a short poll finds the cursor outside the window. The fade itself is the scrim's XAML
///     <c>OpacityTransition</c>; this class only flips <see cref="UIElement.Opacity"/>.
/// </remarks>
internal sealed class CaptionScrimFader : IDisposable
{
    private readonly UIElement _rootElement;
    private readonly FrameworkElement _scrim;
    private readonly GradientStop _topStop;
    private readonly GradientStop _bottomStop;
    private readonly AppWindow _appWindow;
    private readonly PointerEventHandler _onPointerMoved;

    /// <summary>
    ///     Polls the cursor while the scrim is shown: leaving the window through the title bar
    ///     raises no XAML pointer event.
    /// </summary>
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _shown;

    /// <summary>
    ///     The caption glyph colour. The scrim takes the opposite colour, and is re-tinted
    ///     immediately, so a theme or backdrop change while the scrim is shown takes effect at once.
    /// </summary>
    internal Color GlyphColor
    {
        get;
        set
        {
            field = value;
            // Named stops rather than enumerating GradientStops: keeps WinRT collection marshaling out of AOT.
            var tint = value == Colors.Black ? Colors.White : Colors.Black;
            Retint(_topStop, tint);
            Retint(_bottomStop, tint);
        }
    }

    /// <summary>
    ///     Gets or sets whether the window is in full-screen mode. There are no native caption
    ///     buttons there, so the scrim is hidden and stays hidden until full screen ends.
    /// </summary>
    internal bool IsFullScreen
    {
        private get;
        set
        {
            field = value;
            if (value) SetShown(false);
        }
    }

    /// <param name="rootElement">Full-window element whose pointer moves are observed.</param>
    /// <param name="scrim">The scrim element; should start with Opacity 0. Its height is the hover zone.</param>
    /// <param name="topStop">Gradient stop at the top of the scrim.</param>
    /// <param name="bottomStop">Gradient stop at the bottom of the scrim.</param>
    /// <param name="appWindow">The window, for the cursor-left-the-window check.</param>
    /// <param name="glyphColor">Initial value for <see cref="GlyphColor"/>.</param>
    public CaptionScrimFader(UIElement rootElement, FrameworkElement scrim, GradientStop topStop, GradientStop bottomStop,
        AppWindow appWindow, Color glyphColor)
    {
        _rootElement = rootElement;
        _scrim = scrim;
        _topStop = topStop;
        _bottomStop = bottomStop;
        _appWindow = appWindow;
        GlyphColor = glyphColor;

        _onPointerMoved = OnPointerMoved;
        // handledEventsToo: the canvas or an overlay (e.g. the EXIF panel) may mark the move handled.
        _rootElement.AddHandler(UIElement.PointerMovedEvent, _onPointerMoved, true);
        _leaveTimer.Tick += LeaveTimer_Tick;
    }

    public void Dispose()
    {
        _rootElement.RemoveHandler(UIElement.PointerMovedEvent, _onPointerMoved);
        _leaveTimer.Stop();
        _leaveTimer.Tick -= LeaveTimer_Tick;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) =>
        // The hover zone is the scrim itself, so it never hides while the pointer is still over the gradient.
        SetShown(!IsFullScreen && e.GetCurrentPoint(_rootElement).Position.Y <= _scrim.ActualHeight);

    private void LeaveTimer_Tick(object? sender, object e)
    {
        // Physical pixels on both sides, so no DPI conversion.
        if (!Win32Methods.GetCursorPos(out var p)) return;
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        if (p.X < pos.X || p.X >= pos.X + size.Width || p.Y < pos.Y || p.Y >= pos.Y + size.Height)
            SetShown(false);
    }

    /// <summary>Sets the stop's RGB to <paramref name="tint"/>, keeping its alpha (the gradient's shape).</summary>
    private static void Retint(GradientStop stop, Color tint) =>
        stop.Color = Color.FromArgb(stop.Color.A, tint.R, tint.G, tint.B);

    private void SetShown(bool show)
    {
        if (show == _shown) return;
        _shown = show;
        if (show) _leaveTimer.Start();
        else _leaveTimer.Stop();
        _scrim.Opacity = show ? 1 : 0;
    }
}
