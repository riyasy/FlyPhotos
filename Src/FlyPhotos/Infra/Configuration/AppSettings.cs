
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using FlyPhotos.Core.Model;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Infra.Configuration;

public class AppSettings
{
    [JsonPropertyName("Theme")] public string ThemeAsString { get; set; } = "Default";

    [JsonIgnore]
    public ElementTheme Theme
    {
        get => Enum.Parse<ElementTheme>(ThemeAsString, true);
        set => ThemeAsString = value.ToString();
    }

    [JsonPropertyName("WindowBackdrop")] public string WindowBackdropAsString { get; set; } = "Transparent";

    [JsonIgnore]
    public WindowBackdropType WindowBackdrop
    {
        get => Enum.Parse<WindowBackdropType>(WindowBackdropAsString, true);
        set => WindowBackdropAsString = value.ToString();
    }

    [JsonPropertyName("DefaultMouseWheelBehavior")] public string DefaultMouseWheelBehaviorAsString { get; set; } = "Zoom";

    [JsonIgnore]
    public DefaultMouseWheelBehavior DefaultMouseWheelBehavior
    {
        get => Enum.Parse<DefaultMouseWheelBehavior>(DefaultMouseWheelBehaviorAsString, true);
        set => DefaultMouseWheelBehaviorAsString = value.ToString();
    }

    [JsonPropertyName("PanZoomBehaviourOnNavigation")]
    public string PanZoomBehaviourOnNavigationAsString { get; set; } = "Reset";

    [JsonIgnore]
    public PanZoomBehaviourOnNavigation PanZoomBehaviourOnNavigation
    {
        get => Enum.Parse<PanZoomBehaviourOnNavigation>(PanZoomBehaviourOnNavigationAsString, true);
        set => PanZoomBehaviourOnNavigationAsString = value.ToString();
    }

    public int CacheSizeOneSideHqImages { get; set; } = 2;
    public int CacheSizeOneSidePreviews { get; set; } = 200;
    public bool ShowThumbnails { get; set; } = true;
    public string ThumbnailSelectionColor { get; set; } = "#ADFF2F";
    public bool AutoFade { get; set; } = true;
    public int FadeIntensity { get; set; } = 60;
    public bool OpenExitZoom { get; set; } = false;
    public bool HighQualityInterpolation { get; set; } = true;
    public bool CheckeredBackground { get; set; } = false;
    public int ImageFitPercentage { get; set; } = 100;
    public bool StretchSmallImages { get; set; } = false;
    public int TransparentBackgroundIntensity { get; set; } = 40;
    public int ThumbnailSize { get; set; } = 40;
    public int ScrollThreshold { get; set; } = 60;
    [JsonPropertyName("MouseFwdBackBehavior")] public string MouseFwdBackBehaviorAsString { get; set; } = "Navigate";

    [JsonIgnore]
    public MouseFwdBackBehavior MouseFwdBackBehavior
    {
        get => Enum.TryParse<MouseFwdBackBehavior>(MouseFwdBackBehaviorAsString, true, out var result) ? result : MouseFwdBackBehavior.Navigate;
        set => MouseFwdBackBehaviorAsString = value.ToString();
    }
    public bool ConfirmForDelete { get; set; } = true;
    public bool ShowFileName { get; set; } = true;
    public bool ShowCacheStatus { get; set; } = true;
    public bool ShowImageDimensions { get; set; } = false;
    public bool AutoHideMouse { get; set; } = false;

    public bool UseExternalExeForContextMenu { get; set; } = false;

    public bool ShowExternalAppShortcuts { get; set; } = false;
    public string ExternalApp1 { get; set; } = string.Empty;
    public string ExternalApp2 { get; set; } = string.Empty;
    public string ExternalApp3 { get; set; } = string.Empty;
    public string ExternalApp4 { get; set; } = string.Empty;
    // Show zoom percentage overlay in the center of the photo display
    public bool ShowZoomPercent { get; set; } = true;
    public bool DecodeRawData { get; set; } = false;
    public string Language { get; set; } = "Default";
    [JsonPropertyName("WindowLaunchMode")] public string WindowLaunchModeAsString { get; set; } = "Maximized";

    [JsonIgnore]
    public WindowLaunchMode WindowLaunchMode
    {
        get => Enum.TryParse<WindowLaunchMode>(WindowLaunchModeAsString, true, out var result) ? result : WindowLaunchMode.Maximized;
        set => WindowLaunchModeAsString = value.ToString();
    }
    public string WindowState { get; set; } = "";
    public bool AllowMultiInstance { get; set; } = false;

    [JsonPropertyName("RawDecoderPriority")]
    public ObservableCollection<string> RawDecoderPriorityAsStrings { get; set; } =
        [nameof(RawDecoder.WIC), nameof(RawDecoder.Rawler), nameof(RawDecoder.ImageMagick)];

    [JsonIgnore]
    public List<RawDecoder> RawDecoderPriority =>
        RawDecoderPriorityAsStrings
            .Select(s => Enum.Parse<RawDecoder>(s, true))
            .ToList();

}