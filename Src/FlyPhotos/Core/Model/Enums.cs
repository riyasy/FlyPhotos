using System.Text.Json.Serialization;

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
    CtrlDragToMoveWindowToggle,
    AutoFadeToggle,
    AutoHideMouseToggle,
    ImageScalingQualityChange,
    RawDecodingChange
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

// JsonStringEnumConverter is declared here (not on the property) so that element-level
// serialization works when this enum appears inside a collection (ObservableCollection<RawDecoder>).
[JsonConverter(typeof(JsonStringEnumConverter<RawDecoder>))]
public enum RawDecoder { Rawler, WIC, ImageMagick }

public enum WindowLaunchMode
{
    Maximized,
    FullScreen,
    LastWindowState
}

public enum ImageInterpolation
{
    NearestNeighbor,
    Linear,
    Anisotropic,
    HighQualityCubic
}
