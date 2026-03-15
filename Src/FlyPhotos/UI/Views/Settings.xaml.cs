#nullable enable
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;
using FlyPhotos.Services.ExternalAppListing;
using FlyPhotos.UI.Behaviors;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Microsoft.UI.Composition;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.UI.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
internal sealed partial class Settings
{
    public event Action<Setting>? SettingChanged;

    private readonly List<LanguageInfo> _supportedLanguages = [];

    private readonly WindowAppearanceManager _windAppearanceManager;

    internal Settings()
    {
        InitializeComponent();

        // Title property is used only by TaskBar label. Actual TitleBar is customized using AppWindow.TitleBar.
        Title = L.Get("SettingsPage/Title").Replace("FlyPhotos - ", string.Empty);

        _windAppearanceManager = new WindowAppearanceManager(this, false);
        _windAppearanceManager.SetupTransparentTitleBar(null);

        SettingsCardVersion.Description = 
            string.Format(L.Get("SettingsCardVersion/Description"), Constants.AppVersion);

        Util.SetWindowIcon(this);

        MainLayout.Loaded += Settings_Loaded;

        DispatcherQueue.EnsureSystemDispatcherQueue();

        Closed += (_, _) => _windAppearanceManager.Dispose();

        SliderHighResCacheSize.Value = AppConfig.Settings.CacheSizeOneSideHqImages;
        SliderLowResCacheSize.Value = AppConfig.Settings.CacheSizeOneSidePreviews;
        // Use index-based mapping for localized combo box items. Settings are still saved as enum names in AppSettings.
        ComboTheme.SelectedIndex = GetIndexForTheme(AppConfig.Settings.Theme);
        ComboBackGround.SelectedIndex = GetIndexForBackdrop(AppConfig.Settings.WindowBackdrop);
        ComboMouseWheelBehaviour.SelectedIndex = GetIndexForMouseWheelBehaviour(AppConfig.Settings.DefaultMouseWheelBehavior);
        ButtonShowThumbnail.IsOn = AppConfig.Settings.ShowThumbnails;
        ButtonEnableAutoFade.IsOn = AppConfig.Settings.AutoFade;
        ButtonOpenExitZoom.IsOn = AppConfig.Settings.OpenExitZoom;
        SliderFadeIntensity.Value = AppConfig.Settings.FadeIntensity;
        ButtonHighQualityInterpolation.IsOn = AppConfig.Settings.HighQualityInterpolation;
        ButtonShowZoomPercent.IsOn = AppConfig.Settings.ShowZoomPercent;
        ButtonShowCheckeredBackground.IsOn = AppConfig.Settings.CheckeredBackground;
        SliderImageFitPercentage.Value = AppConfig.Settings.ImageFitPercentage;
        SliderTransparentBackgroundIntensity.Value = AppConfig.Settings.TransparentBackgroundIntensity;
        RectThumbnailSelection.Stroke = new SolidColorBrush(ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor));
        SliderThumbnailSize.Value = AppConfig.Settings.ThumbnailSize;
        ButtonRememberLastWindowState.IsOn = AppConfig.Settings.RememberLastWindowState;
        ButtonAllowMultiInstance.IsOn = AppConfig.Settings.AllowMultiInstance;
        ButtonConfirmBeforeDelete.IsOn = AppConfig.Settings.ConfirmForDelete;
        ButtonShowFileName.IsOn = AppConfig.Settings.ShowFileName;
        ButtonShowCacheStatusExpander.IsOn = AppConfig.Settings.ShowCacheStatus;
        ButtonShowImageDimensions.IsOn = AppConfig.Settings.ShowImageDimensions;
        ComboPanZoomNavBehaviour.SelectedIndex = GetIndexForPanZoomBehaviour(AppConfig.Settings.PanZoomBehaviourOnNavigation);
        ButtonEnableAutoHideMouse.IsOn = AppConfig.Settings.AutoHideMouse;
        ButtonEnableExternalShortcut.IsOn = AppConfig.Settings.ShowExternalAppShortcuts;

        MainLayout.KeyDown += MainLayout_OnKeyDown;
        SliderHighResCacheSize.ValueChanged += SliderHighResCacheSize_OnValueChanged;
        SliderLowResCacheSize.ValueChanged += SliderLowResCacheSize_OnValueChanged;
        ComboTheme.SelectionChanged += ComboTheme_OnSelectionChanged;
        ComboBackGround.SelectionChanged += ComboBackGround_OnSelectionChanged;
        ComboMouseWheelBehaviour.SelectionChanged += ComboMouseWheel_OnSelectionChanged;
        ButtonShowThumbnail.Toggled += ButtonShowThumbnail_OnToggled;
        ButtonOpenExitZoom.Toggled += ButtonOpenExitZoom_OnToggled;
        ButtonEnableAutoFade.Toggled += ButtonEnableAutoFade_OnToggled;
        SliderFadeIntensity.ValueChanged += SliderFadeIntensity_ValueChanged;
        ButtonHighQualityInterpolation.Toggled += ButtonHighQualityInterpolation_OnToggled;
        ButtonShowZoomPercent.Toggled += ButtonShowZoomPercent_OnToggled;
        ButtonShowCheckeredBackground.Toggled += ButtonShowCheckeredBackground_OnToggled;
        SliderImageFitPercentage.ValueChanged += SliderImageFitPercentage_ValueChanged;
        SliderTransparentBackgroundIntensity.ValueChanged += SliderTransparentBackgroundIntensity_ValueChanged;
        SliderThumbnailSize.ValueChanged += SliderThumbnailSize_ValueChanged;
        ButtonRememberLastWindowState.Toggled += ButtonRememberLastWindowState_OnToggled;
        ButtonAllowMultiInstance.Toggled += ButtonAllowMultiInstance_OnToggled;
        ButtonConfirmBeforeDelete.Toggled += ButtonConfirmBeforeDelete_OnToggled;
        ButtonShowFileName.Toggled += ButtonShowFileName_OnToggled;
        ButtonShowImageDimensions.Toggled += ButtonShowImageDimensions_OnToggled;
        ButtonShowCacheStatusExpander.Toggled += ButtonShowCacheStatusExpander_OnToggled;
        ComboPanZoomNavBehaviour.SelectionChanged += ComboPanZoomNavBehaviour_OnSelectionChanged;
        ButtonEnableAutoHideMouse.Toggled += ButtonEnableAutoHideMouse_OnToggled;
        ButtonEnableExternalShortcut.Toggled += ButtonEnableExternalShortcut_OnToggled;

        // Initialize codec list view
        ListViewCodecs.ItemsSource = CodecDiscovery.GetAllCodecs();

        PopulateSupportedLanguages();
        ComboLanguage.ItemsSource = _supportedLanguages;
        ComboLanguage.SelectedItem = _supportedLanguages.FirstOrDefault(l => l.LanguageCode == AppConfig.Settings.Language);
        ComboLanguage.SelectionChanged += ComboLanguage_SelectionChanged;

        if (PathResolver.IsPackagedApp)
        {
            SettingsCardDeveloperSupport.Visibility = Visibility.Collapsed;
            BtnCheckVersion.Visibility = Visibility.Collapsed;
        }

        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumWidth = 600;
        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumHeight = 600;
    }

    private async void ButtonEnableExternalShortcut_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowExternalAppShortcuts = ButtonEnableExternalShortcut.IsOn;
        SettingChanged?.Invoke(Setting.ExtShortcutsShowHide);
        await AppConfig.SaveAsync();
    }

    private async void Settings_Loaded(object sender, RoutedEventArgs e)
    {
        await SetButtonIconFromAppData(BtnShortcut1, AppConfig.Settings.ExternalApp1);
        await SetButtonIconFromAppData(BtnShortcut2, AppConfig.Settings.ExternalApp2);
        await SetButtonIconFromAppData(BtnShortcut3, AppConfig.Settings.ExternalApp3);
        await SetButtonIconFromAppData(BtnShortcut4, AppConfig.Settings.ExternalApp4);

        // StartHeartBeat();
    }

    private static async Task SetButtonIconFromAppData(Button btnShortcut, string appShortCut)
    {
        if (string.IsNullOrEmpty(appShortCut)) return;
        var app = await ShellAppProvider.GetAppAsync(appShortCut);
        if (app == null) return;
        
        var bmp = app.Icon;
        btnShortcut.Content = bmp != null
            ? new Image { Source = bmp, Width = 32, Height = 32 }
            : new FontIcon { Glyph = "\uED35", FontSize = 32 }; // Default icon
        
    }

    private async void ButtonEnableAutoHideMouse_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.AutoHideMouse = ButtonEnableAutoHideMouse.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void ComboPanZoomNavBehaviour_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var panZoomEnum = GetPanZoomForIndex(ComboPanZoomNavBehaviour.SelectedIndex);
        AppConfig.Settings.PanZoomBehaviourOnNavigation = panZoomEnum;
        await AppConfig.SaveAsync();
    }
    
    private async void ButtonShowCacheStatusExpander_OnToggled(object s, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowCacheStatus = ButtonShowCacheStatusExpander.IsOn;
        SettingChanged?.Invoke(Setting.CacheStatusShowHide);
        await AppConfig.SaveAsync();
    }

    private async void ButtonShowFileName_OnToggled(object s, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowFileName = ButtonShowFileName.IsOn;
        SettingChanged?.Invoke(Setting.FileNameShowHide);
        await AppConfig.SaveAsync();
    }

    private async void ButtonShowImageDimensions_OnToggled(object s, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowImageDimensions = ButtonShowImageDimensions.IsOn;
        SettingChanged?.Invoke(Setting.ImageDimensionsShowHide);
        await AppConfig.SaveAsync();
    }

    private async void ButtonConfirmBeforeDelete_OnToggled(object s, RoutedEventArgs e)
    {
        AppConfig.Settings.ConfirmForDelete = ButtonConfirmBeforeDelete.IsOn;
        await AppConfig.SaveAsync();
    }


    private async void ButtonRememberLastWindowState_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.RememberLastWindowState = ButtonRememberLastWindowState.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void ButtonAllowMultiInstance_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.AllowMultiInstance = ButtonAllowMultiInstance.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void SliderThumbnailSize_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.ThumbnailSize = (int)SliderThumbnailSize.Value;
        SettingChanged?.Invoke(Setting.ThumbnailSizeSize);
        await AppConfig.SaveAsync();
    }

    private async void SliderTransparentBackgroundIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.TransparentBackgroundIntensity = (int)SliderTransparentBackgroundIntensity.Value;
        SettingChanged?.Invoke(Setting.BackDropTransparency);
        await AppConfig.SaveAsync();
    }

    private async void SliderImageFitPercentage_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.ImageFitPercentage = (int)SliderImageFitPercentage.Value;
        await AppConfig.SaveAsync();
    }

    private async void ButtonShowCheckeredBackground_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.CheckeredBackground = ButtonShowCheckeredBackground.IsOn;
        SettingChanged?.Invoke(Setting.CheckeredBackgroundShowHide);
        await AppConfig.SaveAsync();
    }

    private async void ButtonHighQualityInterpolation_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.HighQualityInterpolation = ButtonHighQualityInterpolation.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void ButtonShowZoomPercent_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowZoomPercent = ButtonShowZoomPercent.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void ButtonOpenExitZoom_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.OpenExitZoom = ButtonOpenExitZoom.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void ButtonEnableAutoFade_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.AutoFade = ButtonEnableAutoFade.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void SliderFadeIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.FadeIntensity = (int)SliderFadeIntensity.Value;
        await AppConfig.SaveAsync();
    }

    private async void ComboTheme_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var themeEnum = GetThemeForIndex(ComboTheme.SelectedIndex);
        AppConfig.Settings.Theme = themeEnum;

        _windAppearanceManager.SetWindowTheme(themeEnum);
        SettingChanged?.Invoke(Setting.Theme);
        await AppConfig.SaveAsync();
    }

    private async void ComboBackGround_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var backGroundEnum = GetBackdropForIndex(ComboBackGround.SelectedIndex);
        AppConfig.Settings.WindowBackdrop = backGroundEnum;
        SettingChanged?.Invoke(Setting.BackDrop);
        await AppConfig.SaveAsync();
    }
    private async void ComboMouseWheel_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var mouseWheelEnum = GetMouseWheelForIndex(ComboMouseWheelBehaviour.SelectedIndex);
        AppConfig.Settings.DefaultMouseWheelBehavior = mouseWheelEnum;
        await AppConfig.SaveAsync();
    }

    private async void ButtonShowThumbnail_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowThumbnails = ButtonShowThumbnail.IsOn;
        await AppConfig.SaveAsync();
        SettingChanged?.Invoke(Setting.ThumbnailShowHide);
    }

    private async void SliderLowResCacheSize_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.CacheSizeOneSidePreviews = (int)SliderLowResCacheSize.Value;
        await AppConfig.SaveAsync();
    }

    private async void SliderHighResCacheSize_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.CacheSizeOneSideHqImages = (int)SliderHighResCacheSize.Value;
        await AppConfig.SaveAsync();
    }

    private void ButtonOpenLog_OnClick(object sender, RoutedEventArgs e)
    {
        var logFilePath = Path.Combine(PathResolver.GetLogFolderPath(), "FlyPhotos.log");
        if (File.Exists(logFilePath))
            Process.Start("notepad.exe", logFilePath);
    }

    private void MainLayout_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Close();
    }

    private bool ShouldEnableTransparencySlider(int index)
    {
        return index == 0;
    }

    // Mapping helpers to use index-based combo box handling while keeping AppSettings stored as enum names
    private static int GetIndexForTheme(ElementTheme theme)
    {
        return theme switch
        {
            ElementTheme.Default => 0,
            ElementTheme.Dark => 1,
            ElementTheme.Light => 2,
            _ => 0
        };
    }

    private static ElementTheme GetThemeForIndex(int index)
    {
        return index switch
        {
            1 => ElementTheme.Dark,
            2 => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private static int GetIndexForBackdrop(WindowBackdropType backdrop)
    {
        return backdrop switch
        {
            WindowBackdropType.Transparent => 0,
            WindowBackdropType.Frozen => 1,
            WindowBackdropType.Acrylic => 2,
            WindowBackdropType.AcrylicThin => 3,
            WindowBackdropType.Mica => 4,
            WindowBackdropType.MicaAlt => 5,
            WindowBackdropType.None => 6,
            _ => 0
        };
    }

    private static WindowBackdropType GetBackdropForIndex(int index)
    {
        return index switch
        {
            1 => WindowBackdropType.Frozen,
            2 => WindowBackdropType.Acrylic,
            3 => WindowBackdropType.AcrylicThin,
            4 => WindowBackdropType.Mica,
            5 => WindowBackdropType.MicaAlt,
            6 => WindowBackdropType.None,
            _ => WindowBackdropType.Transparent,
        };
    }

    private static int GetIndexForMouseWheelBehaviour(DefaultMouseWheelBehavior behaviour)
    {
        return behaviour switch
        {
            DefaultMouseWheelBehavior.Zoom => 0,
            DefaultMouseWheelBehavior.Navigate => 1,
            _ => 0
        };
    }

    private static DefaultMouseWheelBehavior GetMouseWheelForIndex(int index)
    {
        return index == 1 ? DefaultMouseWheelBehavior.Navigate : DefaultMouseWheelBehavior.Zoom;
    }

    private static int GetIndexForPanZoomBehaviour(PanZoomBehaviourOnNavigation behaviour)
    {
        return behaviour switch
        {
            PanZoomBehaviourOnNavigation.Reset => 0,
            PanZoomBehaviourOnNavigation.RememberPerPhoto => 1,
            PanZoomBehaviourOnNavigation.RetainFromLastPhoto => 2,
            _ => 0
        };
    }

    private static PanZoomBehaviourOnNavigation GetPanZoomForIndex(int index)
    {
        return index switch
        {
            1 => PanZoomBehaviourOnNavigation.RememberPerPhoto,
            2 => PanZoomBehaviourOnNavigation.RetainFromLastPhoto,
            _ => PanZoomBehaviourOnNavigation.Reset,
        };
    }

    private async void ColorFlyOutOkButton_Click(object sender, RoutedEventArgs e)
    {
        Windows.UI.Color newColor = FlyoutColorPicker.Color;
        RectThumbnailSelection.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, newColor.R, newColor.G, newColor.B));
        ColorPickerFlyout.Hide();
        AppConfig.Settings.ThumbnailSelectionColor = $"#{newColor.R:X2}{newColor.G:X2}{newColor.B:X2}";
        SettingChanged?.Invoke(Setting.ThumbnailSelectionColor);
        await AppConfig.SaveAsync();
    }

    private void ColorFlyOutCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPickerFlyout.Hide();
    }

    private void RectThumbnailSelection_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var currentColor = ((SolidColorBrush)RectThumbnailSelection.Stroke).Color;
        FlyoutColorPicker.Color = currentColor;
        FlyoutBase.ShowAttachedFlyout(ButtonSetThumbnailSelColor);
    }

    private async void OnShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AppSelectionDialog(this)
        {
            XamlRoot = Content.XamlRoot,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ((FrameworkElement)Content).ActualTheme
        };

        await dialog.ShowAsync();

        if (dialog.SelectedApp == null) return;

        var button = (Button)sender;
        SetShortCutSettingForButton(button, dialog.SelectedApp.GetSerializedState());
        await AppConfig.SaveAsync();

        var bmp = dialog.SelectedApp.Icon;
        button.Content = bmp != null
            ? new Image { Source = bmp, Width = 32, Height = 32 }
            : new FontIcon { Glyph = "\uED35", FontSize = 32 };
    }

    private static void SetShortCutSettingForButton(Button button, string shortCut)
    {
        switch (button.Name)
        {
            case "BtnShortcut1":
                AppConfig.Settings.ExternalApp1 = shortCut;
                break;
            case "BtnShortcut2":
                AppConfig.Settings.ExternalApp2 = shortCut;
                break;
            case "BtnShortcut3":
                AppConfig.Settings.ExternalApp3 = shortCut;
                break;
            case "BtnShortcut4":
                AppConfig.Settings.ExternalApp4 = shortCut;
                break;
        }
    }

    private async void ButtonThirdPartyLicenses_Click(object sender, RoutedEventArgs e)
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices.txt");
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        await Launcher.LaunchFileAsync(file);
    }

    private async void ComboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboLanguage.SelectedValue is LanguageInfo info)
            AppConfig.Settings.Language = info.LanguageCode;
        await AppConfig.SaveAsync();
        await ShowMessageDialog(L.Get("ChangeLanguageAlert/Title"), L.Get("ChangeLanguageAlert/Description"));
    }
    private void PopulateSupportedLanguages()
    {
        _supportedLanguages.Clear();
        foreach (var code in Constants.SupportedLanguages)
        {
            try
            {
                var culture = new CultureInfo(code);
                // Use Parent to get the neutral culture name (e.g., "German" instead of "German (Germany)")
                var neutral = culture.Parent.Name is { Length: > 0 } ? culture.Parent : culture;
                var display = neutral.DisplayName;
                var native = neutral.NativeName;
                _supportedLanguages.Add(new LanguageInfo($"{display} [{native}]", native, code));
            }
            catch (CultureNotFoundException)
            {
                Debug.WriteLine($"Invalid culture code: {code}");
            }
        }
    }

    private async Task ShowMessageDialog(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = L.Get("MessageClose_Ok"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    void StartHeartBeat()
    {
        var heartVisual = ElementCompositionPreview.GetElementVisual(HeartIcon);
        if (heartVisual == null) return;
        var compositor = heartVisual.Compositor;
        // Ensure CenterPoint is set correctly. 
        heartVisual.CenterPoint = new Vector3((float)HeartIcon.ActualWidth / 2, (float)HeartIcon.ActualHeight / 2, 0);
        // Use a single Vector3 animation instead of two Scalar animations
        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        // Set the total duration to 3 seconds
        scaleAnimation.Duration = TimeSpan.FromSeconds(3);
        scaleAnimation.IterationBehavior = AnimationIterationBehavior.Forever;
        // We compress the 1.2s heartbeat into the first 40% of the 3-second timeline.
        // Scales are adjusted so it maxes out at 1.0f to prevent clipping.
        scaleAnimation.InsertKeyFrame(0.00f, new Vector3(0.8f, 0.8f, 1f));
        // First beat (Peak at 1.0f)
        scaleAnimation.InsertKeyFrame(0.08f, new Vector3(1.0f, 1.0f, 1f));
        // Recoil
        scaleAnimation.InsertKeyFrame(0.16f, new Vector3(0.8f, 0.8f, 1f));
        // Second beat (Smaller peak at 0.92f)
        scaleAnimation.InsertKeyFrame(0.24f, new Vector3(0.92f, 0.92f, 1f));
        // End of beat, return to rest
        scaleAnimation.InsertKeyFrame(0.40f, new Vector3(0.8f, 0.8f, 1f));
        // Hold at rest for the remaining 60% of the 3-second loop
        scaleAnimation.InsertKeyFrame(1.00f, new Vector3(0.8f, 0.8f, 1f));
        // Start the single animation on the "Scale" property
        heartVisual.StartAnimation("Scale", scaleAnimation);
    }
}

internal static class ColorConverter
{
    public static Windows.UI.Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255; // Default alpha value
        byte r = byte.Parse(hex[..2], NumberStyles.HexNumber);
        byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
        byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);

        if (hex.Length == 8)
        {
            a = byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber);
        }
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }
}