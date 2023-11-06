#nullable enable
using FlyPhotos.Controllers;
using FlyPhotos.Transparency;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NLog;
using System;
using System.IO;
using Vanara.PInvoke;
using Windows.System;
using WinRT.Interop;
using static FlyPhotos.Controllers.PhotoDisplayController;
using static Vanara.PInvoke.User32;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class PhotoDisplayWindow
{
    private enum WindowStyle
    {
        Transparent,
        Acrylic,
        Mica,
        MicaAlt
    }

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly Win2dCanvasController _canvasController;
    private readonly PhotoDisplayController _photoController;

    private bool _currentlyFullScreen = true;
    private bool _screenResizingInProgress;
    private readonly AppWindow _appWindow;

    private WindowStyle _currentWindowStyle = WindowStyle.Transparent;

    private readonly DispatcherTimer _repeatButtonReleaseCheckTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 100)
    };

    private ComCtl32.SUBCLASSPROC? _wndProcHandler;

    public PhotoDisplayWindow()
    {
        InitializeComponent();
        SetUnpackagedAppIcon();
        _appWindow = GetAppWindowForCurrentWindow();
        SetupRealTransparency();

        _canvasController = new Win2dCanvasController(MainLayout, D2dCanvas);
        _photoController = new PhotoDisplayController(_canvasController, D2dCanvas, UpdateStatus);
        TxtFileName.Text = Path.GetFileName(App.SelectedFileName);

        _appWindow.Changed += AppWindow_Changed;
        D2dCanvas.CreateResources += D2dCanvas_CreateResources;
        D2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        MainLayout.KeyDown += HandleKeyDown;
        MainLayout.KeyUp += HandleKeyUp;
        _repeatButtonReleaseCheckTimer.Tick += RepeatButtonReleaseCheckTimer_Tick;

        var overLappedPresenter = _appWindow.Presenter as OverlappedPresenter;
        if (overLappedPresenter == null) return;
        overLappedPresenter.Maximize();
        overLappedPresenter.SetBorderAndTitleBar(false, false);
    }

    public void UpdateStatus(string currentFileName, string currentCacheStatus)
    {
        TxtFileName.Text = currentFileName;
        CacheStatusProgress.Text = currentCacheStatus;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_screenResizingInProgress) return;
        if (!args.DidSizeChange) return;
        var overLappedPresenter = _appWindow.Presenter as OverlappedPresenter;
        if (!_currentlyFullScreen && overLappedPresenter != null &&
            overLappedPresenter.State == OverlappedPresenterState.Maximized)
        {
            _screenResizingInProgress = true;
            overLappedPresenter.SetBorderAndTitleBar(false, false);
            _currentlyFullScreen = true;
            _screenResizingInProgress = false;
        }
    }

    private void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_screenResizingInProgress) return;
        if (_canvasController.IsPressedOnImage(e.GetCurrentPoint(D2dCanvas).Position)) return;
        if (!_currentlyFullScreen) return;
        _screenResizingInProgress = true;
        var overLappedPresenter = _appWindow.Presenter as OverlappedPresenter;
        if (overLappedPresenter != null)
        {
            overLappedPresenter.SetBorderAndTitleBar(true, true);
            overLappedPresenter.Restore();
        }

        _currentlyFullScreen = false;
        _screenResizingInProgress = false;
    }

    private void SetupRealTransparency()
    {
        SetTitleBar(AppTitlebar);
        var titleBar = _appWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Transparent;

        var windowHandle = new IntPtr((long)AppWindow.Id.Value);
        DwmApi.DwmExtendFrameIntoClientArea(windowHandle, new DwmApi.MARGINS(0));
        using var rgn = Gdi32.CreateRectRgn(-2, -2, -1, -1);
        DwmApi.DwmEnableBlurBehindWindow(windowHandle, new DwmApi.DWM_BLURBEHIND(true)
        {
            dwFlags = DwmApi.DWM_BLURBEHIND_Mask.DWM_BB_ENABLE | DwmApi.DWM_BLURBEHIND_Mask.DWM_BB_BLURREGION,
            hRgnBlur = rgn
        });
        TransparentHelper.SetTransparent(this, true);
        _wndProcHandler = WndProc;
        ComCtl32.SetWindowSubclass(windowHandle, _wndProcHandler, 1, IntPtr.Zero);
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(myWndId);
    }

    private static IntPtr WndProc(HWND hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass,
        IntPtr dwRefData)
    {
        if (uMsg == (uint)WindowMessage.WM_ERASEBKGND)
        {
            if (GetClientRect(hWnd, out var rect))
            {
                using var brush = Gdi32.CreateSolidBrush(new COLORREF(0, 0, 0));
                FillRect(wParam, rect, brush);
                return new IntPtr(1);
            }
        }
        else if (uMsg == (uint)WindowMessage.WM_DWMCOMPOSITIONCHANGED)
        {
            DwmApi.DwmExtendFrameIntoClientArea(hWnd, new DwmApi.MARGINS(0));
            using var rgn = Gdi32.CreateRectRgn(-2, -2, -1, -1);
            DwmApi.DwmEnableBlurBehindWindow(hWnd, new DwmApi.DWM_BLURBEHIND(true)
            {
                dwFlags = DwmApi.DWM_BLURBEHIND_Mask.DWM_BB_ENABLE | DwmApi.DWM_BLURBEHIND_Mask.DWM_BB_BLURREGION,
                hRgnBlur = rgn
            });

            return IntPtr.Zero;
        }

        return ComCtl32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void SetUnpackagedAppIcon()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var sExe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        var ico = System.Drawing.Icon.ExtractAssociatedIcon(sExe);
        SendMessage(hWnd, WindowMessage.WM_SETICON, 1, ico.Handle);
    }

    private void D2dCanvas_CreateResources(CanvasControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        args.TrackAsyncAction(_photoController.LoadFirstPhoto().AsAsyncAction());
    }

    private void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    Close();
                    break;
                case VirtualKey.Right:
                    _photoController.Fly(NavDirection.Next);
                    break;
                case VirtualKey.Left:
                    _photoController.Fly(NavDirection.Prev);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void HandleKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Right or VirtualKey.Left)) return;
        _photoController.Brake();
    }

    private void ButtonBack_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        _photoController.Fly(NavDirection.Prev);
        _repeatButtonReleaseCheckTimer.Start();
    }

    private void ButtonNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        _photoController.Fly(NavDirection.Next);
        _repeatButtonReleaseCheckTimer.Start();
    }

    private void RepeatButtonReleaseCheckTimer_Tick(object? sender, object e)
    {
        if (ButtonBack.IsPressed || ButtonNext.IsPressed) return;
        _repeatButtonReleaseCheckTimer.Stop();
        _photoController.Brake();
    }

    private void ButtonRotate_OnClick(object sender, RoutedEventArgs e)
    {
        _canvasController.RotateCurrentPhotoBy90();
    }

    private void ButtonHelp_OnClick(object sender, RoutedEventArgs e)
    {
    }

    private void ButtonSettings_OnClick(object sender, RoutedEventArgs e)
    {
    }

    private void ButtonCoffee_OnClick(object sender, RoutedEventArgs e)
    {
    }

    private void MenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        var flyOut = sender as MenuFlyoutItem;
        if (flyOut == null) return;

        if (_currentWindowStyle != WindowStyle.Transparent && flyOut.Text == "Transparent")
        {
            TransparentHelper.SetTransparent(this, true);
            DarkBg.Visibility = Visibility.Visible;
        }
        else if (_currentWindowStyle == WindowStyle.Transparent && flyOut.Text != "Transparent")
        {
            TransparentHelper.SetTransparent(this, false);
            DarkBg.Visibility = Visibility.Collapsed;
        }

        switch (flyOut.Text)
        {
            case "Transparent":
            {
                _currentWindowStyle = WindowStyle.Transparent;
                break;
            }
            case "Acrylic":
            {
                _currentWindowStyle = WindowStyle.Acrylic;
                TrySetDesktopAcrylicBackdrop();
                break;
            }
            case "Mica":
            {
                _currentWindowStyle = WindowStyle.Mica;
                TrySetMicaBackdrop(false);
                break;
            }
            case "Mica Alt":
            {
                _currentWindowStyle = WindowStyle.MicaAlt;
                TrySetMicaBackdrop(true);
                break;
            }
        }
    }

    private bool TrySetMicaBackdrop(bool useMicaAlt)
    {
        if (!MicaController.IsSupported()) return false; // Mica is not supported on this system.
        MicaBackdrop micaBackdrop = new()
        {
            Kind = useMicaAlt ? MicaKind.BaseAlt : MicaKind.Base
        };
        SystemBackdrop = micaBackdrop;
        return true; // Succeeded.
    }

    private bool TrySetDesktopAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return false; // DesktopAcrylic is not supported on this system.
        var desktopAcrylicBackdrop = new DesktopAcrylicBackdrop();
        SystemBackdrop = desktopAcrylicBackdrop;
        return true; // Succeeded.
    }

    private void ButtonExpander_Click(object sender, RoutedEventArgs e)
    {
        if (IconExpander.Text == ((char)0xE761).ToString())
        {
            IconExpander.Text = ((char)0xE760).ToString();
            CacheStatusProgress.Visibility = Visibility.Visible;
        }
        else
        {
            IconExpander.Text = ((char)0xE761).ToString();
            CacheStatusProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ButtonScaleSet_Click(object sender, RoutedEventArgs e)
    {
        _canvasController.SetHundredPercent();
    }
}