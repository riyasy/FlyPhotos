namespace FlyPhotos.Data;

public class SettingsData
{
    public string Theme { get; set; }
    public string WindowBackGround { get; set; }
    public bool ResetPanZoomOnNextPhoto { get; set; }
    public int CacheSizeOneSideHqImages { get; set; }
    public int CacheSizeOneSidePreviews { get; set; }
    public bool ShowThumbNails { get; set; }

    public bool AutoFade { get; set; }
    public int FadeIntensity { get; set; }

    public static SettingsData Default()
    {
        var defaultSettings = new SettingsData
        {
            Theme = "Light",
            WindowBackGround = "Transparent",
            ResetPanZoomOnNextPhoto = false,
            CacheSizeOneSideHqImages = 2,
            CacheSizeOneSidePreviews = 300,
            ShowThumbNails = true,
            AutoFade = true,
            FadeIntensity = 60,
        };
        return defaultSettings;
    }
}