using System;
using System.Text.Json.Serialization;
using FlyPhotos.Data;
using Microsoft.UI.Xaml;

namespace FlyPhotos.AppSettings;

public class AppSettings
{
    [JsonPropertyName("Theme")] public string ThemeAsString { get; set; } = "Default";

    [JsonIgnore]
    public ElementTheme Theme
    {
        get => Enum.Parse<ElementTheme>(ThemeAsString, true);
        set => ThemeAsString = value.ToString();
    }

    [JsonPropertyName("WindowBackdrop")] public string WindowBackdropAsString { get; set; } = "Default";

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
}

