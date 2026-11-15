#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
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

/// <summary>
/// The main viewer window: builds the three display controllers, turns input (keys, mouse, touch, buttons) into
/// their calls, and shows what they report (file name, zoom, cache status).
/// <para>
/// Wiring: <see cref="ThumbNailController"/> (the strip) and <see cref="CanvasController"/> (the photo) each draw on
/// their own Win2D render thread; <see cref="PhotoDisplayController"/> owns the photo list and navigation, runs
/// on the UI thread and drives both. The controller is given a <see cref="Display.ImageReading.PhotoDecoder"/> on
/// the main canvas's device and the UI thread's synchronization context, which it uses to bring background work
/// back to this thread.
/// </para>
/// <para>
/// Threading: everything here runs on the UI thread. The controllers raise their events on the UI thread too, except
/// <see cref="PhotoDisplayController.CacheStatusChanged"/>, which comes from the prefetch thread and is posted here.
/// </para>
/// <para>
/// Navigation bursts: holding a Next/Prev key (or button, or side button, or scrolling the wheel) is a <b>burst</b>,
/// during which the controller holds HQ loading. Every input that starts one must end it with
/// <see cref="PhotoDisplayController.Brake"/>: key-up, the window losing focus, a button let go, or the wheel going
/// quiet.
/// </para>
/// </summary>
public sealed partial class PhotoDisplayWindow
{
    #region Fields

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // The smallest window the user can resize to, in DIPs.
    private const int MinWindowWidth = 400;
    private const int MinWindowHeight = 300;

    private readonly CanvasController _canvasController;       // draws the photo, zoom and pan
    private readonly ThumbNailController _thumbNailController; // draws the thumbnail strip
    private readonly PhotoDisplayController _photoController;  // the photo list, navigation and loading

    // The settings window while it is open; null otherwise.
    private Settings? _settingWindow;

    // Let one "Open with" panel / More menu open at a time, however fast the key is pressed.
    private readonly SingleFlightGate _shortcutsGate = new();
    private readonly SingleFlightGate _moreMenuGate = new();
    // The "Open with" panel, made on first use.
    private ShortcutsFlyoutControl? _shortcutsControl;
    // The delete and rename flows (confirmation, refusal feedback), made on first use.
    private FileActionDelete? _fileActionDelete;
    private FileActionRename? _fileActionRename;
    // Registrations of the IsPressed callbacks on the four nav buttons, to unregister them on close.
    private readonly long _backIsPressedToken;
    private readonly long _nextIsPressedToken;
    private readonly long _edgePrevIsPressedToken;
    private readonly long _edgeNextIsPressedToken;
    // Ends a wheel-scroll navigation burst once the wheel has been still this long (restarted by each notch).
    private readonly DispatcherTimer _wheelScrollBrakeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    // Which button the last mouse press on the canvas was, so a double press of a side button isn't taken as a
    // double-tap (D2dCanvas_DoubleTapped).
    private PointerUpdateKind _lastPointerDownKind;
    // Mouse back/forward side buttons: navigate (with a burst while held) or step-zoom, as set.
    private readonly SideButtonNavBehavior _sideButtonNav = null!;

    // What each command does (InitCommands), and which key chord runs which command (ShortcutCatalog, re-resolved
    // when the user changes key bindings).
    private Dictionary<CommandId, Func<Task>> _commands = null!;
    private Dictionary<KeyChord, CommandId> _routes = ShortcutCatalog.Resolve();

    /// <summary>The key currently held down on a navigation burst, so key-up brakes on whatever
    /// chord is bound to Next/Prev rather than on a hard-coded Left/Right.</summary>
    private VirtualKey _burstKey = VirtualKey.None;

    // The cache status text is showing (toggled by the expander button).
    private bool _cacheStatusExpanded;

    // Window chrome behaviours, each owning one piece of show/hide or placement logic.
    private readonly OpacityFader _opacityFader;                   // fades the toolbar, strip and file name when idle
    private readonly InactivityFader _inactivityFader;             // shows the zoom/status text briefly, then hides it
    private readonly InactivityFader _folderNameFader;             // flashes the folder name after a folder switch
    private readonly MouseAutoHider _mouseAutoHider;               // hides an idle mouse cursor
    private readonly WindowCaptionButtonFader _captionButtonFader; // fades the min/max/close buttons
    private readonly EdgeNavArrowFader _edgeNavArrowFader;         // the prev/next arrows at the window edges
    private readonly CaptionScrimFader _captionScrimFader;         // the shade behind the caption buttons
    private readonly WindowPlacementManager _windPlacementManager; // remembers the window's position and size
    private readonly WindowFullScreenManager _windFullScreenManager; // full screen, maximize, restore
    private readonly WindowAppearanceManager _windAppearanceManager; // theme, backdrop, title bar
    private readonly CtrlDragWindowMover _ctrlDragWindowMover;     // drags the window by its background

    // The first photo's load has started (D2dCanvas_CreateResources), so a device re-creation doesn't start it again.
    private bool _loadingStarted;
    // The close animation is running: further close requests are ignored.
    private bool _isClosing;

    // Accumulators for wheel navigation: deltas add up until they pass the ScrollThreshold setting (one step).
    private int _verticalDeltaAccumulator;
    private int _horizontalDeltaAccumulator;

    // For Dragging: the last pointer position, and whether a pan drag is under way.
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
    // Where the right button went down: the point the hold-zoom zooms around.
    private Point _rightClickPosition;

    #endregion

    #region Construction & Init

    /// <summary>
    /// Builds the controllers and hooks every input and event. The first photo starts loading once the canvas has
    /// its device (<see cref="D2dCanvas_CreateResources"/>).
    /// </summary>
    /// <param name="firstPhotoPath">The file Fly was opened with.</param>
    /// <param name="extLaunch">Opened from Explorer: the list follows Explorer's view.</param>
    public PhotoDisplayWindow(string firstPhotoPath, bool extLaunch)
    {
        InitializeComponent();
        D2dCanvasThumbNail.Height = AppConfig.Settings.ThumbnailSize;

        Title = "FlyPhotos";
        Util.SetWindowIcon(this);

        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumWidth = MinWindowWidth;
        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumHeight = MinWindowHeight;

        _windAppearanceManager = new WindowAppearanceManager(this, AppConfig.Settings.WindowBackdrop);
        _windAppearanceManager.SetupTransparentTitleBar(AppTitlebar);

        DispatcherQueue.EnsureSystemDispatcherQueue();

        // The strip first (the canvas redraws it), then the canvas, then the controller that drives both. The
        // controller starts listing the folder on its own startup thread right away.
        _thumbNailController = new ThumbNailController(D2dCanvasThumbNail);
        _canvasController = new CanvasController(D2dCanvas, _thumbNailController);
        _photoController = new PhotoDisplayController(_canvasController, _thumbNailController,
            new Display.ImageReading.PhotoDecoder(D2dCanvas),
            new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(D2dCanvas.DispatcherQueue),
            firstPhotoPath, extLaunch);
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
        Activated += PhotoDisplayWindow_Activated;

        D2dCanvas.CreateResources += D2dCanvas_CreateResources;
        D2dCanvas.PointerPressed += D2dCanvas_PointerPressed;
        D2dCanvas.PointerMoved += D2dCanvas_PointerMoved;
        D2dCanvas.PointerReleased += D2dCanvas_PointerReleased;
        D2dCanvas.DoubleTapped += D2dCanvas_DoubleTapped;
        D2dCanvas.PointerWheelChanged += D2dCanvas_PointerWheelChanged;
        // Touchscreen pan/pinch (#246). Mouse and pen stay on the pointer handlers above.
        D2dCanvas.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale;
        D2dCanvas.ManipulationStarted += D2dCanvas_ManipulationStarted;
        D2dCanvas.ManipulationDelta += D2dCanvas_ManipulationDelta;
        D2dCanvas.Tapped += D2dCanvas_Tapped;

        D2dCanvasThumbNail.PointerWheelChanged += ThumbNail_PointerWheelChanged;

        MainLayout.Loaded += MainLayout_Loaded;
        MainLayout.KeyDown += HandleKeyDown;
        MainLayout.KeyUp += HandleKeyUp;

        _backIsPressedToken = ButtonBack.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, ButtonBackNext_IsPressedChanged);
        _nextIsPressedToken = ButtonNext.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, ButtonBackNext_IsPressedChanged);
        _edgePrevIsPressedToken = EdgeButtonPrev.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, ButtonBackNext_IsPressedChanged);
        _edgeNextIsPressedToken = EdgeButtonNext.RegisterPropertyChangedCallback(ButtonBase.IsPressedProperty, ButtonBackNext_IsPressedChanged);

        _wheelScrollBrakeTimer.Tick += WheelScrollBrakeTimer_Tick;
        _rightClickZoomHoldTimer.Tick += RightClickZoomHoldTimer_Tick;
        _rightClickZoomRepeatTimer.Tick += RightClickZoomRepeatTimer_Tick;

        _sideButtonNav = new SideButtonNavBehavior(
            MainLayout,
            dir => _photoController.Fly(dir),
            () => _photoController.Brake(),
            () => AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom,
            () => AppConfig.Settings.SwapMouseFwdBack);
        _opacityFader = new OpacityFader([BorderButtonPanel, D2dCanvasThumbNail, BorderTxtFileName], MainLayout, BottomPanel, AppConfig.Settings.AutoFade);
        _inactivityFader = new InactivityFader(BorderTxtZoom);
        _folderNameFader = new InactivityFader(BorderTxtFolderName, hideDelayMs: 1500);
        _mouseAutoHider = new MouseAutoHider(MainLayout, AppConfig.Settings.AutoHideMouse, TimeSpan.FromSeconds(1));
        _windPlacementManager = new WindowPlacementManager(this, AppConfig.Settings.WindowState);
        _windFullScreenManager = new WindowFullScreenManager(this);
        _captionButtonFader = new WindowCaptionButtonFader(AppWindow.TitleBar, MainLayout, AppConfig.Settings.AutoHideCaptionButtons, ButtonFullScreenClose);
        _edgeNavArrowFader = new EdgeNavArrowFader(MainLayout, EdgeButtonPrev, EdgeButtonNext, EdgeNavArrowsWanted());
        _captionScrimFader = new CaptionScrimFader(MainLayout, CaptionScrim, CaptionScrimTop, CaptionScrimBottom,
            AppWindow, _windAppearanceManager.CaptionButtonForeground);
        _windAppearanceManager.CaptionButtonForegroundChanged += WindAppearanceManager_CaptionButtonForegroundChanged;
        // Drag-to-move-window is no longer a setting. Enabled stays on the mover so the
        // toggle can come back without touching this class.
        _ctrlDragWindowMover = new CtrlDragWindowMover(D2dCanvas, AppWindow, enabled: true);
        _ctrlDragWindowMover.IsOnBackground = pos => !_canvasController.IsPressedOnImage(pos.AdjustForDpi(D2dCanvas));
        _windFullScreenManager.FullScreenToggled += WindFullScreenManager_FullScreenToggled;
        InitCommands();
    }

    // Wraps a synchronous action as a command.
    private static Func<Task> Act(Action a) => () =>
    {
        a();
        return Task.CompletedTask;
    };

    /// <summary>
    /// What each command does. The chords that reach them come from <see cref="ShortcutCatalog"/>,
    /// so this table says nothing about keys. The handlers stay closures over window-local state
    /// (<see cref="CurrentDragAnchor"/>, ButtonFullScreenClose, ExifInfoPanel, the gates) rather
    /// than becoming command objects that would each need the window injected into them.
    /// </summary>
    private void InitCommands()
    {
        _commands = new Dictionary<CommandId, Func<Task>>
        {
            // File operations
            [CommandId.CopyPhoto] = () => _photoController.CopyFileToClipboardAsync(),
            [CommandId.DeletePhoto] = DeleteCurrentlyDisplayedPhoto,
            [CommandId.RenamePhoto] = ShowRenameFlyoutAsync,
            [CommandId.ShowInExplorer] = Act(OpenFileInExplorer),
            [CommandId.SharePhoto] = Act(() => FileShareDialogService.ShareFile(this, _photoController.GetFullPathCurrentFile())),
            [CommandId.PrintPhoto] = Act(() => Util.PrintFile(_photoController.GetFullPathCurrentFile())),
            [CommandId.MoreActionsMenu] = ShowMoreMenuAsync,

            // Window
            [CommandId.CloseApp] = AnimatePhotoDisplayWindowClose,
            [CommandId.FullScreen] = Act(() => _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose)),
            [CommandId.MaximizeRestore] = Act(ToggleMaximizeRestore),

            // Photo navigation
            [CommandId.NextPhoto] = () => _photoController.Fly(NavDirection.Next),
            [CommandId.PrevPhoto] = () => _photoController.Fly(NavDirection.Prev),
            [CommandId.FirstPhoto] = () => _photoController.FlyToFirst(),
            [CommandId.LastPhoto] = () => _photoController.FlyToLast(),
            [CommandId.NextFolder] = () => FlyToSiblingFolderAsync(NavDirection.Next),
            [CommandId.PrevFolder] = () => FlyToSiblingFolderAsync(NavDirection.Prev),

            // Multi-page navigation
            [CommandId.NextPage] = Act(() => _canvasController.ChangePage(NavDirection.Next)),
            [CommandId.PrevPage] = Act(() => _canvasController.ChangePage(NavDirection.Prev)),

            // Zoom
            [CommandId.ZoomIn] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.In, CurrentDragAnchor())),
            [CommandId.ZoomOut] = Act(() => _canvasController.ZoomByKeyboard(ZoomDirection.Out, CurrentDragAnchor())),
            [CommandId.StepZoomIn] = Act(() => _canvasController.StepZoom(ZoomDirection.In, CurrentDragAnchor())),
            [CommandId.StepZoomOut] = Act(() => _canvasController.StepZoom(ZoomDirection.Out, CurrentDragAnchor())),

            // Pan
            [CommandId.PanUp] = Act(() => _canvasController.Pan(0, -20)),
            [CommandId.PanDown] = Act(() => _canvasController.Pan(0, 20)),
            [CommandId.PanLeft] = Act(() => _canvasController.Pan(-20, 0)),
            [CommandId.PanRight] = Act(() => _canvasController.Pan(20, 0)),

            // Rotate
            [CommandId.RotateLeft] = Act(() => _canvasController.RotateCurrentPhotoBy90(false)),
            [CommandId.RotateRight] = Act(() => _canvasController.RotateCurrentPhotoBy90(true)),

            // View
            [CommandId.FitToWindow] = Act(() => _canvasController.FitToScreen(true)),
            [CommandId.ActualSize] = Act(() => _canvasController.ZoomToHundred()),

            // File properties
            [CommandId.FileProperties] = Act(() => Util.ShowFileProperties(_photoController.GetFullPathCurrentFile())),
            [CommandId.FileDetails] = Act(() => Util.ShowFileProperties(_photoController.GetFullPathCurrentFile(), true)),
            [CommandId.PhotoInfoPanel] = Act(() => ExifInfoPanel.Toggle(_photoController.GetFullPathCurrentFile())),

            // External apps
            [CommandId.OpenWithPanel] = ShowShortcutsPanelAsync,
            [CommandId.OpenWithApp1] = () => LaunchExternalAppAsync(0),
            [CommandId.OpenWithApp2] = () => LaunchExternalAppAsync(1),
            [CommandId.OpenWithApp3] = () => LaunchExternalAppAsync(2),
            [CommandId.OpenWithApp4] = () => LaunchExternalAppAsync(3)
        };

#if DEBUG
        ShortcutCatalog.AssertConsistent(_commands.Keys);
#endif
    }

    #endregion

    #region Window Lifecycle & Closing

    // The system close (Alt+F4, the X button): cancelled, so the close animation can play first.
    private async void PhotoDisplayWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        await AnimatePhotoDisplayWindowClose();
    }

    private async void ButtonFullScreenClose_Click(object sender, RoutedEventArgs e) =>
        await AnimatePhotoDisplayWindowClose();

    // The window is gone: unhooks everything, then disposes the controllers. The canvas goes first (its render loop
    // stops and it frees the image on screen), then the photo controller (loading stops, the photos' images are
    // released), and the disk cache last, once nothing writes to it.
    private void PhotoDisplayWindow_Closed(object sender, WindowEventArgs args)
    {
        ButtonBack.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _backIsPressedToken);
        ButtonNext.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _nextIsPressedToken);
        EdgeButtonPrev.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _edgePrevIsPressedToken);
        EdgeButtonNext.UnregisterPropertyChangedCallback(ButtonBase.IsPressedProperty, _edgeNextIsPressedToken);
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Tick -= WheelScrollBrakeTimer_Tick;
        _rightClickZoomHoldTimer.Stop();
        _rightClickZoomHoldTimer.Tick -= RightClickZoomHoldTimer_Tick;
        _rightClickZoomRepeatTimer.Stop();
        _rightClickZoomRepeatTimer.Tick -= RightClickZoomRepeatTimer_Tick;
        _sideButtonNav.Detach();
        _edgeNavArrowFader.Enabled = false;

        AppWindow.Closing -= PhotoDisplayWindow_Closing;
        Activated -= PhotoDisplayWindow_Activated;

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
        D2dCanvas.ManipulationStarted -= D2dCanvas_ManipulationStarted;
        D2dCanvas.ManipulationDelta -= D2dCanvas_ManipulationDelta;
        D2dCanvas.Tapped -= D2dCanvas_Tapped;

        D2dCanvasThumbNail.PointerWheelChanged -= ThumbNail_PointerWheelChanged;

        MainLayout.Loaded -= MainLayout_Loaded;
        MainLayout.KeyDown -= HandleKeyDown;
        MainLayout.KeyUp -= HandleKeyUp;

        _windFullScreenManager.FullScreenToggled -= WindFullScreenManager_FullScreenToggled;
        _windAppearanceManager.CaptionButtonForegroundChanged -= WindAppearanceManager_CaptionButtonForegroundChanged;
        _captionScrimFader.Dispose();

        _canvasController.Dispose();
        _thumbNailController.Dispose();
        _photoController.Dispose();
        _opacityFader.Dispose();
        _inactivityFader.Dispose();
        _folderNameFader.Dispose();
        _mouseAutoHider.Dispose();
        _ctrlDragWindowMover.Dispose();
        DiskCacherWithSqlite.Shutdown();

        _windAppearanceManager.Dispose();
        _windPlacementManager.Dispose();
    }

    // Full screen on/off: placement tracking pauses (full screen is not a size to remember) and the chrome adapts.
    private void WindFullScreenManager_FullScreenToggled(bool isFullScreen)
    {
        _windPlacementManager.PauseTracking = isFullScreen;
        _captionButtonFader.IsFullScreen = isFullScreen;
        _captionScrimFader.IsFullScreen = isFullScreen;
    }

    private void WindAppearanceManager_CaptionButtonForegroundChanged(Color glyphColor) =>
        _captionScrimFader.GlyphColor = glyphColor;

    // Maximizes, or restores a maximized or full-screen window.
    private void ToggleMaximizeRestore()
    {
        if (_windFullScreenManager.IsMaximizedOrFullScreen)
            _windFullScreenManager.Restore(ButtonFullScreenClose);
        else
            _windFullScreenManager.Maximize();
    }

    /// <summary>
    /// Restores the window with its client area sized around the currently displayed image.
    /// </summary>
    /// <returns><see langword="false"/> if the image bounds aren't usable; the caller does a plain restore.</returns>
    private bool TryRestoreWindowToImage()
    {
        // Mid-animation bounds are an in-between frame; sizing to them would freeze the zoom there.
        if (_canvasController.IsPanZoomAnimating ||
            !_canvasController.TryGetDisplayedImageBounds(out var imageBounds))
            return false;

        if (!_windFullScreenManager.TryGetClientScreenRect(out var client))
            return false;

        var canvasSize = D2dCanvas.GetSize();
        var nonCanvasWidth = client.Width - (int)Math.Round(canvasSize.Width);
        var nonCanvasHeight = client.Height - (int)Math.Round(canvasSize.Height);

        // The image's rect on screen (physical pixels) plus the non-canvas part of the client area, cut
        // to the work area: sides where the image is visible align to its edges, sides where it crosses
        // a screen edge stop there. Zoom and pan are kept, so the visible part of the image doesn't move.
        // The canvas's offset inside the client area is the same before and after, so it cancels out.
        var workArea = _windFullScreenManager.WorkArea;
        var left = Math.Max(client.X + (int)Math.Floor(imageBounds.Left), workArea.X);
        var top = Math.Max(client.Y + (int)Math.Floor(imageBounds.Top), workArea.Y);
        var right = Math.Min(client.X + (int)Math.Ceiling(imageBounds.Right) + nonCanvasWidth,
            workArea.X + workArea.Width);
        var bottom = Math.Min(client.Y + (int)Math.Ceiling(imageBounds.Bottom) + nonCanvasHeight,
            workArea.Y + workArea.Height);
        if (right - left <= nonCanvasWidth || bottom - top <= nonCanvasHeight)
            return false;

        var requested = new RectInt32(left, top, right - left, bottom - top);
        var dpiScale = D2dCanvas.Dpi / 96.0;

        // Windows enforces the minimum window size anyway; grow around the image's centre ourselves so
        // the image stays centred instead of pinned to the top-left. Scaling by DPI over-sizes slightly
        // if the minimum is already in physical pixels, which is harmless.
        var minWidth = (int)Math.Ceiling(MinWindowWidth * dpiScale);
        var minHeight = (int)Math.Ceiling(MinWindowHeight * dpiScale);
        if (requested.Width < minWidth)
        {
            requested.X -= (minWidth - requested.Width) / 2;
            requested.Width = minWidth;
        }
        if (requested.Height < minHeight)
        {
            requested.Y -= (minHeight - requested.Height) / 2;
            requested.Height = minHeight;
        }

        // Only needed if the minimum-size growth above pushed the rect off the work area.
        var target = WindowFullScreenManager.ConstrainToWorkArea(requested, workArea);

        // The canvas origin moves with the client origin; shifting by the final rect keeps the image
        // at the same place on screen.
        _canvasController.PrepareForImageSizedWindow(
            new Point(target.X - client.X, target.Y - client.Y),
            target.Width - nonCanvasWidth,
            target.Height - nonCanvasHeight);
        _windFullScreenManager.RestoreToClientRect(target, ButtonFullScreenClose);
        return true;
    }

    // Every way of closing ends here: plays the exit zoom (if on), saves the window state, then closes.
    private async Task AnimatePhotoDisplayWindowClose()
    {
        if (_isClosing) return; // A second Esc / click-outside during the exit animation.
        _isClosing = true;
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

    // Remembers the window's placement for next launch, when the launch mode is "last window state".
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

    // Runs the command bound to the pressed chord. A navigation command remembers its key, so key-up ends the burst.
    private async void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (!_routes.TryGetValue(KeyChord.FromCurrentModifiers(e.Key), out var id)) return;
            if (!_commands.TryGetValue(id, out var action)) return;

            // Set before awaiting: an async handler suspends here and the routed event finishes
            // bubbling while it runs, so an e.Handled set afterwards is ignored. Suppression also
            // has to survive the repeat guard below, or a held Enter reaches the focused button.
            if (ShortcutCatalog.Has(id, CommandFlags.Suppress)) e.Handled = true;

            // Windows repeats KeyDown while a key is held. That is the point for navigation, zoom
            // and pan; for anything that toggles it flickers, which is what holding I used to do.
            if (e.KeyStatus.WasKeyDown && !ShortcutCatalog.Has(id, CommandFlags.Repeat)) return;

            if (ShortcutCatalog.Has(id, CommandFlags.Burst)) _burstKey = e.Key;
            await action();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    /// <summary>
    /// Ends a navigation burst. Fly() holds HQ loading for as long as the key is held and only
    /// Brake() unwinds it, so this has to follow whatever chord is bound to Next/Prev — braking on
    /// a hard-coded Left/Right would leave every photo at preview quality once they are rebound.
    /// </summary>
    private async void HandleKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != _burstKey) return;
        await EndBurstAsync();
    }

    // Ends a keyboard burst: forgets the key and brakes (HQ loading resumes, the HQ is shown if already loaded).
    private async Task EndBurstAsync()
    {
        _burstKey = VirtualKey.None;
        try { await _photoController.Brake(); }
        catch (Exception ex) { Logger.Error(ex); }
    }

    /// <summary>
    /// A burst key released after the window lost focus sends its KeyUp to another window, so the burst
    /// would never end: HQ loading stays held and Delete/Rename stay refused. End it when focus leaves instead.
    /// </summary>
    private async void PhotoDisplayWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || _burstKey == VirtualKey.None) return;
        await EndBurstAsync();
    }

    #endregion

    #region Navigation

    // A toolbar or edge nav button: one step per click, and per repeat while held (they are RepeatButtons).
    private async void ButtonBackNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var isPrev = ReferenceEquals(sender, ButtonBack) || ReferenceEquals(sender, EdgeButtonPrev);
        await _photoController.Fly(isPrev ? NavDirection.Prev : NavDirection.Next);
    }

    /// <summary>Secondary instances hide the toolbar nav buttons, so they get no edge arrows either.</summary>
    private static bool EdgeNavArrowsWanted() =>
        AppConfig.Settings.ShowEdgeNavArrows && !AppConfig.Volatile.IsSecondaryInstance;

    /// <summary>
    /// Brakes once neither nav RepeatButton is held. RepeatButton has no "released" event, but
    /// IsPressed is a DependencyProperty, so its transition is observable directly. The brake is
    /// enqueued rather than run inline: this callback sits on ButtonBase's own SetValue stack, and
    /// the re-check on the next dispatcher turn debounces a pointer that drags off a held button
    /// and back on — without it, every edge crossing zeroes the burst counter mid-flight.
    /// </summary>
    private void ButtonBackNext_IsPressedChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (AnyNavButtonPressed()) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (AnyNavButtonPressed()) return; // re-pressed before this ran
            if (_photoController.IsSinglePhoto()) return;             // no navigation happened, nothing to brake
            try { await _photoController.Brake(); }
            catch (Exception ex) { Logger.Error(ex); }
        });
    }

    // Whether any of the four nav buttons is held down.
    private bool AnyNavButtonPressed() =>
        ButtonBack.IsPressed || ButtonNext.IsPressed || EdgeButtonPrev.IsPressed || EdgeButtonNext.IsPressed;

    // Scrolling over the nav buttons navigates, like scrolling over the strip.
    private async void ButtonBackNext_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        await HandleMouseWheelNavigation(props.MouseWheelDelta, props.IsHorizontalMouseWheel);
    }

    private void ButtonRotate_OnClick(object sender, RoutedEventArgs e) =>
        _canvasController.RotateCurrentPhotoBy90(true);

    // Scrolling over the rotate button rotates: up clockwise, down anticlockwise.
    private void ButtonRotate_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.RotateCurrentPhotoBy90(delta > 0);
    }

    private void ButtonPrevNextPage_OnClick(object sender, RoutedEventArgs e) =>
        _canvasController.ChangePage(ReferenceEquals(sender, ButtonPrevPage) ? NavDirection.Prev : NavDirection.Next);

    // Scrolling over the page buttons pages through a multi-page photo.
    private void ButtonBackNextPage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ButtonBack).Properties.MouseWheelDelta;
        _canvasController.ChangePage(delta > 0 ? NavDirection.Prev : NavDirection.Next);
    }

    // A thumbnail was clicked: jump by its offset from the centre. A single jump, so no burst and no brake.
    private async void Thumbnail_Clicked(int shiftIndex) => await _photoController.FlyBy(shiftIndex);

    #endregion

    #region Zoom / Fit Toolbar

    // The fit toggle only turns fitting on: clicking it while fitted keeps it checked. The canvas reports when the
    // view stops being fitted, which unchecks it. The 1:1 toggle below works the same way.
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

    // The canvas has its device: the first photo can be decoded on it now.
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

    // Mouse/pen press on the image: left starts a pan drag, right arms the hold-to-zoom timer (if set).
    private void D2dCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Touch pans/pinches through the manipulation handlers; the mouse-drag path below would
        // jitter between contacts because each finger overwrites _lastPoint.
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return;

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
                // Not starting the timer leaves the release path to open the context menu as usual.
                if (AppConfig.Settings.RightClickHoldBehavior == RightClickHoldBehavior.ZoomIn)
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

    // Pans by how far the pointer moved since the last move, while a left-button drag is on.
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

    // Release: right shows the Explorer context menu (unless it was zooming), left ends a drag or is a click outside
    // the image, middle and the side buttons do what the settings say.
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

            // Touch goes through Tapped instead, which WinUI doesn't raise after a pan/pinch.
            case PointerUpdateKind.LeftButtonReleased when e.Pointer.PointerDeviceType != PointerDeviceType.Touch:
                HandleClickOutside(currentPoint.Position);
                break;

            case PointerUpdateKind.MiddleButtonReleased:
                switch (AppConfig.Settings.MiddleClickBehavior)
                {
                    case MiddleClickBehavior.FullScreen:
                        _windFullScreenManager.ToggleFullScreen(ButtonFullScreenClose);
                        break;
                    case MiddleClickBehavior.MaximizeRestore:
                        if (_windFullScreenManager.IsMaximizedOrFullScreen)
                            _windFullScreenManager.Restore(ButtonFullScreenClose);
                        else
                            _windFullScreenManager.Maximize();
                        break;
                }
                break;

            case PointerUpdateKind.XButton1Released:
            case PointerUpdateKind.XButton2Released:
                if (AppConfig.Settings.MouseFwdBackBehavior == MouseFwdBackBehavior.StepZoom)
                {
                    var isBack = (properties.PointerUpdateKind == PointerUpdateKind.XButton1Released)
                                 != AppConfig.Settings.SwapMouseFwdBack;
                    _canvasController.StepZoom(isBack ? ZoomDirection.Out : ZoomDirection.In, dpiAdjustedPosition);
                }
                break;
        }
    }

    // The wheel over the photo: navigate or zoom, by modifier keys and then the DefaultMouseWheelBehavior setting.
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
            // Shift + vertical scroll always zooms, in fine steps. Checked before Ctrl so Shift also
            // slows a touchpad pinch, which Windows delivers as Ctrl + wheel.
            case ScrollDirection.Vertical when Util.IsShiftPressed():
                _canvasController.ZoomAtPointFine(delta, currentPoint.AdjustForDpi(D2dCanvas));
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

    // A click on the background (not the image, not the title bar, not the second click of a double-click): restore
    // the window or close the app, as the ClickOutsideBehavior setting says.
    private void HandleClickOutside(Point rawPosition)
    {
        if (Environment.TickCount64 - _lastDoubleTappedAt < Win32Methods.GetDoubleClickTime()) return;

        if (rawPosition.Y < AppTitlebar.ActualHeight ||
            _canvasController.IsPressedOnImage(rawPosition.AdjustForDpi(D2dCanvas))) return;

        switch (AppConfig.Settings.ClickOutsideBehavior)
        {
            case ClickOutsideBehavior.RestoreWindow when _windFullScreenManager.IsMaximizedOrFullScreen:
                if (!AppConfig.Settings.SizeWindowToImageOnRestore || !TryRestoreWindowToImage())
                    _windFullScreenManager.Restore(ButtonFullScreenClose);
                break;
            case ClickOutsideBehavior.CloseApp:
                _ = AnimatePhotoDisplayWindowClose();
                break;
        }
    }

    // A touch tap: the touch version of a click outside the image.
    private void D2dCanvas_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Touch)
            HandleClickOutside(e.GetPosition(D2dCanvas));
    }

    // Mouse/pen manipulations are completed at once, so ManipulationDelta only ever sees touch.
    // Like a mouse drag, a touch gesture must start on the image; on the background it's left to
    // CtrlDragWindowMover (window move when restored, nothing when maximized).
    // Start position is the contacts' centroid just past the drag threshold, not the exact press point.
    private void D2dCanvas_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (e.PointerDeviceType != PointerDeviceType.Touch ||
            !_canvasController.IsPressedOnImage(e.Position.AdjustForDpi(D2dCanvas)))
        {
            e.Complete();
            return;
        }
        // A pinch whose first finger landed on the background started a window move; the image wins.
        _ctrlDragWindowMover.CancelDrag();
    }

    // A touch pan/pinch step.
    private void D2dCanvas_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var delta = e.Delta;
        // Pan(0,0) isn't a no-op (it clears the fitted state), so skip pure-scale events.
        if (delta.Translation.X != 0 || delta.Translation.Y != 0)
            _canvasController.Pan(delta.Translation.X.AdjustForDpi(D2dCanvas), delta.Translation.Y.AdjustForDpi(D2dCanvas));
        // Pan first, then scale about the contacts' current centroid so the pinch point stays under the fingers.
        _canvasController.ZoomAtPointByFactor(delta.Scale, e.Position.AdjustForDpi(D2dCanvas));
    }

    // Double-click/tap: on the image toggles 1:1 and fit; on the background maximizes a restored window.
    private void D2dCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Touch presses don't update _lastPointerDownKind, so the X-button check only applies to mouse.
        if (e.PointerDeviceType != PointerDeviceType.Touch &&
            _lastPointerDownKind is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed) return;
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
            if (AppConfig.Settings.ClickOutsideBehavior == ClickOutsideBehavior.RestoreWindow &&
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

    // Scrolling over the strip always navigates.
    private async void ThumbNail_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(D2dCanvasThumbNail).Properties;
        int delta = props.MouseWheelDelta;
        bool isHorizontal = props.IsHorizontalMouseWheel;
        await HandleMouseWheelNavigation(delta, isHorizontal);
    }

    // One step per ScrollThreshold of wheel travel. Each step is a press in a burst (HQ loading is held from the
    // second one on); the brake timer ends the burst once the wheel stops.
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

    // Zooms around the pointer: smoothly for a touchpad, one step per notch for a wheel.
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

    // Pushes the wheel burst's end back by another interval.
    private void RestartBrakeTimer()
    {
        _wheelScrollBrakeTimer.Stop();
        _wheelScrollBrakeTimer.Start();
    }

    // The wheel has been still for the interval: the wheel burst ends.
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

    // Every 16 ms while the right button stays held: zooms in a little around where it was pressed.
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

    // Opens an Explorer window with the photo selected.
    private void OpenFileInExplorer()
    {
        var filePath = _photoController.GetFullPathCurrentFile();
        if (File.Exists(filePath))
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }

    #endregion

    #region External App Shortcuts

    // Ctrl+1..4: opens the photo in the configured external app at index (0-based), if there is one.
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

    // A More-menu "Open with" item: its Tag is the app.
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

    // The "Open with" panel, made once; a click launches the app on the photo on screen at that moment.
    private ShortcutsFlyoutControl CreateShortcutsControl()
    {
        var control = new ShortcutsFlyoutControl();
        control.AppLaunchRequested += app => _ = app.LaunchAsync(_photoController.GetFullPathCurrentFile());
        return control;
    }

    // Rebuilds the "Open with" items at the top of the More menu (everything above SeparatorAfterOpenWith), each time
    // the menu opens, so it follows the current shortcut settings.
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

    // Shortens a long app name for the menu; the full name is in the tooltip.
    private static string TruncateAppName(string name, int maxLength = 24) =>
        name.Length > maxLength ? name[..maxLength] + "…" : name;

    #endregion

    #region Rename

    private async void MenuItemRename_OnClick(object sender, RoutedEventArgs e)
    {
        try { await ShowRenameFlyoutAsync(); }
        catch (Exception ex) { Logger.Error(ex); }
    }

    // Opens the rename box for the photo on screen. Refused (a shrug) while the controller says no: the listing
    // isn't published yet, a burst is on, or the HQ isn't on screen.
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

    // The photo on screen changed: a rename box open for another file closes.
    private void CloseRenameFlyoutIfFileChanged(string currentFilePath) =>
        _fileActionRename?.Close(currentFilePath);

    #endregion

    #region Delete

    private async void MenuItemDelete_OnClick(object sender, RoutedEventArgs e)
    {
        try { await DeleteCurrentlyDisplayedPhoto(); }
        catch (Exception ex) { Logger.Error(ex); }
    }

    // Deletes the photo on screen through FileActionDelete. Refused with "loading high quality" while the controller
    // says no; closes the app when the last photo is deleted.
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

    // Opens the settings window, or brings the open one forward.
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

    // A setting changed in the settings window (already stored in AppConfig.Settings): passes it on to whatever
    // needs to react now. Settings read fresh on every use need no case here.
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
                _photoController.InvalidateRawHqCache();
                break;
            case Setting.CacheSizeChange:
                _photoController.CacheSizeChanged();
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
            case Setting.AutoFadeToggle:
                _opacityFader.Enabled = AppConfig.Settings.AutoFade;
                break;
            case Setting.AutoHideMouseToggle:
                _mouseAutoHider.Enabled = AppConfig.Settings.AutoHideMouse;
                break;
            case Setting.KeyBindingsChanged:
                _routes = ShortcutCatalog.Resolve();
                break;
            case Setting.EdgeNavArrowsShowHide:
                _edgeNavArrowFader.Enabled = EdgeNavArrowsWanted();
                break;
        }
    }

    #endregion

    #region Cache Status

    // Shows or hides the cache status text.
    private void ButtonExpander_Click(object sender, RoutedEventArgs e)
    {
        _cacheStatusExpanded = !_cacheStatusExpanded;
        IconExpander.Text = ((char)(_cacheStatusExpanded ? 0xE760 : 0xE761)).ToString();
        CacheStatusProgress.Visibility = _cacheStatusExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    // Raised on the prefetch thread: posted to the UI thread.
    private void PhotoController_CacheStatusChanged(string cacheProgressStatus) =>
        DispatcherQueue.TryEnqueue(() => { CacheStatusProgress.Text = cacheProgressStatus; });

    #endregion

    #region State Callbacks

    // The photo on screen or its details changed: updates the file name bar and the window title, and closes the
    // info panel and rename box if they belong to another file. Raised on the UI thread; still queued, so it runs
    // after the navigation that raised it.
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

    /// <summary>Ctrl+PageUp / Ctrl+PageDown in Folder mode: open the sibling folder and flash its name.</summary>
    private async Task FlyToSiblingFolderAsync(NavDirection direction)
    {
        if (await _photoController.TryOpenSiblingFolderAsync(direction) is not { } folderName) return;
        TxtFolderName.Text = folderName;
        _folderNameFader.ReportActivity();
    }

    // Shows the zoom percentage briefly, if that setting is on. UI thread (the canvas marshals it).
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

    // Shows the page buttons only for a multi-page photo. UI thread.
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

    // The trial/license checks, made on first use; closes the app when the trial has expired.
    private ActionCheckLicense LicenseAction => field ??= new ActionCheckLicense(
        xamlRootProvider: () => Content?.XamlRoot, onTrialExpired: AnimatePhotoDisplayWindowClose);

    // FirstPhotoLoaded fires on the UI thread (when the listing is published); queued so the license check runs
    // after the current work.
    private void OnFirstPhotoLoaded() => DispatcherQueue.TryEnqueue(async () =>
    {
        try { await LicenseAction.PhotoLoadedAsync(); }
        catch (Exception ex) { Logger.Error(ex); }
    });

    // The window's content is loaded: the license check's window-loaded step.
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
        // A run is in flight. UI thread only, so a plain bool.
        private bool _isRunning;
        /// <summary>Runs <paramref name="action"/> unless a run is already in flight.</summary>
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
