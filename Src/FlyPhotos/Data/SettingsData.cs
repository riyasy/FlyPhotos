using Microsoft.UI.Xaml;

namespace FlyPhotos.Data;

public class SettingsData
{
    public ElementTheme Theme { get; set; }
    public WindowBackdropType WindowBackGround { get; set; }
    public bool ResetPanZoomOnNextPhoto { get; set; }
    public int CacheSizeOneSideHqImages { get; set; }
    public int CacheSizeOneSidePreviews { get; set; }
    public bool ShowThumbNails { get; set; }

    public bool AutoFade { get; set; }
    public int FadeIntensity { get; set; }
    public bool OpenExitZoom { get; set; }

    public static SettingsData Default()
    {
        var defaultSettings = new SettingsData
        {
            Theme = ElementTheme.Default,
            WindowBackGround = WindowBackdropType.Transparent,
            ResetPanZoomOnNextPhoto = false,
            CacheSizeOneSideHqImages = 2,
            CacheSizeOneSidePreviews = 300,
            ShowThumbNails = true,
            AutoFade = true,
            FadeIntensity = 60,
            OpenExitZoom = false,
        };
        return defaultSettings;
    }
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