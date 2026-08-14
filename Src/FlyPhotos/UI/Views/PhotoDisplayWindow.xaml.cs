#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Controllers;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;
using FlyPhotos.Services.ExternalAppListing;
using FlyPhotos.UI.Actions;
using FlyPhotos.UI.Behaviors;
using FlyPhotos.UI.Controls;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
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
    #region Fields

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasController _canvasController;
    private readonly ThumbNailController _thumbNailController;
    private readonly PhotoDisplayController _photoController;

    private Settings? _settingWindow;

    private readonly SingleFlightGate _shortcutsGate = new();
    private readonly SingleFlightGate _moreMenuGate = new();
    private ShortcutsFlyoutControl? _shortcutsControl;
    private FileActionDelete? _fileActionDelete;
    private FileActionRename? _fileActionRename;
    private readonly long _backIsPressedToken;
    private readonly long _nextIsPressedToken;
    private readonly DispatcherTimer _wheelScrollBrakeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private PointerUpdateKind _lastPointerDownKind;
    private readonly SideButtonNavBehavior _sideButtonNav = null!;

    private readonly VirtualKey _plusVk = Util.GetKeyThatProduces('+');
    private readonly VirtualKey _minusVk = Util.GetKeyThatProduces('-');

    private Dictionary<(VirtualKey Key, bool Ctrl, bool Alt), Func<Task>> _keyActions = null!;
    private HashSet<(VirtualKey, bool, bool)> _handledKeys = null!;
    private bool _cacheStatusExpanded;

    private readonly OpacityFader _opacityFader;
    private readonly InactivityFader _inactivityFader;
    private readonly MouseAutoHider _mouseAutoHider;
    private readonly WindowCaptionButtonFader _captionButtonFader;
    private readonly WindowPlacementManager _windPlacementManager;
    private readonly WindowFullScreenManager _windFullScreenManager;
    private readonly WindowAppearanceManager _windAppearanceManager;
    private readonly CtrlDragWindowMover _ctrlDragWindowMover;

    private bool _loadingStarted;

    // Accumulators for smooth scrolling
    private int _verticalDeltaAccumulator;
    private int _horizontalDeltaAccumulator;

    // For Dragging
    private Point _lastPoint;
    private bool _isDragging;

    // Last double tapped tick count, used to differentiate single/double taps
    private long _lastDoubleTappedAt;

    // For Right Click Zoom
    private readonly DispatcherTimer _rightClickZoomHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private readonly DispatcherTimer _rightClickZoomRepeatTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    // True for as long as the right button is physically held; guards a Tick that was already
    // queued on the dispatcher at the moment the button is released from acting on stale state.
    private bool _isRightClickHeld;
    private Point _rightClickPosition;

    #endregion

    #region Construction & Init

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

        var photoSessionState = new PhotoSessionState { FirstPhotoPath = firstPhotoPath, FlyLaunchedExternally = extLaunch };
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
        _canvasController.OnPageChanged += CanvasController_OnPageChanged;
        _thumbNailController.ThumbnailClicked += Thumbnail_Clicked;

        TxtFileName.Text = Path.GetFileName(firstPhotoPath);
        BorderTxtFileName.Visibility = AppConfig.Settings.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
        ButtonExpander.Visibility = AppConfig.Settings.ShowCacheStatus ? Visibility.Visible : Visibility.Collapsed;

        // The canvas/photo and keyboard navigation must never mirror, regardless of app
        // language or ambient system FlowDirection.
        RtlLayoutBehavior.ForceLeftToRight((FrameworkElement)Content);
        RtlLayoutBehavior.ApplyFlowDirection(ButtonMoreMenuFlyout, AppConfig.Settings.Language);

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

        _backIsPressedToken = ButtonBack.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, ButtonBackNext_IsPressedChanged);
        _nextIsPressedToken = ButtonNext.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, ButtonBackNext_IsPressedChanged);

        _wheelScrollBrakeTimer.Tick += WheelScrollBrakeTimer_Tick;
        _rightClickZoomHoldTimer.Tick += RightClickZoomHoldTimer_Tick;
        _rightClickZoomRepeatTimer.Tick += RightClickZoomRepeatTimer_Tick;

        _sideButtonNav = new SideButtonNavBehavior(
            MainLayout,
            dir => _photoController.Fly(dir),
            () => _photoController.Brake(),
            () => AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom);
        _opacityFader = new OpacityFader([BorderButtonPanel, D2dCanvasThumbNail, BorderTxtFileName], MainLayout, BottomPanel, AppConfig.Settings.AutoFade);
        _inactivityFader = new InactivityFader(BorderTxtZoom);
        _mouseAutoHider = new MouseAutoHider(MainLayout, AppConfig.Settings.AutoHideMouse, TimeSpan.FromSeconds(1));
        _windPlacementManager = new WindowPlacementManager(this, AppConfig.Settings.WindowState);
        _windFullScreenManager = new WindowFullScreenManager(this);
        _captionButtonFader = new WindowCaptionButtonFader(AppWindow.TitleBar, MainLayout, AppConfig.Settings.AutoHideCaptionButtons, ButtonFullScreenClose);
        _ctrlDragWindowMover = new CtrlDragWindowMover(D2dCanvas, AppWindow, AppConfig.Settings.CtrlDragToMoveWindow);
        _ctrlDragWindowMover.IsOnBackground = pos => !_canvasController.IsPressedOnImage(pos.AdjustForDpi(D2dCanvas));
        _windFullScreenManager.FullScreenToggled += WindFullScreenManager_FullScreenToggled;
        InitKeyActions();
    }

    private static Func<Task> Act(Action a) => () =>
    {
        a();
        return Task.CompletedTask;
    };

    private void InitKeyActions()
    {
        // KEY, CTRL, ALT, whether to mark event as handled after executing the action
        _handledKeys =
        [
            (VirtualKey.C, true, false), // Ctrl+C — prevent browser-style copy
            (VirtualKey.Enter, false, false), // Enter — prevent WinUI default button activation
            (VirtualKey.Enter, false, true), // Alt+Enter — prevent default Enter handling
            (VirtualKey.Delete, false, false) // Delete — prevent WinUI focus-loss default
        ];

        // KEY, CTRL, ALT, Action
        _keyActions = new Dictionary<(VirtualKey, bool, bool), Func<Task>>
        {
            // File operations
            [(VirtualKey.C, true, false)] = () => _photoController.CopyFileToClipboardAsync(),
            [(VirtualKey.Delete, false, false)] = DeleteCurrentlyDisplayedPhoto,
            [(VirtualKey.F2, false, false)] = ShowRenameFlyoutAsync,
            [(VirtualKey.W, false, false)] = Act(OpenFileInExplorer),
            [(VirtualKey.S, false, false)] = Act(() => FileShareDialogService.ShareFile(this, _photoController.GetFullPathCurrentFile())),
            [(VirtualKey.P, false, false)] = Act(() => Util.PrintFile(_photoController.GetFullPathCurrentFile())),
            [(VirtualKey.M, false, false)] = ShowMoreMenuAsync,

            // Window
            [(VirtualKey.Escape, false, false)] = AnimatePhotoDisplayWindowClose,
            [(VirtualKey.F11, false, false)] = Act(() => _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose)),
            [(VirtualKey.Enter, false, false)] = Act(ToggleMaximizeRestore),

            // Photo navigation
            [(VirtualKey.Right, false, false)] = () => _photoController.Fly(NavDirection.Next),
            [(VirtualKey.Left, false, false)] = () => _photoController.Fly(NavDirection.Prev),
            [(VirtualKey.Home, false, false)] = () => _photoController.FlyToFirst(),
            [(VirtualKey.End, false, false)] = () => _photoController.FlyToLast(),

            // Multi-page navigation (Alt+Arrow)
            [(VirtualKey.Right, false, true)] = Act(() => _canvasController.ChangePage(NavDirection.Next)),
            [(VirtualKey.Left, false, true)] = Act(() => _canvasController.ChangePage(NavDirection.Prev)),

            // Zoom
            [(VirtualKey.Add, true, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In, CurrentDragAnchor())),
            [(VirtualKey.Subtract, true, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out, CurrentDragAnchor())),
            [(VirtualKey.Up, false, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In, CurrentDragAnchor())),
            [(VirtualKey.Down, false, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out, CurrentDragAnchor())),
            [(VirtualKey.PageUp, false, false)] = Act(() => _canvasController.StepZoom(ZoomDirection.In, CurrentDragAnchor())),
            [(VirtualKey.PageDown, false, false)] = Act(() => _canvasController.StepZoom(ZoomDirection.Out, CurrentDragAnchor())),

            // Pan (Ctrl+Arrow)
            [(VirtualKey.Up, true, false)] = Act(() => _canvasController.Pan(0, -20)),
            [(VirtualKey.Down, true, false)] = Act(() => _canvasController.Pan(0, 20)),
            [(VirtualKey.Left, true, false)] = Act(() => _canvasController.Pan(-20, 0)),
            [(VirtualKey.Right, true, false)] = Act(() => _canvasController.Pan(20, 0)),

            // Rotate
            [(VirtualKey.L, false, false)] = Act(() => _canvasController.RotateCurrentPhotoBy90(false)),
            [(VirtualKey.R, false, false)] = Act(() => _canvasController.RotateCurrentPhotoBy90(true)),

            // View
            [(VirtualKey.F, false, false)] = Act(() => _canvasController.FitToScreen(true)),
            [(VirtualKey.A, false, false)] = Act(() => _canvasController.ZoomToHundred()),

            // File properties
            [(VirtualKey.Enter, false, true)] = Act(() => Util.ShowFileProperties(_photoController.GetFullPathCurrentFile())),
            [(VirtualKey.D, false, false)] = Act(() => Util.ShowFileProperties(_photoController.GetFullPathCurrentFile(), true)),
            [(VirtualKey.I, false, false)] = Act(() => ExifInfoPanel.Toggle(_photoController.GetFullPathCurrentFile())),

            // External apps
            [(VirtualKey.E, false, false)] = ShowShortcutsPanelAsync,
            [(VirtualKey.Number1, true, false)] = () => LaunchExternalAppAsync(0),
            [(VirtualKey.NumberPad1, true, false)] = () => LaunchExternalAppAsync(0),
            [(VirtualKey.Number2, true, false)] = () => LaunchExternalAppAsync(1),
            [(VirtualKey.NumberPad2, true, false)] = () => LaunchExternalAppAsync(1),
            [(VirtualKey.Number3, true, false)] = () => LaunchExternalAppAsync(2),
            [(VirtualKey.NumberPad3, true, false)] = () => LaunchExternalAppAsync(2),
            [(VirtualKey.Number4, true, false)] = () => LaunchExternalAppAsync(3),
            [(VirtualKey.NumberPad4, true, false)] = () => LaunchExternalAppAsync(3),

            // Layout-aware zoom (keyboard-layout-dependent keys for + and -)
            [(_plusVk, true, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In)),
            [(_minusVk, true, false)] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out))
        };
    }

    #endregion

    #region Window Lifecycle & Closing

    private async void PhotoDisplayWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        await AnimatePhotoDisplayWindowClose();
    }

    private async void ButtonFullScreenClose_Click(object sender, RoutedEventArgs e) =>
        await AnimatePhotoDisplayWindowClose();

    private void PhotoDisplayWindow_Closed(object sender, WindowEventArgs args)
    {
        ButtonBack.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _backIsPressedToken);
        ButtonNext.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _nextIsPressedToken);
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Tick -= WheelScrollBrakeTimer_Tick;
        _rightClickZoomHoldTimer.Stop();
        _rightClickZoomHoldTimer.Tick -= RightClickZoomHoldTimer_Tick;
        _rightClickZoomRepeatTimer.Stop();
        _rightClickZoomRepeatTimer.Tick -= RightClickZoomRepeatTimer_Tick;
        _sideButtonNav.Detach();

        AppWindow.Closing -= PhotoDisplayWindow_Closing;

        _photoController.FirstPhotoLoaded -= OnFirstPhotoLoaded;
        _photoController.CacheStatusChanged -= PhotoController_CacheStatusChanged;
        _photoController.FileNameOrDetailsChanged -= PhotoController_FileNameOrDetailsChanged;

        _canvasController.OnZoomChanged -= CanvasController_OnZoomChanged;
        _canvasController.OnFitToScreenStateChanged -= CanvasController_OnFitToScreenStateChanged;
        _canvasController.OnOneToOneStateChanged -= CanvasController_OnOneToOneStateChanged;
        _canvasController.OnMutliPagePhotoLoaded -= CanvasController_OnMultiPagePhotoLoaded;
        _canvasController.OnPageChanged -= CanvasController_OnPageChanged;

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

        _windFullScreenManager.FullScreenToggled -= WindFullScreenManager_FullScreenToggled;

        _canvasController.Dispose();
        _thumbNailController.Dispose();
        _photoController.Dispose();
        _opacityFader.Dispose();
        _inactivityFader.Dispose();
        _mouseAutoHider.Dispose();
        _ctrlDragWindowMover.Dispose();
        DiskCacherWithSqlite.Shutdown();

        _windAppearanceManager.Dispose();
        _windPlacementManager.Dispose();
    }

    private void WindFullScreenManager_FullScreenToggled(bool isFullScreen)
    {
        _windPlacementManager.PauseTracking = isFullScreen;
        _captionButtonFader.IsFullScreen = isFullScreen;
    }

    private void ToggleMaximizeRestore()
    {
        if (_windFullScreenManager.IsMaximizedOrFullScreen)
            _windFullScreenManager.Restore(ButtonFullScreenClose);
        else
            _windFullScreenManager.Maximize();
    }

    private async Task AnimatePhotoDisplayWindowClose()
    {
        _settingWindow?.Close();

        if (AppConfig.Settings.OpenExitZoom)
        {
            // Arm before starting so the W2D subscribe is enqueued (FIFO) ahead of ZoomOutOnExit's
            // start action, then wait for the actual animation to finish instead of a fixed delay.
            var exitAnimation = _canvasController.WaitForPanZoomAnimationAsync(
                Constants.PanZoomAnimationDurationForExit * 2);
            _canvasController.ZoomOutOnExit(Constants.PanZoomAnimationDurationForExit);
            await exitAnimation;
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

    /// <summary>
    ///     Activates the window and applies the configured launch mode (maximized, full-screen,
    ///     or last window state). <c>Activate()</c> is always called first so that
    ///     <c>ExtendsContentIntoTitleBar</c> is stable before any maximize, avoiding the
    ///     top-edge position jag that occurs when <c>SW_SHOWMAXIMIZED</c> is applied inside
    ///     <c>WM_SHOWWINDOW</c> before the title-bar geometry has settled.
    /// </summary>
    internal void ActivateForStartup()
    {
        Activate();
        switch (AppConfig.Settings.WindowLaunchMode)
        {
            case WindowLaunchMode.Maximized:
                this.Maximize();
                break;
            case WindowLaunchMode.FullScreen:
                _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose);
                break;
            case WindowLaunchMode.LastWindowState:
                if (_windPlacementManager.WasMaximized)
                    this.Maximize();
                break;
        }
    }

    #endregion

    #region Keyboard Input

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

    #region Navigation

    private async void ButtonBackNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        await _photoController.Fly(ReferenceEquals(sender, ButtonBack) ? NavDirection.Prev : NavDirection.Next);
    }

    /// <summary>
    /// Brakes once neither nav RepeatButton is held. RepeatButton has no "released" event, but
    /// IsPressed is a DependencyProperty, so its transition is observable directly. The brake is
    /// enqueued rather than run inline: this callback sits on ButtonBase's own SetValue stack, and
    /// the re-check on the next dispatcher turn debounces a pointer that drags off a held button
    /// and back on — without it, every edge crossing zeroes the burst counter mid-flight.
    /// </summary>
    private void ButtonBackNext_IsPressedChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (ButtonBack.IsPressed || ButtonNext.IsPressed) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (ButtonBack.IsPressed || ButtonNext.IsPressed) return; // re-pressed before this ran
            if (_photoController.IsSinglePhoto()) return;             // no navigation happened, nothing to brake
            try { await _photoController.Brake(); }
            catch (Exception ex) { Logger.Error(ex); }
        });
    }

    private async void ButtonBackNext_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        await HandleMouseWheelNavigation(props.MouseWheelDelta, props.IsHorizontalMouseWheel);
    }

    private void ButtonRotate_OnClick(object sender, RoutedEventArgs e) =>
        _canvasController.RotateCurrentPhotoBy90(true);

    private void ButtonRotate_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.RotateCurrentPhotoBy90(delta > 0);
    }

    private void ButtonPrevNextPage_OnClick(object sender, RoutedEventArgs e) =>
        _canvasController.ChangePage(ReferenceEquals(sender, ButtonPrevPage) ? NavDirection.Prev : NavDirection.Next);

    private void ButtonBackNextPage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.ChangePage(delta > 0 ? NavDirection.Prev : NavDirection.Next);
    }

    private async void Thumbnail_Clicked(int shiftIndex) => await _photoController.FlyBy(shiftIndex);

    #endregion

    #region Zoom / Fit Toolbar

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

    #endregion

    #region Canvas Pointer Input

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
        _lastPointerDownKind = updateKind;
        var pointerOverImage = _canvasController.IsPressedOnImage(dpiAdjustedPos);

        switch (updateKind)
        {
            case PointerUpdateKind.RightButtonPressed when pointerOverImage:
                D2dCanvas.CapturePointer(e.Pointer);
                _rightClickZoomHoldTimer.Stop();
                _rightClickZoomRepeatTimer.Stop();
                _rightClickPosition = dpiAdjustedPos;
                _isRightClickHeld = true;
                _rightClickZoomHoldTimer.Start();
                break;

            case PointerUpdateKind.LeftButtonPressed when pointerOverImage:
                if (_ctrlDragWindowMover.Enabled && Util.IsControlPressed()) break;
                D2dCanvas.CapturePointer(e.Pointer);
                _lastPoint = pointerPoint.Position;
                _isDragging = true;
                break;
        }
    }

    /// <summary>
    /// The DPI-adjusted cursor position to anchor a keyboard zoom at while a left-button drag is in progress,
    /// or null when not dragging (so the zoom falls back to the canvas centre). Keeps keyboard zoom anchored at
    /// the same point the user is panning, matching wheel-zoom behaviour.
    /// </summary>
    private Point? CurrentDragAnchor() => _isDragging ? _lastPoint.AdjustForDpi(D2dCanvas) : null;

    private void D2dCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var pp = e.GetCurrentPoint(D2dCanvas);
        if (!pp.Properties.IsLeftButtonPressed) { _isDragging = false; return; }
        var currentPoint = pp.Position;
        // Calculate logical delta
        double deltaX = currentPoint.X - _lastPoint.X;
        double deltaY = currentPoint.Y - _lastPoint.Y;
        // Adjust delta for DPI
        deltaX = deltaX.AdjustForDpi(D2dCanvas);
        deltaY = deltaY.AdjustForDpi(D2dCanvas);
        _canvasController.Pan(deltaX, deltaY);
        _lastPoint = currentPoint;
    }

    private void D2dCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(D2dCanvas);
        var properties = currentPoint.Properties;
        var dpiAdjustedPosition = currentPoint.Position.AdjustForDpi(D2dCanvas);

        switch (properties.PointerUpdateKind)
        {
            case PointerUpdateKind.RightButtonReleased:
                _isRightClickHeld = false;
                var wasZooming = _rightClickZoomRepeatTimer.IsEnabled;
                _rightClickZoomHoldTimer.Stop();
                _rightClickZoomRepeatTimer.Stop();
                D2dCanvas.ReleasePointerCapture(e.Pointer);
                if (!wasZooming && _canvasController.IsPressedOnImage(dpiAdjustedPosition))
                {
                    var filePath = _photoController.GetFullPathCurrentFile();
                    if (File.Exists(filePath))
                        ContextMenuHelper.ShowContextMenu(this, filePath);
                }
                break;

            case PointerUpdateKind.LeftButtonReleased when _isDragging:
                D2dCanvas.ReleasePointerCapture(e.Pointer);
                _isDragging = false;
                break;

            case PointerUpdateKind.LeftButtonReleased:
                if (Environment.TickCount64 - _lastDoubleTappedAt < Win32Methods.GetDoubleClickTime()) break;

                if (AppConfig.Settings.ClickOutsideImageToRestoreWindow &&
                    !(currentPoint.Position.Y < AppTitlebar.ActualHeight) &&
                    !_canvasController.IsPressedOnImage(dpiAdjustedPosition) &&
                    _windFullScreenManager.IsMaximizedOrFullScreen)
                    _windFullScreenManager.Restore(ButtonFullScreenClose);
                break;

            case PointerUpdateKind.MiddleButtonReleased:
                _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose);
                break;

            case PointerUpdateKind.XButton1Released:
                if (AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom)
                    _canvasController.StepZoom(ZoomDirection.Out, dpiAdjustedPosition);
                break;

            case PointerUpdateKind.XButton2Released:
                if (AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom)
                    _canvasController.StepZoom(ZoomDirection.In, dpiAdjustedPosition);
                break;
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
                if (props.IsLeftButtonPressed) // A quirky interaction to ease one-handed mouse operation.
                    await HandleMouseWheelNavigation(delta, false);
                else
                    HandleMouseWheelZoom(delta, currentPoint);
                break;
        }
    }

    private void D2dCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_lastPointerDownKind is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed) return;
        var rawPosition = e.GetPosition(D2dCanvas);
        var point = rawPosition.AdjustForDpi(D2dCanvas);

        if (_canvasController.IsPressedOnImage(point))
        {
            _lastDoubleTappedAt = Environment.TickCount64;
            // Double-tap on image: toggle between 1:1 and Fit.
            // CanvasController raises events to update button states.
            if (ButtonOneIsToOne.IsChecked == true)
                _canvasController.FitToScreen(true);
            else
                _canvasController.ZoomToHundred(point);
        }
        else
        {
            if (AppConfig.Settings.ClickOutsideImageToRestoreWindow &&
                !(rawPosition.Y < AppTitlebar.ActualHeight) &&
                !_windFullScreenManager.IsMaximizedOrFullScreen)
            {
                _windFullScreenManager.Maximize();
                _lastDoubleTappedAt = Environment.TickCount64;
            }
        }
    }

    #endregion

    #region Mouse Wheel Nav & Zoom

    private async void ThumbNail_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(D2dCanvasThumbNail).Properties;
        int delta = props.MouseWheelDelta;
        bool isHorizontal = props.IsHorizontalMouseWheel;
        await HandleMouseWheelNavigation(delta, isHorizontal);
    }

    private async Task HandleMouseWheelNavigation(int delta, bool isHorizontalScroll)
    {
        if (_photoController.IsSinglePhoto()) return;

        ref int accumulator = ref isHorizontalScroll ? ref _horizontalDeltaAccumulator : ref _verticalDeltaAccumulator;
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

    private async void WheelScrollBrakeTimer_Tick(object? sender, object e)
    {
        _wheelScrollBrakeTimer.Stop();
        // The gesture ended: drop any sub-threshold delta so it can't carry into the next one.
        _verticalDeltaAccumulator = 0;
        _horizontalDeltaAccumulator = 0;
        await _photoController.Brake();
    }

    #endregion

    #region Right-Click Zoom

    // Long-press threshold before right-click-hold starts zooming.
    private void RightClickZoomHoldTimer_Tick(object? sender, object e)
    {
        _rightClickZoomHoldTimer.Stop();
        if (!_isRightClickHeld) return; // button was released before this queued tick ran
        _rightClickZoomRepeatTimer.Start();
    }

    private void RightClickZoomRepeatTimer_Tick(object? sender, object e)
    {
        if (!_isRightClickHeld) { _rightClickZoomRepeatTimer.Stop(); return; } // released before this queued tick ran
        _canvasController.ZoomAtPointPrecision(10, _rightClickPosition);
    }

    #endregion

    #region More Menu

    private async void ButtonMore_OnClick(object sender, RoutedEventArgs e)
    {
        try { await ShowMoreMenuAsync(); }catch (Exception ex) { Logger.Error(ex); }
    }

    private Task ShowMoreMenuAsync() => _moreMenuGate.RunAsync(async () =>
    {
        if (!File.Exists(_photoController.GetFullPathCurrentFile())) return;
        await PopulateOpenWithSectionAsync();
        FlyoutBase.ShowAttachedFlyout(ButtonMore);
    });

    private void MenuItemPrint_OnClick(object sender, RoutedEventArgs e) =>
        Util.PrintFile(_photoController.GetFullPathCurrentFile());

    private void MenuItemShare_OnClick(object sender, RoutedEventArgs e) =>
        FileShareDialogService.ShareFile(this, _photoController.GetFullPathCurrentFile());

    private void MenuItemPhotoInfo_OnClick(object sender, RoutedEventArgs e) =>
        ExifInfoPanel.Toggle(_photoController.GetFullPathCurrentFile());

    private void MenuItemFileInfo_OnClick(object sender, RoutedEventArgs e) =>
        Util.ShowFileProperties(_photoController.GetFullPathCurrentFile());

    private void MenuItemOpenFileLocation_OnClick(object sender, RoutedEventArgs e) =>
        OpenFileInExplorer();

    private void OpenFileInExplorer()
    {
        var filePath = _photoController.GetFullPathCurrentFile();
        if (File.Exists(filePath))
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }

    #endregion

    #region External App Shortcuts

    private async Task LaunchExternalAppAsync(int index)
    {
        var filePathArgument = _photoController.GetFullPathCurrentFile();
        if (!File.Exists(filePathArgument)) return;

        var apps = await ExternalAppResolver.GetConfiguredAsync();
        if (index < 0 || index >= apps.Length) return;

        var app = apps[index];
        if (app != null)
            _ = app.LaunchAsync(filePathArgument);
    }

    private void LaunchExternalAppFromSender(object sender)
    {
        var filePathArgument = _photoController.GetFullPathCurrentFile();
        if (sender is FrameworkElement { Tag: InstalledApp app })
            _ = app.LaunchAsync(filePathArgument); // Fire and forget the launch, we don't need to await it here
    }

    private void MenuItemOpenWithApp_OnClick(object sender, RoutedEventArgs e) =>
        LaunchExternalAppFromSender(sender);

    /// <summary>
    /// Shows a Flyout of configured external-app shortcuts, anchored to the bottom toolbar panel.
    /// App resolution stays here (shared with the More menu / Ctrl+1..4 cache); the control lays
    /// out the buttons and raises <see cref="ShortcutsFlyoutControl.AppLaunchRequested"/> on click.
    /// </summary>
    private Task ShowShortcutsPanelAsync() => _shortcutsGate.RunAsync(async () =>
    {
        if (!File.Exists(_photoController.GetFullPathCurrentFile())) return;

        var apps = await ExternalAppResolver.GetConfiguredAsync();
        _shortcutsControl ??= CreateShortcutsControl();
        _shortcutsControl.Show(BorderButtonPanel, apps);
    });

    private ShortcutsFlyoutControl CreateShortcutsControl()
    {
        var control = new ShortcutsFlyoutControl();
        control.AppLaunchRequested += app => _ = app.LaunchAsync(_photoController.GetFullPathCurrentFile());
        return control;
    }

    private async Task PopulateOpenWithSectionAsync()
    {
        while (ButtonMoreMenuFlyout.Items[0] != SeparatorAfterOpenWith)
            ButtonMoreMenuFlyout.Items.RemoveAt(0);

        if (!AppConfig.Settings.ShowExternalAppShortcuts)
        {
            SeparatorAfterOpenWith.Visibility = Visibility.Collapsed;
            return;
        }
        SeparatorAfterOpenWith.Visibility = Visibility.Visible;

        var insertAt = 0;
        ButtonMoreMenuFlyout.Items.Insert(insertAt++,
            new MenuFlyoutItem { Text = L.Get("MenuItemOpenWithHeader/Text"), IsEnabled = false });

        var apps = await ExternalAppResolver.GetConfiguredAsync();
        var anyAdded = false;
        foreach (var app in apps)
        {
            if (app == null) continue;
            var item = new MenuFlyoutItem { Text = TruncateAppName(app.DisplayName), Tag = app, Icon = AppIconFactory.Build(app) };
            ToolTipService.SetToolTip(item, app.DisplayName);
            item.Click += MenuItemOpenWithApp_OnClick;
            ButtonMoreMenuFlyout.Items.Insert(insertAt++, item);
            anyAdded = true;
        }

        if (!anyAdded)
            ButtonMoreMenuFlyout.Items.Insert(insertAt,
                new MenuFlyoutItem { Text = L.Get("NoShortcutsCreated/Message"), IsEnabled = false });
    }

    private static string TruncateAppName(string name, int maxLength = 24) =>
        name.Length > maxLength ? name[..maxLength] + "…" : name;

    #endregion

    #region Rename

    private async void MenuItemRename_OnClick(object sender, RoutedEventArgs e)
    {
        try { await ShowRenameFlyoutAsync(); }
        catch (Exception ex) { Logger.Error(ex); }
    }
    private Task ShowRenameFlyoutAsync()
    {
        _fileActionRename ??= new FileActionRename(
            preExecute:       _photoController.CanRenameCurrentPhoto,
            preExecuteFailed: () => _canvasController.Shrug(),
            anchorProvider:   () => BorderButtonPanel,
            filePathProvider: _photoController.GetFullPathCurrentFile,
            execute:          _photoController.RenameCurrentPhoto);
        return _fileActionRename.ExecuteAsync();
    }

    private void CloseRenameFlyoutIfFileChanged(string currentFilePath) =>
        _fileActionRename?.Close(currentFilePath);

    #endregion

    #region Delete

    private async void MenuItemDelete_OnClick(object sender, RoutedEventArgs e)
    {
        try { await DeleteCurrentlyDisplayedPhoto(); }
        catch (Exception ex) { Logger.Error(ex); }
    }

    private Task DeleteCurrentlyDisplayedPhoto()
    {
        _fileActionDelete ??= new FileActionDelete(
            xamlRootProvider: () => Content.XamlRoot,
            preExecute: _photoController.CanDeleteCurrentPhoto,
            preExecuteFailed: () => { TxtZoom.Text = L.Get("LoadingHighQuality/Message");
                                      _inactivityFader.ReportActivity(); _canvasController.Shrug(); },
            execute: _photoController.DeleteCurrentPhoto,
            executeFailed: () => _canvasController.Shrug(),
            postExecute: () => Task.CompletedTask,                 // no-op today
            postExecuteLastItem: AnimatePhotoDisplayWindowClose);

        return _fileActionDelete.ExecuteAsync();
    }

    #endregion

    #region Settings Window

    private void ButtonSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingWindow == null)
        {
            _settingWindow = new Settings { IsCurrentPhotoRaw = () => _photoController.CurrentPhotoIsRaw };
            _settingWindow.Closed += SettingWindow_Closed;
            _settingWindow.SettingChanged += SettingWindow_SettingChanged;
            _settingWindow.Initialize(Util.GetMonitorForWindow(this));
        }
        _settingWindow.Activate();
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

    private void SettingWindow_SettingChanged(Setting setting)
    {
        switch (setting)
        {
            case Setting.ThumbnailSizeSize:
                D2dCanvasThumbNail.Height = AppConfig.Settings.ThumbnailSize;
                // CanvasAnimatedControl has no Invalidate(); RefreshThumbnail() flags a rebuild on its loop.
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
            case Setting.ImageScalingQualityChange:
                _canvasController.HandleImageScalingQualityChange();
                break;
            case Setting.RawDecodingChange:
                _ = _photoController.InvalidateRawHqCache();
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
                _photoController.UpdateFileNameAndDetails();
                break;
            case Setting.CacheStatusShowHide:
                ButtonExpander.Visibility = AppConfig.Settings.ShowCacheStatus ? Visibility.Visible : Visibility.Collapsed;
                break;
            case Setting.CaptionButtonsAutoHideToggle:
                _captionButtonFader.Enabled = AppConfig.Settings.AutoHideCaptionButtons;
                break;
            case Setting.CtrlDragToMoveWindowToggle:
                _ctrlDragWindowMover.Enabled = AppConfig.Settings.CtrlDragToMoveWindow;
                break;
            case Setting.AutoFadeToggle:
                _opacityFader.Enabled = AppConfig.Settings.AutoFade;
                break;
            case Setting.AutoHideMouseToggle:
                _mouseAutoHider.Enabled = AppConfig.Settings.AutoHideMouse;
                break;
        }
    }

    #endregion

    #region Cache Status

    private void ButtonExpander_Click(object sender, RoutedEventArgs e)
    {
        _cacheStatusExpanded = !_cacheStatusExpanded;
        IconExpander.Text = ((char)(_cacheStatusExpanded ? 0xE760 : 0xE761)).ToString();
        CacheStatusProgress.Visibility = _cacheStatusExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PhotoController_CacheStatusChanged(string cacheProgressStatus) =>
        DispatcherQueue.TryEnqueue(() => { CacheStatusProgress.Text = cacheProgressStatus; });

    #endregion

    #region State Callbacks

    private void PhotoController_FileNameOrDetailsChanged(FileDisplayDetails fileDisplayDetails)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var currentFilePath = _photoController.GetFullPathCurrentFile();
            ExifInfoPanel.CloseIfFileChanged(currentFilePath);
            CloseRenameFlyoutIfFileChanged(currentFilePath);
            TxtFileName.Text = fileDisplayDetails.DisplayText;
            Title = fileDisplayDetails.FileName;
        });
    }

    private void CanvasController_OnZoomChanged(int zoomPercent)
    {
        if (AppConfig.Settings.ShowZoomPercent)
        {
            TxtZoom.Text = $"{zoomPercent}%";
            _inactivityFader.ReportActivity();
        }
    }

    // CanvasController marshals this event to the UI thread.
    private void CanvasController_OnFitToScreenStateChanged(bool isFitted) => ButtonScaleSet.IsChecked = isFitted;

    // CanvasController marshals this event to the UI thread.
    private void CanvasController_OnOneToOneStateChanged(bool isOneToOne) => ButtonOneIsToOne.IsChecked = isOneToOne;

    private void CanvasController_OnMultiPagePhotoLoaded(bool isMultiPagePhoto)
    {
        var visibilityState = isMultiPagePhoto ? Visibility.Visible : Visibility.Collapsed;
        ButtonNextPage.Visibility = visibilityState;
        ButtonPrevPage.Visibility = visibilityState;
    }

    // CanvasController raises this on the UI thread once a page navigation settled, so the filename
    // bar can pick up the new page's size and index.
    private void CanvasController_OnPageChanged() => _photoController.UpdateFileNameAndDetails();

    #endregion

    #region License

    private ActionCheckLicense LicenseAction => field ??= new ActionCheckLicense(
        xamlRootProvider: () => Content?.XamlRoot, onTrialExpired: AnimatePhotoDisplayWindowClose);

    // FirstPhotoLoaded fires on the controller's STA thread; marshal to the UI thread.
    private void OnFirstPhotoLoaded() => DispatcherQueue.TryEnqueue(async () =>
    {
        try { await LicenseAction.PhotoLoadedAsync(); }
        catch (Exception ex) { Logger.Error(ex); }
    });

    private async void MainLayout_Loaded(object sender, RoutedEventArgs e)
    {
        try { await LicenseAction.WindowLoadedAsync(); }
        catch (Exception ex) { Logger.Error(ex); }
    }

    #endregion

    #region Infrastructure

    /// <summary>
    /// Ensures only one async run of a guarded action is in flight at a time; a call that
    /// arrives while one is already running is dropped instead of overlapping with it.
    /// </summary>
    private sealed class SingleFlightGate
    {
        private bool _isRunning;
        public async Task RunAsync(Func<Task> action)
        {
            if (_isRunning) return;
            _isRunning = true;
            try { await action(); }
            finally { _isRunning = false; }
        }
    }

    #endregion
}
