namespace FlyPhotos.Core.Model;

public enum DisplayLevel
{
    PlaceHolder,
    Preview,
    Hq
}

public enum NavDirection
{
    Next,
    Prev
}

public enum Origin
{
    DiskCache,
    Disk,
    ErrorScreen,
    Undefined
}

public enum WindowBackdropType
{
    None,
    Mica,
    MicaAlt,
    Acrylic,
    AcrylicThin,
    Transparent,
    Frozen
}

public enum ZoomDirection
{
    In,
    Out
}

public enum DefaultMouseWheelBehavior
{
    Zoom,
    Navigate
}

public enum MouseFwdBackBehavior
{
    Navigate,
    StepZoom
}

public enum Setting
{
    ThumbnailShowHide,
    ThumbnailSelectionColor,
    ThumbnailSizeSize,
    CheckeredBackgroundShowHide,
    Theme,
    BackDrop,
    BackDropTransparency,
    FileNameShowHide,
    CacheStatusShowHide,
    ExtShortcutsShowHide,
    ImageDimensionsShowHide,
    CaptionButtonsAutoHideToggle,
    CtrlDragToMoveWindowToggle
}

public enum ScrollDirection
{
    Horizontal,
    Vertical
}

public enum PanZoomBehaviourOnNavigation
{
    Reset,
    RememberPerPhoto,
    RetainFromLastPhoto
}

public enum RawDecoder { Rawler, WIC, ImageMagick }

public enum WindowLaunchMode
{
    Maximized,
    FullScreen,
    LastWindowState
}