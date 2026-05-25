#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;
using FlyPhotos.Services.ExternalAppListing;
using FlyPhotos.UI.Behaviors;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NLog;
using WinUIEx;

namespace FlyPhotos.UI.Views;

public sealed partial class PhotoDisplayWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasController _canvasController;
    private readonly ThumbNailController _thumbNailController;
    private readonly PhotoDisplayController _photoController;

    private Settings? _settingWindow;

    private readonly DispatcherTimer _repeatButtonReleaseCheckTimer = new() { Interval = new TimeSpan(0, 0, 0, 0, 100) };
    private readonly DispatcherTimer _wheelScrollBrakeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private readonly VirtualKey _plusVk = Util.GetKeyThatProduces('+');
    private readonly VirtualKey _minusVk = Util.GetKeyThatProduces('-');

    private Dictionary<(VirtualKey Key, bool Ctrl, bool Alt), Func<Task>> _keyActions = null!;
    private HashSet<(VirtualKey, bool, bool)> _handledKeys = null!;
    private bool _cacheStatusExpanded;

    private readonly OpacityFader _opacityFader;
    private readonly InactivityFader _inactivityFader;
    private readonly MouseAutoHider _mouseAutoHider;
    // private readonly WindowCaptionButtonFader _captionButtonFader;
    private readonly WindowPlacementManager _windPlacementManager;
    private readonly WindowFullScreenManager _windFullScreenManager;
    private readonly WindowAppearanceManager _windAppearanceManager;

    private bool _loadingStarted;
    private bool _firstPhotoLoaded;
    private bool _licenseCheckDone;

    // Accumulators for smooth scrolling
    private int _verticalDeltaAccumulator;
    private int _horizontalDeltaAccumulator;

    // For Dragging
    private Point _lastPoint;
    private bool _isDragging;

    // For Right Click Zoom
    private CancellationTokenSource? _rightClickCts;
    private bool _isRightClickZooming;
    private Point _rightClickPosition;

    public PhotoDisplayWindow(string firstPhotoPath, bool extLaunch)
    {
        InitializeComponent();
        D2dCanvasThumbNail.Height = AppConfig.Settings.ThumbnailSize;

        Title = "FlyPhotos";
        Util.SetWindowIcon(this);

        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumWidth = 400;
        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumHeight = 300;

        _windAppearanceManager = new WindowAppearanceManager(this, AppConfig.Settings.WindowBackdrop);
        _windAppearanceManager.SetupTransparentTitleBar(AppTitlebar);

        DispatcherQueue.EnsureSystemDispatcherQueue();

        var photoSessionState = new PhotoSessionState() { FirstPhotoPath = firstPhotoPath, FlyLaunchedExternally = extLaunch };
        _thumbNailController = new ThumbNailController(D2dCanvasThumbNail, photoSessionState);
        _canvasController = new CanvasController(D2dCanvas, _thumbNailController, photoSessionState);
        _photoController = new PhotoDisplayController(D2dCanvas, _canvasController, _thumbNailController, photoSessionState);
        _photoController.CacheStatusChanged += PhotoController_CacheStatusChanged;
        _photoController.FileNameOrDetailsChanged += PhotoController_FileNameOrDetailsChanged;
        _photoController.FirstPhotoLoaded += OnFirstPhotoLoaded;
        _canvasController.OnZoomChanged += CanvasController_OnZoomChanged;
        _canvasController.OnFitToScreenStateChanged += CanvasController_OnFitToScreenStateChanged;  
        _canvasController.OnOneToOneStateChanged += CanvasController_OnOneToOneStateChanged;
        _canvasController.OnMutliPagePhotoLoaded += CanvasController_OnMultiPagePhotoLoaded;
        _thumbNailController.ThumbnailClicked += Thumbnail_Clicked;

        TxtFileName.Text = Path.GetFileName(firstPhotoPath);
        BorderTxtFileName.Visibility = AppConfig.Settings.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
        ButtonExpander.Visibility = AppConfig.Settings.ShowCacheStatus ? Visibility.Visible : Visibility.Collapsed;
        ButtonShortcuts.Visibility = AppConfig.Settings.ShowExternalAppShortcuts ? Visibility.Visible : Visibility.Collapsed;

        // Secondary instances are restricted: no Settings, no cache status.
        if (AppConfig.Volatile.IsSecondaryInstance)
        {
            ButtonSettings.Visibility = Visibility.Collapsed;
            ButtonExpander.Visibility = Visibility.Collapsed;
            ButtonBack.Visibility = Visibility.Collapsed;
            ButtonNext.Visibility = Visibility.Collapsed;
        }
        
        AppWindow.Closing += PhotoDisplayWindow_Closing;
        Closed += PhotoDisplayWindow_Closed;

        D2dCanvas.CreateResources += D2dCanvas_CreateResources;
        D2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        D2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        D2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        D2dCanvas.DoubleTapped += D2dCanvas_DoubleTapped;
        D2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;

        D2dCanvasThumbNail.PointerWheelChanged += ThumbNail_PointerWheelChanged;

        MainLayout.Loaded += MainLayout_Loaded;
        MainLayout.KeyDown += HandleKeyDown;
        MainLayout.KeyUp += HandleKeyUp;

        _repeatButtonReleaseCheckTimer.Tick += RepeatButtonReleaseCheckTimer_Tick;
        _wheelScrollBrakeTimer.Tick += WheelScrollBrakeTimer_Tick;

        _opacityFader = new OpacityFader([BorderButtonPanel, D2dCanvasThumbNail, BorderTxtFileName], MainLayout);
        _inactivityFader = new InactivityFader(BorderTxtZoom);
        _mouseAutoHider = new MouseAutoHider(MainLayout, TimeSpan.FromSeconds(1));
        _windPlacementManager = new WindowPlacementManager(this, AppConfig.Settings.WindowState);
        _windFullScreenManager = new WindowFullScreenManager(this);
        // _captionButtonFader = new WindowCaptionButtonFader(AppWindow.TitleBar, MainLayout);
        _windFullScreenManager.FullScreenToggled += isFullScreen =>
        {
            _windPlacementManager.PauseTracking = isFullScreen;
            //_captionButtonFader.Suspended = isFullScreen;
        };
        InitKeyActions();
    }

    private static Func<Task> Act(Action a) => () => { a(); return Task.CompletedTask; };

    private void InitKeyActions()
    {
        // KEY, CTRL, ALT, whether to mark event as handled after executing the action
        _handledKeys =
        [
            (VirtualKey.C,      true,  false),  // Ctrl+C — prevent browser-style copy
            (VirtualKey.Enter,  false, true),   // Alt+Enter — prevent default Enter handling
            (VirtualKey.Delete, false, false),  // Delete — prevent WinUI focus-loss default
        ];

        // KEY, CTRL, ALT, Action
        _keyActions = new Dictionary<(VirtualKey, bool, bool), Func<Task>>
        {
            // File operations
            [(VirtualKey.C,      true,  false)] = () => _photoController.CopyFileToClipboardAsync(),
            [(VirtualKey.Delete, false, false)] = DeleteCurrentlyDisplayedPhoto,
            [(VirtualKey.W,      false, false)] = Act(OpenFileInExplorer),
            [(VirtualKey.S,      false, false)] = Act(() => FileShareDialogService.ShareFile(this, _photoController.GetFullPathCurrentFile())),

            // Window
            [(VirtualKey.Escape, false, false)] = AnimatePhotoDisplayWindowClose,
            [(VirtualKey.F11,    false, false)] = Act(() => _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose)),

            // Photo navigation
            [(VirtualKey.Right, false, false)] = () => _photoController.Fly(NavDirection.Next),
            [(VirtualKey.Left,  false, false)] = () => _photoController.Fly(NavDirection.Prev),
            [(VirtualKey.Home,  false, false)] = () => _photoController.FlyToFirst(),
            [(VirtualKey.End,   false, false)] = () => _photoController.FlyToLast(),

            // Multi-page navigation (Alt+Arrow)
            [(VirtualKey.Right, false, true)] = Act(() => _canvasController.ChangePage(NavDirection.Next)),
            [(VirtualKey.Left,  false, true)] = Act(() => _canvasController.ChangePage(NavDirection.Prev)),

            // Zoom
            [(VirtualKey.Add,      true,  false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In)),
            [(VirtualKey.Subtract, true,  false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out)),
            [(VirtualKey.Up,       false, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In)),
            [(VirtualKey.Down,     false, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out)),
            [(VirtualKey.PageUp,   false, false)] = Act(() => _canvasController.StepZoom(ZoomDirection.In)),
            [(VirtualKey.PageDown, false, false)] = Act(() => _canvasController.StepZoom(ZoomDirection.Out)),

            // Pan (Ctrl+Arrow)
            [(VirtualKey.Up,    true, false)] = Act(() => _canvasController.Pan(0,   -20)),
            [(VirtualKey.Down,  true, false)] = Act(() => _canvasController.Pan(0,    20)),
            [(VirtualKey.Left,  true, false)] = Act(() => _canvasController.Pan(-20,   0)),
            [(VirtualKey.Right, true, false)] = Act(() => _canvasController.Pan(20,    0)),

            // Rotate
            [(VirtualKey.L, false, false)] = Act(() => _canvasController.RotateCurrentPhotoBy90(false)),
            [(VirtualKey.R, false, false)] = Act(() => _canvasController.RotateCurrentPhotoBy90(true)),

            // View
            [(VirtualKey.F, false, false)] = Act(() => _canvasController.FitToScreen(true)),
            [(VirtualKey.A, false, false)] = Act(() => _canvasController.ZoomToHundred()),

            // File properties
            [(VirtualKey.Enter, false, true)] = Act(() => Util.ShowFileProperties(_photoController.GetFullPathCurrentFile())),
            [(VirtualKey.D,     false, false)] = Act(() => Util.ShowFileProperties(_photoController.GetFullPathCurrentFile(), true)),

            // External apps
            [(VirtualKey.E,          false, false)] = Act(() => ButtonShortcuts_OnClick(ButtonShortcuts, new RoutedEventArgs())),
            [(VirtualKey.Number1,    true,  false)] = () => LaunchExternalAppAsync(0),
            [(VirtualKey.NumberPad1, true,  false)] = () => LaunchExternalAppAsync(0),
            [(VirtualKey.Number2,    true,  false)] = () => LaunchExternalAppAsync(1),
            [(VirtualKey.NumberPad2, true,  false)] = () => LaunchExternalAppAsync(1),
            [(VirtualKey.Number3,    true,  false)] = () => LaunchExternalAppAsync(2),
            [(VirtualKey.NumberPad3, true,  false)] = () => LaunchExternalAppAsync(2),
            [(VirtualKey.Number4,    true,  false)] = () => LaunchExternalAppAsync(3),
            [(VirtualKey.NumberPad4, true,  false)] = () => LaunchExternalAppAsync(3),

            // Layout-aware zoom (keyboard-layout-dependent keys for + and -)
            [(_plusVk,  true, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In)),
            [(_minusVk, true, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out)),
        };
    }

    #region Event Handlers

    private async void PhotoDisplayWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        await AnimatePhotoDisplayWindowClose();
    }

    private async void ButtonFullScreenClose_Click(object sender, RoutedEventArgs e)
    {
        await AnimatePhotoDisplayWindowClose();
    }

    private void PhotoDisplayWindow_Closed(object sender, WindowEventArgs args)
    {
        _repeatButtonReleaseCheckTimer.Stop();
        _repeatButtonReleaseCheckTimer.Tick -= RepeatButtonReleaseCheckTimer_Tick;
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Tick -= WheelScrollBrakeTimer_Tick;
        _rightClickCts?.Cancel();
        _rightClickCts?.Dispose();

        AppWindow.Closing -= PhotoDisplayWindow_Closing;

        _photoController.FirstPhotoLoaded -= OnFirstPhotoLoaded;
        _photoController.CacheStatusChanged -= PhotoController_CacheStatusChanged;
        _photoController.FileNameOrDetailsChanged -= PhotoController_FileNameOrDetailsChanged;

        _canvasController.OnZoomChanged -= CanvasController_OnZoomChanged;
        _canvasController.OnFitToScreenStateChanged -= CanvasController_OnFitToScreenStateChanged;
        _canvasController.OnOneToOneStateChanged -= CanvasController_OnOneToOneStateChanged;
        _canvasController.OnMutliPagePhotoLoaded -= CanvasController_OnMultiPagePhotoLoaded;

        _thumbNailController.ThumbnailClicked -= Thumbnail_Clicked;

        D2dCanvas.CreateResources -= D2dCanvas_CreateResources;
        D2dCanvas.PointerPressed -= D2dCanvas_PointerPressed;
        D2dCanvas.PointerMoved -= D2dCanvas_PointerMoved;
        D2dCanvas.PointerReleased -= D2dCanvas_PointerReleased;
        D2dCanvas.DoubleTapped -= D2dCanvas_DoubleTapped;
        D2dCanvas.PointerWheelChanged -= D2dCanvas_PointerWheelChanged;

        D2dCanvasThumbNail.PointerWheelChanged -= ThumbNail_PointerWheelChanged;

        MainLayout.Loaded -= MainLayout_Loaded;
        MainLayout.KeyDown -= HandleKeyDown;
        MainLayout.KeyUp -= HandleKeyUp;

        _canvasController.Dispose();
        _thumbNailController.Dispose();
        _photoController.Dispose();
        _opacityFader.Dispose();
        _inactivityFader.Dispose();
        _mouseAutoHider.Dispose();
        DiskCacherWithSqlite.Shutdown();

        _windAppearanceManager.Dispose();
        _windPlacementManager.Dispose();
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

    private void ButtonPrevPage_OnClick(object sender, RoutedEventArgs e)
    {
        _canvasController.ChangePage(NavDirection.Prev);
    }

    private void ButtonNextPage_OnClick(object sender, RoutedEventArgs e)
    {
        _canvasController.ChangePage(NavDirection.Next);
    }

    private void ButtonBackNextPage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.ChangePage(delta > 0 ? NavDirection.Prev : NavDirection.Next);
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
        _cacheStatusExpanded = !_cacheStatusExpanded;
        IconExpander.Text = ((char)(_cacheStatusExpanded ? 0xE760 : 0xE761)).ToString();
        CacheStatusProgress.Visibility = _cacheStatusExpanded ? Visibility.Visible : Visibility.Collapsed;
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

    private async void ButtonShortcuts_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_photoController.GetFullPathCurrentFile())) return;

        var senderButton = sender as Button;
        if (senderButton == null) return;
        var stackPanel = new StackPanel { Spacing = 4, };
        var appShortCuts = new List<string> { AppConfig.Settings.ExternalApp1, AppConfig.Settings.ExternalApp2, AppConfig.Settings.ExternalApp3, AppConfig.Settings.ExternalApp4 };

        foreach (var shortCut in appShortCuts)
        {
            if (string.IsNullOrEmpty(shortCut)) continue;
            
            var app = await ShellAppProvider.GetAppAsync(shortCut);

            if (app == null) continue;

            var flyoutButton = new Button { Width = 60, Height = 50, Tag = shortCut };
            var bmp = app.Icon;
            flyoutButton.Content = bmp != null
                ? new Image { Source = bmp, Width = 32, Height = 32 }
                : new FontIcon { Glyph = "\uED35", FontSize = 32 }; // Default icon
            ToolTipService.SetToolTip(flyoutButton, app.DisplayName);
            flyoutButton.Tag = app;
            flyoutButton.Click += FlyoutButton_OnClick;
            stackPanel.Children.Add(flyoutButton);
        }

        UIElement content = stackPanel.Children.Count != 0 ? stackPanel : new TextBlock { Text = L.Get("NoShortcutsCreated/Message") };
        var flyout = new Flyout { Content = content };        
        FlyoutBase.SetAttachedFlyout(senderButton, flyout);
        FlyoutBase.ShowAttachedFlyout(senderButton);
    }

    /// <summary>
    /// This event handler is for the buttons INSIDE the flyout.
    /// It launches the application path stored in the button's Tag property.
    /// </summary>
    private void FlyoutButton_OnClick(object sender, RoutedEventArgs e)
    {
		string filePathArgument = _photoController.GetFullPathCurrentFile();
        var clickedButton = sender as Button;
        if (clickedButton?.Tag is InstalledApp appToLaunch)
            _ = appToLaunch.LaunchAsync(filePathArgument); // Fire and forget the launch, we don't need to await it here
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

    private void D2dCanvas_CreateResources(CanvasAnimatedControl sender, 
        CanvasCreateResourcesEventArgs args)
    {
        // This flag is needed to prevent multiple calls to LoadFirstPhoto
        // CreateResources can be called again after the initial call
        // if the device is lost and recreated which can happen when there is a DPI change for example.
        if (!_loadingStarted)
        {
            _loadingStarted = true;
            _ = _photoController.LoadFirstPhoto();
        }
    }
    private void D2dCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pointerPoint = e.GetCurrentPoint(D2dCanvas);
        var dpiAdjustedPos = pointerPoint.Position.AdjustForDpi(D2dCanvas);
        var updateKind = pointerPoint.Properties.PointerUpdateKind;

        if (updateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed)
        {
            if (!_canvasController.IsPressedOnImage(dpiAdjustedPos)) return;
            D2dCanvas.CapturePointer(e.Pointer);

            _rightClickCts?.Cancel();
            _rightClickCts?.Dispose();
            _rightClickCts = new CancellationTokenSource();
            var token = _rightClickCts.Token;

            _rightClickPosition = dpiAdjustedPos;
            _isRightClickZooming = false;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, token);
                    if (token.IsCancellationRequested) return;

                    _isRightClickZooming = true;
                    while (!token.IsCancellationRequested)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!token.IsCancellationRequested)
                                _canvasController.ZoomAtPointPrecision(10, _rightClickPosition);
                        });
                        await Task.Delay(16, token);
                    }
                }
                catch (TaskCanceledException) { }
            }, token);

            return;
        }

        if (updateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed || pointerPoint.Properties.IsLeftButtonPressed)
        {
            if (!_canvasController.IsPressedOnImage(dpiAdjustedPos)) return;
            D2dCanvas.CapturePointer(e.Pointer);
            _lastPoint = pointerPoint.Position;
            _isDragging = true;
        }
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
        var currentPoint = e.GetCurrentPoint(D2dCanvas);
        var properties = currentPoint.Properties;
        var dpiAdjustedPosition = currentPoint.Position.AdjustForDpi(D2dCanvas);

        switch (properties.PointerUpdateKind)
        {
            case Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased:
                {
                    _rightClickCts?.Cancel();
                    D2dCanvas.ReleasePointerCapture(e.Pointer);

                    if (!_isRightClickZooming)
                    {
                        if (_canvasController.IsPressedOnImage(dpiAdjustedPosition))
                        {
                            var filePath = _photoController.GetFullPathCurrentFile();
                            if (File.Exists(filePath))
                            {
                                ContextMenuHelper.ShowContextMenu(this, filePath);
                            }
                        }
                    }
                    _isRightClickZooming = false;
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
                            var pointerY = currentPoint.Position.Y;
                            if (pointerY >= AppTitlebar.ActualHeight)
                                _windFullScreenManager.Restore(ButtonFullScreenClose);
                        }
                    }
                    break;
                }
            case Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased:
                {
                    _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose);
                    break;
                }
            case Microsoft.UI.Input.PointerUpdateKind.XButton1Released: // Mouse Back button pressed
                {
                    if (AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom)
                        _canvasController.StepZoom(ZoomDirection.Out, dpiAdjustedPosition);
                    else
                        await _photoController.FlyBy(-1);
                    break;
                }
            case Microsoft.UI.Input.PointerUpdateKind.XButton2Released: // Mouse Forward button pressed
                {
                    if (AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom)
                        _canvasController.StepZoom(ZoomDirection.In, dpiAdjustedPosition);
                    else
                        await _photoController.FlyBy(1);
                    break;
                }
        }
    }

    private async void D2dCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(D2dCanvas).Properties;
        var delta = props.MouseWheelDelta;
        var scroll = props.IsHorizontalMouseWheel ? ScrollDirection.Horizontal : ScrollDirection.Vertical;
        var currentPoint = e.GetCurrentPoint(D2dCanvas).Position;

        switch (scroll)
        {
            case ScrollDirection.Horizontal:
                await HandleMouseWheelNavigation(delta, true);
                break;
            // Alt + vertical scroll always navigates
            case ScrollDirection.Vertical when Util.IsAltPressed():
                await HandleMouseWheelNavigation(delta, false);
                break;
            // Ctrl + vertical scroll always zooms
            case ScrollDirection.Vertical when Util.IsControlPressed():
                HandleMouseWheelZoom(delta, currentPoint);
                break;
            // When Alt and Ctrl are not pressed, behave based on settings
            case ScrollDirection.Vertical when AppConfig.Settings.DefaultMouseWheelBehavior == DefaultMouseWheelBehavior.Navigate:
                await HandleMouseWheelNavigation(delta, false);
                break;
            case ScrollDirection.Vertical when AppConfig.Settings.DefaultMouseWheelBehavior == DefaultMouseWheelBehavior.Zoom:
                HandleMouseWheelZoom(delta, currentPoint);
                break;
        }
    }

    private void D2dCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var point = e.GetPosition(D2dCanvas).AdjustForDpi(D2dCanvas);
        // Only handle double click if inside image bounds
        if (!_canvasController.IsPressedOnImage(point)) return;

        // Toggle between 1:1 and Fit
        // If currently one-to-one, then fit. Otherwise zoom to 100%.
        // CanvasController raises events to update button states.
        if (ButtonOneIsToOne.IsChecked == true)
        {
            _canvasController.FitToScreen(true);
        }
        else
        {
            _canvasController.ZoomToHundred(point);
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
            var key = (e.Key, Util.IsControlPressed(), Util.IsAltPressed());
            if (_keyActions.TryGetValue(key, out var action))
            {
                await action();
                if (_handledKeys.Contains(key)) e.Handled = true;
            }
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

    private void PhotoController_FileNameOrDetailsChanged(FileDisplayDetails fileDisplayDetails)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TxtFileName.Text = fileDisplayDetails.DisplayText;
            Title = fileDisplayDetails.FileName;
        });
    }

    private void PhotoController_CacheStatusChanged(string cacheProgressStatus)
    {
        DispatcherQueue.TryEnqueue(() => { CacheStatusProgress.Text = cacheProgressStatus; });
    }

    private void CanvasController_OnZoomChanged(int zoomPercent)
    {
        if (AppConfig.Settings.ShowZoomPercent)
        {
            TxtZoom.Text = $"{zoomPercent}%";
            _inactivityFader.ReportActivity();
        }
    }

    private void CanvasController_OnFitToScreenStateChanged(bool isFitted)
    {
        // CanvasController marshals this event to the UI thread.
        ButtonScaleSet.IsChecked = isFitted;
    }

    private void CanvasController_OnOneToOneStateChanged(bool isOneToOne)
    {
        // CanvasController marshals this event to the UI thread.
        ButtonOneIsToOne.IsChecked = isOneToOne;
    }

    private void CanvasController_OnMultiPagePhotoLoaded(bool isMultiPagePhoto)
    {
        var visibilityState = isMultiPagePhoto ? Visibility.Visible : Visibility.Collapsed;
        ButtonNextPage.Visibility = visibilityState;
        ButtonPrevPage.Visibility = visibilityState;
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
                _windAppearanceManager.SetWindowTheme(AppConfig.Settings.Theme);
                break;
            case Setting.BackDrop:
                _windAppearanceManager.SetWindowBackdrop(AppConfig.Settings.WindowBackdrop);
                break;
            case Setting.BackDropTransparency:
                _windAppearanceManager.SetWindowBackdropTransparency(AppConfig.Settings.TransparentBackgroundIntensity);
                break;
            case Setting.FileNameShowHide:
                BorderTxtFileName.Visibility = AppConfig.Settings.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
                break;
            case Setting.ImageDimensionsShowHide:
                _photoController.RefreshFileNameAndDetails();
                break;
            case Setting.CacheStatusShowHide:
                ButtonExpander.Visibility = AppConfig.Settings.ShowCacheStatus ? Visibility.Visible : Visibility.Collapsed;
                break;
            case Setting.ExtShortcutsShowHide:
                ButtonShortcuts.Visibility = AppConfig.Settings.ShowExternalAppShortcuts ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    #endregion

    #region Helper Functions

    private async Task LaunchExternalAppAsync(int index)
    {
        var filePathArgument = _photoController.GetFullPathCurrentFile();
        if (!File.Exists(filePathArgument)) return;

        var appShortCuts = new List<string> { AppConfig.Settings.ExternalApp1, AppConfig.Settings.ExternalApp2, AppConfig.Settings.ExternalApp3, AppConfig.Settings.ExternalApp4 };
        if (index < 0 || index >= appShortCuts.Count) return;

        var shortCut = appShortCuts[index];
        if (string.IsNullOrEmpty(shortCut)) return;

        var app = await ShellAppProvider.GetAppAsync(shortCut);
        if (app != null)
        {
            _ = app.LaunchAsync(filePathArgument);
        }
    }

    /// <summary>
    ///     Enters full-screen mode at launch, using the same <see cref="WindowFullScreenManager.ToggleFullScreen"/> path
    ///     as the interactive F11 toggle. This ensures PauseTracking is set, the exit-full-screen button is shown,
    ///     and <c>_wasMaximizedBeforeFullScreen</c> is recorded correctly so exiting later works properly.
    /// </summary>
    internal void EnterFullScreenOnLaunch()
    {
        _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose);
    }

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

    private async Task AnimatePhotoDisplayWindowClose()
    {
        _settingWindow?.Close();

        if (AppConfig.Settings.OpenExitZoom)
        {
            _canvasController.ZoomOutOnExit(Constants.PanZoomAnimationDurationForExit);
            await Task.Delay(Constants.PanZoomAnimationDurationForExit);
        }
        SaveLastWindowState();
        this.Hide();
        Close();
    }

    private void SaveLastWindowState()
    {
        if (AppConfig.Settings.WindowLaunchMode == WindowLaunchMode.LastWindowState)
        {
            AppConfig.Settings.WindowState = _windPlacementManager.Data;
            AppConfig.Save();
        }
    }

    private void OnFirstPhotoLoaded() => DispatcherQueue.TryEnqueue(() =>
    {
        _firstPhotoLoaded = true;
        if (_licenseCheckDone) CheckLicense();
    });

    private async void MainLayout_Loaded(object sender, RoutedEventArgs e)
    {
        await LicenseService.Instance.RefreshLicenseStateAsync();
        _licenseCheckDone = true;
        if (_firstPhotoLoaded) CheckLicense();
    }

    private async void CheckLicense()
    {
        if (LicenseService.Instance.State != LicenseState.TrialExpired) return;
        if (Content?.XamlRoot == null) return; // window may be closing
        var dialog = new ContentDialog
        {
            Title = L.Get("TrialExpiredMessage/Title"),
            Content = L.Get("TrialExpiredMessage/Content"),
            CloseButtonText = L.Get("TrialExpiredMessage/CloseButton"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
        await AnimatePhotoDisplayWindowClose();
    }

    private void OpenFileInExplorer()
    {
        var filePath = _photoController.GetFullPathCurrentFile();
        if (File.Exists(filePath))
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }

    private async Task DeleteCurrentlyDisplayedPhoto()
    {
        if (AppConfig.Volatile.IsSecondaryInstance) return;
        if (!_photoController.CanDeleteCurrentPhoto())
        {
            TxtZoom.Text = L.Get("LoadingHighQuality/Message");
            _inactivityFader.ReportActivity();
            _canvasController.Shrug();
            return;
        }

        if (AppConfig.Settings.ConfirmForDelete)
        {
            var confirmDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = L.Get("ConfirmDeletion/Title"),
                Content = L.Get("ConfirmDeletion/Message"),
                PrimaryButtonText = L.Get("ConfirmDeletion/DeleteButton"),
                CloseButtonText = L.Get("ConfirmDeletion/CancelButton"),
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
                XamlRoot = Content.XamlRoot,
                Title = L.Get("DeletionFailed/Title"),
                Content = $"{L.Get("DeletionFailed/Message")}{Environment.NewLine}{delResult.FailMessage}",
                CloseButtonText = L.Get("DeletionFailed/CloseButton")
            };
            await errorDialog.ShowAsync();
        }
    }
    #endregion
}

