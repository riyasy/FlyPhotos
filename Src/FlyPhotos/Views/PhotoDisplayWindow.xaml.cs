#nullable enable
using CliWrapper;
using FlyPhotos.Controllers;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NLog;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;
using WinUIEx;
using static FlyPhotos.Controllers.PhotoDisplayController;
using static Vanara.PInvoke.User32;
using Icon = System.Drawing.Icon;
using Window = Microsoft.UI.Xaml.Window;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class PhotoDisplayWindow : IBackGroundChangeable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly Win2dCanvasController _canvasController;
    private readonly ThumbNailController _thumbNailController;
    private readonly PhotoDisplayController _photoController;

    private Window? _settingWindow;

    private readonly DispatcherTimer _repeatButtonReleaseCheckTimer = new()
    {
        Interval = new TimeSpan(0, 0, 0, 0, 100)
    };

    private readonly TransparentTintBackdrop _transparentTintBackdrop = new() { TintColor = Color.FromArgb(0xAA, 0, 0, 0) };
    private readonly BlurredBackdrop _frozenBackdrop = new();

    private OverlappedPresenterState _lastWindowState;

    private readonly VirtualKey PlusVK = Utils.Util.GetKeyThatProduces('+');
    private readonly VirtualKey MinusVK = Utils.Util.GetKeyThatProduces('-');

    private OpacityFader _opacityFader;
    private bool _pointerInBottom = false;

    public PhotoDisplayWindow()
    {
        InitializeComponent();

        Title = "Fly Photos";
        SetUnpackagedAppIcon();
        SetupTransparentTitleBar();
        SetWindowBackground(App.Settings.WindowBackGround);
        //(AppWindow.Presenter as OverlappedPresenter)?.SetBorderAndTitleBar(false, false);

        _thumbNailController = new ThumbNailController(D2dCanvasThumbNail);
        _canvasController = new Win2dCanvasController(D2dCanvas, _thumbNailController);
        _photoController = new PhotoDisplayController(D2dCanvas, UpdateStatus, _canvasController, _thumbNailController);
        

        TxtFileName.Text = Path.GetFileName(App.SelectedFileName);

        SizeChanged += PhotoDisplayWindow_SizeChanged;
        Closed += PhotoDisplayWindow_Closed;

        MainLayout.PointerMoved += MainLayout_PointerMoved;

        D2dCanvas.CreateResources += D2dCanvas_CreateResources;
        D2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        D2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;
        D2dCanvasThumbNail.PointerWheelChanged += D2dCanvasThumbNail_PointerWheelChanged;               

        MainLayout.KeyDown += HandleKeyDown;
        MainLayout.KeyUp += HandleKeyUp;

        _repeatButtonReleaseCheckTimer.Tick += RepeatButtonReleaseCheckTimer_Tick;

        this.Maximize();
        _lastWindowState = OverlappedPresenterState.Maximized;

        _opacityFader = new OpacityFader(new FrameworkElement[] { ButtonPanel, D2dCanvasThumbNail, TxtFileName }); 
    }

    private void MainLayout_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(MainLayout).Position;
        double windowHeight = this.Bounds.Height;

        double bottomThreshold = Math.Max(100, windowHeight * 0.40);
        bool pointerInBottom = pos.Y >= windowHeight - bottomThreshold;

        if (pointerInBottom != _pointerInBottom)
        {
            _pointerInBottom = pointerInBottom;
            if (pointerInBottom)
                _opacityFader.FadeTo(1.0f);    // Fully visible
            else
                _opacityFader.FadeTo(0.3f);    // Mostly transparent
        }
    }

    private void ButtonBackNext_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        if (delta > 0)
        {
            _photoController.Fly(NavDirection.Next);
            _repeatButtonReleaseCheckTimer.Start();
        }
        else
        {
            _photoController.Fly(NavDirection.Prev);
            _repeatButtonReleaseCheckTimer.Start();
        }
    }

    private void ButtonRotate_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.RotateCurrentPhotoBy90(delta > 0);
    }

    public void UpdateStatus(string currentFileName, string currentCacheStatus)
    {
        TxtFileName.Text = currentFileName;
        CacheStatusProgress.Text = currentCacheStatus;
    }

    private void PhotoDisplayWindow_SizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs args)
    {
        var presenter = (AppWindow.Presenter as OverlappedPresenter);
        if (presenter != null && _lastWindowState != presenter?.State)
        {
            _lastWindowState = presenter.State;
            //var timer = DispatcherQueue.CreateTimer();
            //timer.Interval = TimeSpan.FromMilliseconds(500);
            //timer.Tick += (_, _) =>
            //{
            //    if (presenter?.State == OverlappedPresenterState.Maximized)
            //    {
            //        presenter?.SetBorderAndTitleBar(false, false);
            //    }
            //    else if (presenter?.State == OverlappedPresenterState.Restored)
            //    {
            //        presenter?.SetBorderAndTitleBar(true, true);
            //    }
            //    timer.Stop();
            //};
            //timer.Start();
        }
    }

    private void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasController.IsPressedOnImage(e.GetCurrentPoint(D2dCanvas).Position))
        {
            var properties = e.GetCurrentPoint(D2dCanvas).Properties;
            if (properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased)
            {
                try
                {
                    var filePath = _photoController.GetFullPathCurrentFile();
                    if (File.Exists(filePath))
                    {
                        User32.GetCursorPos(out POINT mousePosScreen);
                        ManagedShellUtility.ShowContextMenu(filePath, mousePosScreen.X, mousePosScreen.Y);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Showing context menu failed");
                    Logger.Error(ex);
                }
            }
        }
        else
        {
            var pointerY = e.GetCurrentPoint(D2dCanvas).Position.Y;
            if (_lastWindowState == OverlappedPresenterState.Maximized && pointerY >= AppTitlebar.ActualHeight)
            {
                //(AppWindow.Presenter as OverlappedPresenter)?.SetBorderAndTitleBar(true, true);
                this.Restore();
            }
        }
    }

    private void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var coreWindow = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool isControlPressed = coreWindow.HasFlag(CoreVirtualKeyStates.Down);
        if (isControlPressed)
        {
            if (_photoController.IsSinglePhoto()) return;
            var delta = e.GetCurrentPoint(D2dCanvas).Properties.MouseWheelDelta;
            if (delta > 0)
            {
                _photoController.Fly(NavDirection.Next);
                _repeatButtonReleaseCheckTimer.Start();
            }
            else
            {
                _photoController.Fly(NavDirection.Prev);
                _repeatButtonReleaseCheckTimer.Start();
            }
        }
    }

    private void D2dCanvasThumbNail_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;

        var delta = e.GetCurrentPoint(D2dCanvasThumbNail).Properties.MouseWheelDelta;
        if (delta > 0)
        {
            _photoController.Fly(NavDirection.Next);
            _repeatButtonReleaseCheckTimer.Start();
        }
        else
        {
            _photoController.Fly(NavDirection.Prev);
            _repeatButtonReleaseCheckTimer.Start();
        }
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
        var sExe = Process.GetCurrentProcess().MainModule.FileName;
        var ico = Icon.ExtractAssociatedIcon(sExe);
        SendMessage(hWnd, WindowMessage.WM_SETICON, 1, ico.Handle);
    }

    private void D2dCanvas_CreateResources(CanvasControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        //args.TrackAsyncAction(_photoController.LoadFirstPhoto().AsAsyncAction());
        _photoController.LoadFirstPhoto();
    }

    private async void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            bool ctrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            switch (e.Key)
            {
                case VirtualKey.C when ctrlPressed:
                    await _photoController.CopyFileToClipboardAsync();
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                    Close();
                    break;

                // File Navigation
                case VirtualKey.Right when !ctrlPressed:
                    _photoController.Fly(NavDirection.Next);
                    break;
                case VirtualKey.Left when !ctrlPressed:
                    _photoController.Fly(NavDirection.Prev);
                    break;

                // ZOOM
                case VirtualKey.Add when ctrlPressed:
                    _canvasController.ZoomByKeyboard(1.25f);
                    break;
                case VirtualKey.Subtract when ctrlPressed:
                    _canvasController.ZoomByKeyboard(0.8f);
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
            if (e.Key == PlusVK)
                _canvasController.ZoomByKeyboard(1.25f);
            else if (e.Key == MinusVK)
                _canvasController.ZoomByKeyboard(0.8f);
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
        _canvasController.RotateCurrentPhotoBy90(true);
    }

    private void ButtonHelp_OnClick(object sender, RoutedEventArgs e)
    {
    }

    private void ButtonSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingWindow == null)
        {
            _settingWindow = new Settings(_thumbNailController);
            _settingWindow.SetWindowSize(1024, 768);
            ThemeController.Instance.AddWindow(_settingWindow);
            _settingWindow.Closed += SettingWindow_Closed;
        }

        _settingWindow.Activate();
    }

    private void SettingWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingWindow != null)
            _settingWindow.Closed -= SettingWindow_Closed;
        _settingWindow = null;
    }

    private void PhotoDisplayWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingWindow != null)
        {
            _settingWindow.Close();
        }
    }

    //private void ButtonCoffee_OnClick(object sender, RoutedEventArgs e)
    //{
    //}

    public void SetWindowBackground(string backGround)
    {
        switch (backGround)
        {
            case "Transparent":
            {
                SystemBackdrop = _transparentTintBackdrop;
                break;
            }
            case "Acrylic":
            {
                SystemBackdrop = null;
                TrySetDesktopAcrylicBackdrop();
                break;
            }
            case "Mica":
            {
                SystemBackdrop = null;
                TrySetMicaBackdrop(false);
                break;
            }
            case "Mica Alt":
            {
                SystemBackdrop = null;
                TrySetMicaBackdrop(true);
                break;
            }
            case "Frozen":
            {
                SystemBackdrop = _frozenBackdrop;
                break;
            }
        }
    }

    private bool TrySetMicaBackdrop(bool useMicaAlt)
    {
        if (!MicaController.IsSupported()) return false;
        MicaBackdrop micaBackdrop = new()
        {
            Kind = useMicaAlt ? MicaKind.BaseAlt : MicaKind.Base
        };
        SystemBackdrop = micaBackdrop;
        return true;
    }

    private bool TrySetDesktopAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return false;
        var desktopAcrylicBackdrop = new DesktopAcrylicBackdrop();
        SystemBackdrop = desktopAcrylicBackdrop;
        return true;
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
        _canvasController.SetHundredPercent(true);
    }
}

public class BlurredBackdrop : CompositionBrushBackdrop
{
    protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
    {
        return compositor.CreateHostBackdropBrush();
    }
}