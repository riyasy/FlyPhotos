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
    Frozen,
    Custom
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

public enum MiddleClickBehavior
{
    FullScreen,
    MaximizeRestore,
    Nothing
}

public enum ClickOutsideBehavior
{
    RestoreWindow,
    CloseApp,
    Nothing
}

public enum RightClickHoldBehavior
{
    ZoomIn,
    Nothing
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
    AutoFadeToggle,
    AutoHideMouseToggle,
    ImageScalingQualityChange,
    RawDecodingChange,
    KeyBindingsChanged,
    EdgeNavArrowsShowHide
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

/// <summary>Where the photo list comes from when a photo is opened.</summary>
public enum ListSource
{
    /// <summary>The items, order and filter of the Explorer window the photo was opened from.</summary>
    Explorer,
    /// <summary>The supported images in the photo's folder, sorted by Fly and watched for changes.</summary>
    Folder
}

/// <summary>The sort key for <see cref="ListSource.Folder"/>.</summary>
public enum FolderSortBy
{
    Name,
    DateModified,
    DateCreated,
    Size
}
