#nullable enable
using FlyPhotos.AppSettings;
using FlyPhotos.Controllers;
using FlyPhotos.Data;
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
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using FlyPhotos.NativeWrappers;
using WinRT;
using WinRT.Interop;
using WinUIEx;
using Icon = System.Drawing.Icon;
using Win32Methods = FlyPhotos.NativeWrappers.Win32Methods;

namespace FlyPhotos.Views;

public sealed partial class PhotoDisplayWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasController _canvasController;
    private readonly ThumbNailController _thumbNailController;
    private readonly PhotoDisplayController _photoController;

    private Settings? _settingWindow;

    private readonly DispatcherTimer _repeatButtonReleaseCheckTimer = new() { Interval = new TimeSpan(0, 0, 0, 0, 100) };
    private readonly DispatcherTimer _wheelScrollBrakeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

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

    // Accumulators for smooth scrolling
    private int _verticalDeltaAccumulator;
    private int _horizontalDeltaAccumulator;

    // For Dragging
    private Point _lastPoint;
    private bool _isDragging;

    public PhotoDisplayWindow(string firstPhotoPath)
    {
        InitializeComponent();
        D2dCanvasThumbNail.Height = AppConfig.Settings.ThumbnailSize;

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
        SetWindowBackdrop(AppConfig.Settings.WindowBackdrop);
        SetWindowTheme(AppConfig.Settings.Theme);
        DispatcherQueue.EnsureSystemDispatcherQueue();

        var photoSessionState = new PhotoSessionState() { FirstPhotoPath = firstPhotoPath };
        _thumbNailController = new ThumbNailController(D2dCanvasThumbNail, photoSessionState);
        _canvasController = new CanvasController(D2dCanvas, _thumbNailController, photoSessionState);
        _photoController = new PhotoDisplayController(D2dCanvas, _canvasController, _thumbNailController, photoSessionState);
        _photoController.StatusUpdated += PhotoController_StatusUpdated;
        _canvasController.OnZoomChanged += CanvasController_OnZoomChanged;
        _canvasController.OnFitToScreenStateChanged += CanvasController_OnFitToScreenStateChanged;  
        _canvasController.OnOneToOneStateChanged += CanvasController_OnOneToOneStateChanged;
        _thumbNailController.ThumbnailClicked += Thumbnail_Clicked;

        TxtFileName.Text = Path.GetFileName(firstPhotoPath);
        BorderTxtFileName.Visibility = AppConfig.Settings.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
        ButtonExpander.Visibility = AppConfig.Settings.ShowCacheStatus ? Visibility.Visible : Visibility.Collapsed;

        Activated += PhotoDisplayWindow_Activated;
        SizeChanged += PhotoDisplayWindow_SizeChanged;
        AppWindow.Closing += PhotoDisplayWindow_Closing;
        Closed += PhotoDisplayWindow_Closed;

        D2dCanvas.CreateResources += D2dCanvas_CreateResources;
        D2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        D2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        D2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        D2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;

        D2dCanvasThumbNail.PointerWheelChanged += ThumbNail_PointerWheelChanged;

        MainLayout.KeyDown += HandleKeyDown;
        MainLayout.KeyUp += HandleKeyUp;

        _repeatButtonReleaseCheckTimer.Tick += RepeatButtonReleaseCheckTimer_Tick;
        _wheelScrollBrakeTimer.Tick += WheelScrollBrakeTimer_Tick;

        //this.Maximize(); // Maximise will be called from App.xaml.cs
        _lastWindowState = OverlappedPresenterState.Maximized;

        _opacityFader = new OpacityFader([BorderButtonPanel, D2dCanvasThumbNail, BorderTxtFileName], MainLayout);
        _inactivityFader = new InactivityFader(BorderTxtZoom);
    }

    #region Event Handlers

    private void PhotoDisplayWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        var presenter = (AppWindow.Presenter as OverlappedPresenter);
        if (presenter != null && presenter.State != _lastWindowState)
            _lastWindowState = presenter.State;
    }

    private async void PhotoDisplayWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        await SaveLastUsedMonitorInfo();
        await AnimatePhotoDisplayWindowClose();
    }

    private async void PhotoDisplayWindow_Closed(object sender, WindowEventArgs args)
    {
        _repeatButtonReleaseCheckTimer.Stop();
        _wheelScrollBrakeTimer.Stop();
        _thumbNailController.ThumbnailClicked -= Thumbnail_Clicked;
        _photoController.StatusUpdated -= PhotoController_StatusUpdated;
        _canvasController.OnZoomChanged -= CanvasController_OnZoomChanged;
        _canvasController.OnFitToScreenStateChanged -= CanvasController_OnFitToScreenStateChanged;        
        _canvasController.OnOneToOneStateChanged -= CanvasController_OnOneToOneStateChanged;
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

    private async void ButtonNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        await _photoController.Fly(NavDirection.Next);
        _repeatButtonReleaseCheckTimer.Stop();
        _repeatButtonReleaseCheckTimer.Start();
    }

    private async void ButtonBack_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        await _photoController.Fly(NavDirection.Prev);
        _repeatButtonReleaseCheckTimer.Stop();
        _repeatButtonReleaseCheckTimer.Start();
    }

    private async void ButtonBackNext_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        await _photoController.Fly(delta > 0 ? NavDirection.Prev : NavDirection.Next);
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Start();
    }

    private void ButtonRotate_OnClick(object sender, RoutedEventArgs e)
    {
        _canvasController.RotateCurrentPhotoBy90(true);
    }

    private void ButtonRotate_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.RotateCurrentPhotoBy90(delta > 0);
    }

    private void ButtonScaleSet_Click(object sender, RoutedEventArgs e)
    {
        if (ButtonScaleSet.IsChecked != false)        
            _canvasController.FitToScreen(true);        
        else        
            ButtonScaleSet.IsChecked = true;
         
    }

    private void ButtonOneIsToOne_Click(object sender, RoutedEventArgs e)
    {
        if (ButtonOneIsToOne.IsChecked != false)        
            _canvasController.ZoomToHundred();        
        else        
            ButtonOneIsToOne.IsChecked = true;
        
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

    private void ButtonSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingWindow == null)
        {
            _settingWindow = new Settings();
            _settingWindow.SetWindowSize(900, 768);
            Util.MoveWindowToMonitor(_settingWindow, Util.GetMonitorForWindow(this));
            _settingWindow.CenterOnScreen();
            _settingWindow.Closed += SettingWindow_Closed;
            _settingWindow.Activate();
            _settingWindow.SettingChanged += SettingWindow_SettingChanged;
        }
        else
        {
            _settingWindow.Activate();
        }
    }

    private void SettingWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingWindow != null)
        {
            _settingWindow.Closed -= SettingWindow_Closed;
            _settingWindow.SettingChanged -= SettingWindow_SettingChanged;
        }
        _settingWindow = null;
    }

    private void D2dCanvas_CreateResources(CanvasControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        //args.TrackAsyncAction(_photoController.LoadFirstPhoto().AsAsyncAction());
        _ = _photoController.LoadFirstPhoto();
    }

    private void D2dCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(D2dCanvas);
        if (!pointerPoint.Properties.IsLeftButtonPressed) return;
        if (!_canvasController.IsPressedOnImage(pointerPoint.Position.AdjustForDpi(D2dCanvas))) return;
        D2dCanvas.CapturePointer(e.Pointer);
        _lastPoint = pointerPoint.Position;
        _isDragging = true;
    }

    private void D2dCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var currentPoint = e.GetCurrentPoint(D2dCanvas).Position;
        // Calculate logical delta
        double deltaX = currentPoint.X - _lastPoint.X;
        double deltaY = currentPoint.Y - _lastPoint.Y;
        // Adjust delta for DPI 
        deltaX = deltaX.AdjustForDpi(D2dCanvas);
        deltaY = deltaY.AdjustForDpi(D2dCanvas);
        _canvasController.Pan(deltaX, deltaY);
        _lastPoint = currentPoint;
    }

    private async void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(D2dCanvas).Properties;
        var dpiAdjustedPosition = e.GetCurrentPoint(D2dCanvas).Position.AdjustForDpi(D2dCanvas);

        switch (properties.PointerUpdateKind)
        {
            case Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased:
                {
                    if (_canvasController.IsPressedOnImage(dpiAdjustedPosition))
                    {
                        var filePath = _photoController.GetFullPathCurrentFile();
                        if (File.Exists(filePath))
                        {
                            Win32Methods.GetCursorPos(out Win32Methods.POINT mousePosScreen);
                            NativeWrapper.ShowContextMenu(this, filePath, mousePosScreen.X, mousePosScreen.Y);
                        }
                    }
                    break;
                }
            case Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased:
                {
                    if (_isDragging)
                    {
                        D2dCanvas.ReleasePointerCapture(e.Pointer);
                        _isDragging = false;
                    }
                    else
                    {
                        if (!_canvasController.IsPressedOnImage(dpiAdjustedPosition))
                        {
                            var pointerY = e.GetCurrentPoint(D2dCanvas).Position.Y;
                            if (_lastWindowState == OverlappedPresenterState.Maximized && pointerY >= AppTitlebar.ActualHeight)
                            {
                                this.Restore();
                            }
                        }
                    }
                    break;
                }
            case Microsoft.UI.Input.PointerUpdateKind.XButton1Released: // Mouse Back button pressed
                {
                    if (AppConfig.Settings.UseMouseFwdBackForStepZoom)
                        _canvasController.StepZoom(ZoomDirection.Out, dpiAdjustedPosition);
                    else
                        await _photoController.Fly(NavDirection.Prev);
                    break;
                }
            case Microsoft.UI.Input.PointerUpdateKind.XButton2Released: // Mouse Forward button pressed
                {
                    if (AppConfig.Settings.UseMouseFwdBackForStepZoom)
                        _canvasController.StepZoom(ZoomDirection.In, dpiAdjustedPosition);
                    else
                        await _photoController.Fly(NavDirection.Next);
                    break;
                }
        }
    }

    private async void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(D2dCanvas).Properties;
        var delta = props.MouseWheelDelta;
        var isHorizontalScroll = props.IsHorizontalMouseWheel;
        var currentPoint = e.GetCurrentPoint(D2dCanvas).Position;

        if (isHorizontalScroll)
        {
            await HandleMouseWheelNavigation(delta, isHorizontalScroll: true);
        }
        else // Is vertical scroll
        {
            // Alt + vertical scroll always navigates
            if (Util.IsAltPressed())
            {
                await HandleMouseWheelNavigation(delta, isHorizontalScroll: false);
            }
            // Ctrl + vertical scroll always zooms
            else if (Util.IsControlPressed())
            {
                HandleMouseWheelZoom(delta, currentPoint);
            }
            else
            {
                // When Alt and Ctrl are not pressed, behave based on settings
                switch (AppConfig.Settings.DefaultMouseWheelBehavior)
                {
                    case DefaultMouseWheelBehavior.Navigate:
                        await HandleMouseWheelNavigation(delta, isHorizontalScroll: false);
                        break;
                    case DefaultMouseWheelBehavior.Zoom:
                        HandleMouseWheelZoom(delta, currentPoint);
                        break;
                }
            }
        }
    }

    private async void Thumbnail_Clicked(int shiftIndex)
    {
        await _photoController.FlyBy(shiftIndex);
    }

    private async void ThumbNail_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(D2dCanvasThumbNail).Properties;
        int delta = props.MouseWheelDelta;
        bool isHorizontal = props.IsHorizontalMouseWheel;

        await HandleMouseWheelNavigation(delta, isHorizontal);
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

                case VirtualKey.Delete:
                    await DeleteCurrentlyDisplayedPhoto();
                    break;

                case VirtualKey.Home:
                    await _photoController.FlyToFirst();
                    break;
                case VirtualKey.End:
                    await _photoController.FlyToLast();
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
                case VirtualKey.Up when !ctrlPressed:
                    _canvasController.ZoomByKeyboard(ZoomDirection.In);
                    break;
                case VirtualKey.Down when !ctrlPressed:
                    _canvasController.ZoomByKeyboard(ZoomDirection.Out);
                    break;

                case VirtualKey.PageUp:
                    _canvasController.StepZoom(ZoomDirection.In);
                    break;
                case VirtualKey.PageDown:
                    _canvasController.StepZoom(ZoomDirection.Out);
                    break;

                // PAN
                case VirtualKey.Up when ctrlPressed:
                    _canvasController.Pan(0, -20);
                    break;
                case VirtualKey.Down when ctrlPressed:
                    _canvasController.Pan(0, 20);
                    break;
                case VirtualKey.Left when ctrlPressed:
                    _canvasController.Pan(-20, 0);
                    break;
                case VirtualKey.Right when ctrlPressed:
                    _canvasController.Pan(20, 0);
                    break;

                case VirtualKey.D:
                    Util.ShowFileProperties(_photoController.GetFullPathCurrentFile(), this);
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

    private async Task DeleteCurrentlyDisplayedPhoto()
    {
        if (!_photoController.CanDeleteCurrentPhoto())
        {
            TxtZoom.Text = "Loading high quality version. Try after some time";
            _inactivityFader.ReportActivity();
            _canvasController.Shrug();
            return;
        }

        if (AppConfig.Settings.ConfirmForDelete)
        {
            var confirmDialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "Confirm Deletion",
                Content = "Are you sure you want to delete this file?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            var result = await confirmDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                Logger.Info("User cancelled file deletion.");
                return;
            }
        }

        var delResult = await _photoController.DeleteCurrentPhoto();

        if (delResult.DeleteSuccess)
        {
            if (delResult.IsLastPhoto)
            {
                await AnimatePhotoDisplayWindowClose();
            }
        }
        else
        {
            _canvasController.Shrug();
            Logger.Error("Failed to delete file");
            var errorDialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "Deletion Failed",
                Content = $"The application could not delete the file.{Environment.NewLine}{delResult.FailMessage}",
                CloseButtonText = "OK"
            };
            await errorDialog.ShowAsync();
        }
    }

    private async void HandleKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Right or VirtualKey.Left)) return;
        await _photoController.Brake();
    }


    #endregion

    #region Timer Ticks

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

    #endregion

    #region callbacks

    private void PhotoController_StatusUpdated(object? sender, StatusUpdateEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TxtFileName.Text = e.ListPositionAndFileName;
            CacheStatusProgress.Text = e.CacheProgressStatus;
        });
    }

    private void CanvasController_OnZoomChanged(int zoomPercent)
    {
        TxtZoom.Text = $"{zoomPercent}%";
        _inactivityFader.ReportActivity();
    }

    private void CanvasController_OnFitToScreenStateChanged(bool isFitted)
    {
        DispatcherQueue.TryEnqueue(() => { ButtonScaleSet.IsChecked = isFitted; });
    }

    private void CanvasController_OnOneToOneStateChanged(bool isOneToOne)
    {
        DispatcherQueue.TryEnqueue(() => { ButtonOneIsToOne.IsChecked = isOneToOne; });
    }

    #endregion

    #region Helper Functions

    private async Task HandleMouseWheelNavigation(int delta, bool isHorizontalScroll)
    {
        if (_photoController.IsSinglePhoto()) return;

        ref int accumulator = ref (isHorizontalScroll ? ref _horizontalDeltaAccumulator : ref _verticalDeltaAccumulator);
        accumulator += delta;

        if (Math.Abs(accumulator) < AppConfig.Settings.ScrollThreshold) return;

        var direction = isHorizontalScroll ?
            (accumulator > 0 ? NavDirection.Next : NavDirection.Prev) :
            (accumulator > 0 ? NavDirection.Prev : NavDirection.Next);

        accumulator = 0;
        await _photoController.Fly(direction);
        RestartBrakeTimer();
    }

    private void HandleMouseWheelZoom(int delta, Point point)
    {
        var adjustedPoint = point.AdjustForDpi(D2dCanvas);

        if (IsPrecisionTouchpad(delta))
            _canvasController.ZoomAtPointPrecision(delta, adjustedPoint);
        else
            _canvasController.ZoomAtPoint(delta > 0 ? ZoomDirection.In : ZoomDirection.Out, adjustedPoint);
    }

    /// <summary>
    /// Heuristic to detect precision touchpad / smooth pinch scroll.
    /// Returns true if delta is not a multiple of standard WHEEL_DELTA (120),
    /// which usually indicates a touchpad gesture.
    /// </summary>
    private static bool IsPrecisionTouchpad(int delta) => Math.Abs(delta) % 120 != 0;

    private void RestartBrakeTimer()
    {
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Start();
    }

    private async Task SaveLastUsedMonitorInfo()
    {
        // Save monitor information before closing
        if (!AppConfig.Settings.RememberLastMonitor) return;
        AppConfig.Settings.LastUsedMonitorId = Util.GetMonitorForWindow(this);
        await AppConfig.SaveAsync();
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

    #endregion

    #region Theming and Backdrop
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
        Win32Methods.SendMessage(hWnd, Win32Methods.WM_SETICON, new IntPtr(1), ico.Handle);
    }

    private void SetWindowBackdropTransparency(int transparencyIntensity)
    {
        if (_currentBackdropType == WindowBackdropType.Transparent)
        {
            _transparentTintBackdrop.TintColor = Color.FromArgb((byte)(((100 - transparencyIntensity) * 255) / 100), 0, 0, 0);
            SystemBackdrop = _transparentTintBackdrop;
        }
    }

    private void SettingWindow_SettingChanged(Setting setting)
    {
        switch (setting)
        {
            case Setting.ThumbnailSizeSize:
                D2dCanvasThumbNail.Height = AppConfig.Settings.ThumbnailSize;
                D2dCanvasThumbNail.Invalidate();
                _thumbNailController.RefreshThumbnail();
                break;
            case Setting.ThumbnailSelectionColor:
                _thumbNailController.RefreshThumbnail();
                break;
            case Setting.ThumbnailShowHide:
                _thumbNailController.ShowHideThumbnailBasedOnSettings();
                break;
            case Setting.CheckeredBackgroundShowHide:
                _canvasController.HandleCheckeredBackgroundChange();
                break;
            case Setting.Theme:
                SetWindowTheme(AppConfig.Settings.Theme);
                break;
            case Setting.BackDrop:
                SetWindowBackdrop(AppConfig.Settings.WindowBackdrop);
                break;
            case Setting.BackDropTransparency:
                SetWindowBackdropTransparency(AppConfig.Settings.TransparentBackgroundIntensity);
                break;
            case Setting.FileNameShowHide:
                BorderTxtFileName.Visibility = AppConfig.Settings.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
                break;
            case Setting.CacheStatusShowHide:
                ButtonExpander.Visibility = AppConfig.Settings.ShowCacheStatus ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    private void SetWindowBackdrop(WindowBackdropType backdropType)
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

    private void PhotoDisplayWindow_Activated(object sender, WindowActivatedEventArgs args)
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

    private void SetBackColorAsPerThemeAndBackdrop()
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

    #endregion
}

public partial class BlurredBackdrop : CompositionBrushBackdrop
{
    protected override Windows.UI.Composition.CompositionBrush CreateBrush(Windows.UI.Composition.Compositor compositor)
    {
        return compositor.CreateHostBackdropBrush();
    }
}