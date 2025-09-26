#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using FlyPhotos.AppSettings;
using FlyPhotos.Controllers;
using FlyPhotos.Data;
using FlyPhotos.FlyNativeLibWrapper;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NLog;
using WinRT;
using WinRT.Interop;
using WinUIEx;
using Icon = System.Drawing.Icon;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.Views;


/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class PhotoDisplayWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasController _canvasController;
    private readonly ThumbNailController _thumbNailController;
    private readonly PhotoDisplayController _photoController;

    private Settings? _settingWindow;

    private readonly DispatcherTimer _repeatButtonReleaseCheckTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 100)
    };

    private readonly DispatcherTimer _wheelScrollBrakeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };

    private readonly TransparentTintBackdrop _transparentTintBackdrop = new()
    {
        TintColor = Color.FromArgb((byte)(((100 - AppConfig.Settings.TransparentBackgroundIntensity) * 255) / 100), 0, 0, 0)
    };
    private readonly BlurredBackdrop _frozenBackdrop = new();

    private OverlappedPresenterState _lastWindowState;

    private readonly VirtualKey _plusVk = Util.GetKeyThatProduces('+');
    private readonly VirtualKey _minusVk = Util.GetKeyThatProduces('-');

    private readonly OpacityFader _opacityFader;
    private readonly InactivityFader _inactivityFader;

    private WindowBackdropType _currentBackdropType;
    private ISystemBackdropControllerWithTargets? _backdropController;
    private readonly SystemBackdropConfiguration _configurationSource;


    public PhotoDisplayWindow(string firstPhotoPath)
    {
        InitializeComponent();

        Title = "Fly Photos";
        if (!PathResolver.IsPackagedApp)
            SetUnpackagedAppIcon();

        SetupTransparentTitleBar();

        //(AppWindow.Presenter as OverlappedPresenter)?.SetBorderAndTitleBar(false, false);

        _configurationSource = new SystemBackdropConfiguration
        {
            IsHighContrast = ThemeSettings.CreateForWindowId(this.AppWindow.Id).HighContrast,
            IsInputActive = true
        };


        ((FrameworkElement)Content).ActualThemeChanged += PhotoDisplayWindow_ActualThemeChanged;
        SetConfigurationSourceTheme();
        SetWindowBackground(AppConfig.Settings.WindowBackdrop);
        SetWindowTheme(AppConfig.Settings.Theme);
        DispatcherQueue.EnsureSystemDispatcherQueue();

        var photoSessionState = new PhotoSessionState() { FirstPhotoPath = firstPhotoPath };
        _thumbNailController = new ThumbNailController(D2dCanvasThumbNail, photoSessionState);
        _canvasController = new CanvasController(D2dCanvas, _thumbNailController, photoSessionState);
        _photoController = new PhotoDisplayController(D2dCanvas, _canvasController, _thumbNailController, photoSessionState);
        _photoController.StatusUpdated += PhotoController_StatusUpdated;
        _canvasController.OnZoomChanged += CanvasController_OnZoomChanged;

        TxtFileName.Text = Path.GetFileName(firstPhotoPath);

        Activated += PhotoDisplayWindow_Activated;
        SizeChanged += PhotoDisplayWindow_SizeChanged;
        AppWindow.Closing += PhotoDisplayWindow_Closing;
        Closed += PhotoDisplayWindow_Closed;

        D2dCanvas.CreateResources += D2dCanvas_CreateResources;
        D2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        D2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;
        D2dCanvasThumbNail.PointerWheelChanged += D2dCanvasThumbNail_PointerWheelChanged;
        _thumbNailController.ThumbnailClicked += ThumbNailController_ThumbnailClicked;

        MainLayout.KeyDown += HandleKeyDown;
        MainLayout.KeyUp += HandleKeyUp;

        _repeatButtonReleaseCheckTimer.Tick += RepeatButtonReleaseCheckTimer_Tick;
        _wheelScrollBrakeTimer.Tick += WheelScrollBrakeTimer_Tick;

        //this.Maximize(); // Maximise will be called from App.xaml.cs
        _lastWindowState = OverlappedPresenterState.Maximized;

        _opacityFader = new OpacityFader([BorderButtonPanel, D2dCanvasThumbNail, BorderTxtFileName], MainLayout);
        _inactivityFader = new InactivityFader(BorderTxtZoom);
    }

    private async void ThumbNailController_ThumbnailClicked(int shiftIndex)
    {
        await _photoController.FlyBy(shiftIndex);
    }

    private async void ButtonBackNext_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        await _photoController.Fly(delta > 0 ? NavDirection.Prev : NavDirection.Next);
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Start();
    }

    private void ButtonRotate_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.RotateCurrentPhotoBy90(delta > 0);
    }

    private void PhotoController_StatusUpdated(object? sender, StatusUpdateEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TxtFileName.Text = e.IndexAndFileName;
            CacheStatusProgress.Text = e.CacheProgressStatus;
        });
    }

    private void CanvasController_OnZoomChanged(int zoomPercent)
    {
        TxtZoom.Text = $"{zoomPercent}%";
        _inactivityFader.ReportActivity();
    }

    private void PhotoDisplayWindow_SizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args)
    {
        var presenter = (AppWindow.Presenter as OverlappedPresenter);
        if (presenter != null && presenter.State != _lastWindowState)
        {
            _lastWindowState = presenter.State;
        }
    }

    private void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var dpiAdjustedPosition = e.GetCurrentPoint(D2dCanvas).Position.AdjustForDpi(D2dCanvas);
        if (_canvasController.IsPressedOnImage(dpiAdjustedPosition))
        {
            var properties = e.GetCurrentPoint(D2dCanvas).Properties;
            if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased)
            {
                var filePath = _photoController.GetFullPathCurrentFile();
                if (File.Exists(filePath))
                {
                    NativeMethods.GetCursorPos(out NativeMethods.POINT mousePosScreen);
                    CliWrapper.ShowContextMenu(this, filePath, mousePosScreen.X, mousePosScreen.Y);
                }
            }
        }
        else
        {
            var pointerY = e.GetCurrentPoint(D2dCanvas).Position.Y;
            if (_lastWindowState == OverlappedPresenterState.Maximized && pointerY >= AppTitlebar.ActualHeight)
            {
                this.Restore();
            }
        }
    }

    private async void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;

        if (Util.IsControlPressed()) return;

        if (Util.IsAltPressed() || AppConfig.Settings.DefaultMouseWheelBehavior == DefaultMouseWheelBehavior.Navigate)
        {
            var delta = e.GetCurrentPoint(D2dCanvas).Properties.MouseWheelDelta;
            await _photoController.Fly(delta > 0 ? NavDirection.Prev : NavDirection.Next);
            _wheelScrollBrakeTimer.Stop();
            _wheelScrollBrakeTimer.Start();
        }
    }

    private async void D2dCanvasThumbNail_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(D2dCanvasThumbNail).Properties.MouseWheelDelta;
        await _photoController.Fly(delta > 0 ? NavDirection.Prev : NavDirection.Next);
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Start();
    }

    private void SetupTransparentTitleBar()
    {
        SetTitleBar(AppTitlebar);
        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Gray;
    }

    private void SetUnpackagedAppIcon()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var processModule = Process.GetCurrentProcess().MainModule;
        if (processModule == null) return;
        var sExe = processModule.FileName;
        var ico = Icon.ExtractAssociatedIcon(sExe);
        if (ico == null) return;
        NativeMethods.SendMessage(hWnd, NativeMethods.WM_SETICON, new IntPtr(1), ico.Handle);
    }

    private void D2dCanvas_CreateResources(CanvasControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        //args.TrackAsyncAction(_photoController.LoadFirstPhoto().AsAsyncAction());
        _ = _photoController.LoadFirstPhoto();
    }

    private async void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            bool ctrlPressed = Util.IsControlPressed();
            switch (e.Key)
            {
                case VirtualKey.C when ctrlPressed:
                    await _photoController.CopyFileToClipboardAsync();
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                    await AnimatePhotoDisplayWindowClose();
                    break;

                // File Navigation
                case VirtualKey.Right when !ctrlPressed:
                    await _photoController.Fly(NavDirection.Next);
                    break;
                case VirtualKey.Left when !ctrlPressed:
                    await _photoController.Fly(NavDirection.Prev);
                    break;

                // ZOOM
                case VirtualKey.Add when ctrlPressed:
                    _canvasController.ZoomByKeyboard(ZoomDirection.In);
                    break;
                case VirtualKey.Subtract when ctrlPressed:
                    _canvasController.ZoomByKeyboard(ZoomDirection.Out);
                    break;

                // PAN
                case VirtualKey.Up when ctrlPressed:
                    _canvasController.PanByKeyboard(0, -20);
                    break;
                case VirtualKey.Down when ctrlPressed:
                    _canvasController.PanByKeyboard(0, 20);
                    break;
                case VirtualKey.Left when ctrlPressed:
                    _canvasController.PanByKeyboard(-20, 0);
                    break;
                case VirtualKey.Right when ctrlPressed:
                    _canvasController.PanByKeyboard(20, 0);
                    break;
            }

            // Layout-aware override
            if (ctrlPressed && e.Key == _plusVk)
                _canvasController.ZoomByKeyboard(ZoomDirection.In);
            else if (ctrlPressed && e.Key == _minusVk)
                _canvasController.ZoomByKeyboard(ZoomDirection.Out);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private async void HandleKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Right or VirtualKey.Left)) return;
        await _photoController.Brake();
    }

    private async void ButtonBack_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        await _photoController.Fly(NavDirection.Prev);
        _repeatButtonReleaseCheckTimer.Stop();
        _repeatButtonReleaseCheckTimer.Start();
    }

    private async void ButtonNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        await _photoController.Fly(NavDirection.Next);
        _repeatButtonReleaseCheckTimer.Stop();
        _repeatButtonReleaseCheckTimer.Start();
    }

    private async void RepeatButtonReleaseCheckTimer_Tick(object? sender, object e)
    {
        if (ButtonBack.IsPressed || ButtonNext.IsPressed) return;
        _repeatButtonReleaseCheckTimer.Stop();
        await _photoController.Brake();
    }

    private async void WheelScrollBrakeTimer_Tick(object? sender, object e)
    {
        _wheelScrollBrakeTimer.Stop();
        await _photoController.Brake();
    }

    private void ButtonRotate_OnClick(object sender, RoutedEventArgs e)
    {
        _canvasController.RotateCurrentPhotoBy90(true);
    }

    private void ButtonSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingWindow == null)
        {
            _settingWindow = new Settings(_thumbNailController);
            _settingWindow.SetWindowSize(1024, 768);
            _settingWindow.Closed += SettingWindow_Closed;
            _settingWindow.Activate();
            _settingWindow.ShowCheckeredBackgroundChanged += SettingWindow_ShowCheckeredBackgroundChanged;
            _settingWindow.BackDropTransparencyChanged += SettingWindow_BackdropTransparencyChanged;
            _settingWindow.ThemeChanged += SetWindowTheme;
            _settingWindow.BackdropChanged += SetWindowBackground;
        }
        else
        {
            _settingWindow.Activate();
        }
    }

    private async void PhotoDisplayWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        await AnimatePhotoDisplayWindowClose();
    }

    private async Task AnimatePhotoDisplayWindowClose()
    {
        _settingWindow?.Close();

        if (AppConfig.Settings.OpenExitZoom)
        {
            _canvasController.ZoomOutOnExit(Constants.PanZoomAnimationDurationForExit);
            await Task.Delay(Constants.PanZoomAnimationDurationForExit);
        }
        this.Hide();
        this.Close();
    }

    private async void PhotoDisplayWindow_Closed(object sender, WindowEventArgs args)
    {
        _repeatButtonReleaseCheckTimer.Stop();
        _wheelScrollBrakeTimer.Stop();
        _thumbNailController.ThumbnailClicked -= ThumbNailController_ThumbnailClicked;
        _photoController.StatusUpdated -= PhotoController_StatusUpdated;
        _canvasController.OnZoomChanged -= CanvasController_OnZoomChanged;
        ((FrameworkElement)Content).ActualThemeChanged -= PhotoDisplayWindow_ActualThemeChanged;

        await _canvasController.DisposeAsync();
        _thumbNailController.Dispose();
        _photoController.Dispose();
        _opacityFader.Dispose();
        _inactivityFader.Dispose();
        DiskCacherWithSqlite.Shutdown();

        _backdropController?.RemoveSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _backdropController?.Dispose();
        _backdropController = null;
    }

    private void SettingWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingWindow != null)
        {
            _settingWindow.Closed -= SettingWindow_Closed;
            _settingWindow.ThemeChanged -= SetWindowTheme;
            _settingWindow.BackdropChanged -= SetWindowBackground;
            _settingWindow.ShowCheckeredBackgroundChanged -= SettingWindow_ShowCheckeredBackgroundChanged;
            _settingWindow.BackDropTransparencyChanged -= SettingWindow_BackdropTransparencyChanged;
        }

        _settingWindow = null;
    }

    private void SettingWindow_ShowCheckeredBackgroundChanged(bool showChecker)
    {
        _canvasController.HandleCheckeredBackgroundChange(showChecker);
    }
    private void SettingWindow_BackdropTransparencyChanged(int obj)
    {
        if (_currentBackdropType == WindowBackdropType.Transparent)
        {
            _transparentTintBackdrop.TintColor = Color.FromArgb((byte)(((100 - obj) * 255) / 100), 0, 0, 0);
            SystemBackdrop = _transparentTintBackdrop;
        }
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
        _canvasController.FitToScreen(true);
    }

    private void ButtonOneIsToOne_Click(object sender, RoutedEventArgs e)
    {
        _canvasController.ZoomToHundred();
    }

    private void SetWindowBackground(WindowBackdropType backdropType)
    {
        _currentBackdropType = backdropType;
        if (_backdropController != null)
        {
            _backdropController.RemoveSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _backdropController = null;
        }
        this.SystemBackdrop = null;

        switch (backdropType)
        {
            case WindowBackdropType.Transparent:
                SystemBackdrop = _transparentTintBackdrop;
                break;

            case WindowBackdropType.Acrylic:
                TrySetAcrylicBackdrop(false);
                break;

            case WindowBackdropType.AcrylicThin:
                TrySetAcrylicBackdrop(true);
                break;

            case WindowBackdropType.Mica:
                TrySetMicaBackdrop(false);
                break;

            case WindowBackdropType.MicaAlt:
                TrySetMicaBackdrop(true);
                break;

            case WindowBackdropType.Frozen:
                SystemBackdrop = _frozenBackdrop;
                break;

            case WindowBackdropType.None:
            default:
                break;
        }
        SetBackColorAsPerThemeAndBackdrop();
    }

    bool TrySetMicaBackdrop(bool useMicaAlt)
    {
        if (!MicaController.IsSupported()) return false;
        var micaController = new MicaController { Kind = useMicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
        micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        micaController.SetSystemBackdropConfiguration(_configurationSource);
        _backdropController = micaController;
        return true;
    }

    bool TrySetAcrylicBackdrop(bool useAcrylicThin)
    {
        if (!DesktopAcrylicController.IsSupported()) return false;
        var acrylicController = new DesktopAcrylicController
        { Kind = useAcrylicThin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base };
        acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        acrylicController.SetSystemBackdropConfiguration(_configurationSource);
        _backdropController = acrylicController;
        return true;
    }

    private void PhotoDisplayWindow_ActualThemeChanged(FrameworkElement sender, object args)
    {
        SetConfigurationSourceTheme();
        SetBackColorAsPerThemeAndBackdrop();
    }

    private void PhotoDisplayWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
    }

    private void SetConfigurationSourceTheme()
    {
        _configurationSource.IsHighContrast = ThemeSettings.CreateForWindowId(this.AppWindow.Id).HighContrast;
        _configurationSource.Theme = (SystemBackdropTheme)((FrameworkElement)Content).ActualTheme;
    }

    private void SetWindowTheme(ElementTheme theme)
    {
        ((FrameworkElement)Content).RequestedTheme = theme;
    }

    void SetBackColorAsPerThemeAndBackdrop()
    {
        var actualTheme = ((FrameworkElement)Content).ActualTheme;

        // Determine colors first
        var gridColor = _currentBackdropType == WindowBackdropType.None
            ? (actualTheme == ElementTheme.Light ? Colors.White : Colors.Black)
            : Colors.Transparent;

        var buttonPanelColor = actualTheme == ElementTheme.Light
            ? Colors.Transparent
            : Color.FromArgb(0x44, 0, 0, 0);

        var fileNameBackgroundColor = actualTheme == ElementTheme.Light
            ? Color.FromArgb(0x44, 255, 255, 255)
            : Color.FromArgb(0x44, 0, 0, 0);

        // Assign brushes
        ((Grid)Content).Background = new SolidColorBrush(gridColor);
        BorderButtonPanel.Background = new SolidColorBrush(buttonPanelColor);
        BorderTxtFileName.Background = new SolidColorBrush(fileNameBackgroundColor);
    }
}

public partial class BlurredBackdrop : CompositionBrushBackdrop
{
    protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
    {
        return compositor.CreateHostBackdropBrush();
    }
}