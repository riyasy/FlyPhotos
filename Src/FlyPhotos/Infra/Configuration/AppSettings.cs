using System;
using System.Text.Json.Serialization;
using FlyPhotos.Core.Model;
using Microsoft.UI.Xaml;

namespace FlyPhotos.Infra.Configuration;

public class AppSettings
{
    [JsonPropertyName("Theme")] public string ThemeAsString { get; set; } = "Default";

    [JsonPropertyName("Language")] public string Language { get; set; } = "Default";

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
    public int CacheSizeOneSidePreviews { get; set; } = 300;
    public bool ShowThumbnails { get; set; } = true;
    public string ThumbnailSelectionColor { get; set; } = "#ADFF2F";
    public bool AutoFade { get; set; } = true;
    public int FadeIntensity { get; set; } = 60;
    public bool OpenExitZoom { get; set; } = false;
    public bool HighQualityInterpolation { get; set; } = true;
    public bool CheckeredBackground { get; set; } = false;
    public int ImageFitPercentage { get; set; } = 100;
    public int TransparentBackgroundIntensity { get; set; } = 40;
    public int ThumbnailSize { get; set; } = 40;
    public ulong LastUsedMonitorId { get; set; } = 0;
    public bool RememberLastMonitor { get; set; } = false;
    public int ScrollThreshold { get; set; } = 60;
    public bool UseMouseFwdBackForStepZoom { get; set; } = false;
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
}