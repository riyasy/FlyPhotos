using System;
using System.Text.Json.Serialization;
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


    public bool ResetPanZoomOnNextPhoto { get; set; }
    public int CacheSizeOneSideHqImages { get; set; }
    public int CacheSizeOneSidePreviews { get; set; }
    public bool ShowThumbnails { get; set; }
    public bool AutoFade { get; set; }
    public int FadeIntensity { get; set; }
    public bool OpenExitZoom { get; set; }
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