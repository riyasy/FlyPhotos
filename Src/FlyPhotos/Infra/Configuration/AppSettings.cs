
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using FlyPhotos.Core.Model;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Infra.Configuration;

public class AppSettings
{
    // ElementTheme is a WinUI/SDK enum — converter must be at property level
    [JsonConverter(typeof(JsonStringEnumConverter<ElementTheme>))]
    public ElementTheme Theme { get; set; } = ElementTheme.Default;

    [JsonConverter(typeof(JsonStringEnumConverter<WindowBackdropType>))]
    public WindowBackdropType WindowBackdrop { get; set; } = WindowBackdropType.Transparent;

    [JsonConverter(typeof(JsonStringEnumConverter<DefaultMouseWheelBehavior>))]
    public DefaultMouseWheelBehavior DefaultMouseWheelBehavior { get; set; } = DefaultMouseWheelBehavior.Zoom;

    [JsonConverter(typeof(JsonStringEnumConverter<PanZoomBehaviourOnNavigation>))]
    public PanZoomBehaviourOnNavigation PanZoomBehaviourOnNavigation { get; set; } = PanZoomBehaviourOnNavigation.Reset;

    public int CacheSizeOneSideHqImages { get; set; } = 2;
    public int CacheSizeOneSidePreviews { get; set; } = 200;
    public bool ShowThumbnails { get; set; } = true;
    public string ThumbnailSelectionColor { get; set; } = "#ADFF2F";
    public bool AutoFade { get; set; } = true;
    public int FadeIntensity { get; set; } = 60;
    public bool OpenExitZoom { get; set; } = false;

    [JsonConverter(typeof(JsonStringEnumConverter<ImageInterpolation>))]
    public ImageInterpolation ImageScalingQuality { get; set; } = ImageInterpolation.Linear;

    public bool CheckeredBackground { get; set; } = false;
    public int ImageFitPercentage { get; set; } = 100;
    public bool StretchSmallImages { get; set; } = false;
    public int TransparentBackgroundIntensity { get; set; } = 40;
    public int ThumbnailSize { get; set; } = 40;
    public int ScrollThreshold { get; set; } = 60;

    [JsonConverter(typeof(JsonStringEnumConverter<MouseFwdBackBehavior>))]
    public MouseFwdBackBehavior MouseFwdBackBehavior { get; set; } = MouseFwdBackBehavior.Navigate;

    public bool ConfirmForDelete { get; set; } = true;
    public bool ShowFileName { get; set; } = true;
    public bool ShowCacheStatus { get; set; } = true;
    public bool ShowImageDimensions { get; set; } = false;
    public bool AutoHideMouse { get; set; } = false;
    public bool AutoHideCaptionButtons { get; set; } = false;
    public bool ClickOutsideImageToRestoreWindow { get; set; } = true;
    public bool CtrlDragToMoveWindow { get; set; } = true;
    public bool UseExternalExeForContextMenu { get; set; } = false;
    public bool ShowExternalAppShortcuts { get; set; } = false;
    public string ExternalApp1 { get; set; } = string.Empty;
    public string ExternalApp2 { get; set; } = string.Empty;
    public string ExternalApp3 { get; set; } = string.Empty;
    public string ExternalApp4 { get; set; } = string.Empty;
    public bool ShowZoomPercent { get; set; } = true;
    public bool DecodeRawData { get; set; } = false;
    public string Language { get; set; } = "Default";

    [JsonConverter(typeof(JsonStringEnumConverter<WindowLaunchMode>))]
    public WindowLaunchMode WindowLaunchMode { get; set; } = WindowLaunchMode.Maximized;

    public string WindowState { get; set; } = "";
    public bool AllowMultiInstance { get; set; } = false;
    public bool UseSubPixelSnapping { get; set; } = true;
    public bool StickyZoomLevels { get; set; } = true;

    // String serialization for elements is handled by [JsonConverter] on the RawDecoder type,
    // since property-level JsonStringEnumConverter<T> does not apply to collection elements.
    public ObservableCollection<RawDecoder> RawDecoderPriority { get; set; } =
        [RawDecoder.WIC, RawDecoder.Rawler, RawDecoder.ImageMagick];
}
