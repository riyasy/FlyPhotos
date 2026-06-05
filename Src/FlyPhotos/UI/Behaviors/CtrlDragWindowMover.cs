using System;
using System.Runtime.InteropServices;
using FlyPhotos.Infra.Utils;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace FlyPhotos.UI.Behaviors;

public sealed partial class CtrlDragWindowMover : IDisposable
{
    private readonly UIElement _canvas;
    private readonly AppWindow _appWindow;

    private bool _enabled;
    private bool _isActive;
    private PointInt32 _startWindowPos;
    private POINT _startCursorPos;
    private bool _disposed;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public CtrlDragWindowMover(UIElement canvas, AppWindow appWindow, bool enabled)
    {
        _canvas = canvas;
        _appWindow = appWindow;
        Enabled = enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (_enabled)
            {
                _canvas.PointerPressed  += OnPointerPressed;
                _canvas.PointerMoved    += OnPointerMoved;
                _canvas.PointerReleased += OnPointerReleased;
            }
            else
            {
                if (_isActive) CancelDrag();
                _canvas.PointerPressed  -= OnPointerPressed;
                _canvas.PointerMoved    -= OnPointerMoved;
                _canvas.PointerReleased -= OnPointerReleased;
            }
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!Util.IsControlPressed()) return;
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        // The `{ State: Restored }` member read roots the OverlappedPresenter projection so this cast
        // resolves under NativeAOT + Release; a memberless `is/as OverlappedPresenter` can silently
        // return null there (Debug/non-AOT are fine). Refs: CsWinRT#1930, microsoft-ui-xaml#10471.
        if (_appWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        GetCursorPos(out _startCursorPos);
        _startWindowPos = _appWindow.Position;
        _isActive = true;
        _canvas.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isActive) return;
        GetCursorPos(out var cur);
        _appWindow.Move(new PointInt32(
            _startWindowPos.X + (cur.X - _startCursorPos.X),
            _startWindowPos.Y + (cur.Y - _startCursorPos.Y)));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isActive) return;
        _isActive = false;
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    private void CancelDrag() => _isActive = false;

    public void Dispose()
    {
        if (_disposed) return;
        Enabled = false;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
