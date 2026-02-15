#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using FlyPhotos.Core;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;
using FlyPhotos.Services.ExternalAppListing;
using FlyPhotos.UI.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.UI.Screens;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
internal sealed partial class Settings
{
    public event Action<Setting>? SettingChanged;

    private readonly SystemBackdropConfiguration _configurationSource;

    internal Settings()
    {
        InitializeComponent();
        //Title = "FlyPhotos - Settings";

        SettingsCardVersion.Description = 
            string.Format(L.Get("SettingsCardVersion/Description"), Constants.AppVersion);

        if (!PathResolver.IsPackagedApp)
            Util.SetUnpackagedAppIcon(this);

        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Gray;

        _configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

        Activated += Settings_Activated;
        MainLayout.Loaded += Settings_Loaded;
        ((FrameworkElement)Content).ActualThemeChanged += Settings_ActualThemeChanged;
        SetConfigurationSourceTheme();
        SetWindowTheme(AppConfig.Settings.Theme);
        DispatcherQueue.EnsureSystemDispatcherQueue();

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
        ButtonRememberLastMonitor.IsOn = AppConfig.Settings.RememberLastMonitor;
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
        ButtonRememberLastMonitor.Toggled += ButtonRememberLastMonitor_OnToggled;
        ButtonConfirmBeforeDelete.Toggled += ButtonConfirmBeforeDelete_OnToggled;
        ButtonShowFileName.Toggled += ButtonShowFileName_OnToggled;
        ButtonShowImageDimensions.Toggled += ButtonShowImageDimensions_OnToggled;
        ButtonShowCacheStatusExpander.Toggled += ButtonShowCacheStatusExpander_OnToggled;
        ComboPanZoomNavBehaviour.SelectionChanged += ComboPanZoomNavBehaviour_OnSelectionChanged;
        ButtonEnableAutoHideMouse.Toggled += ButtonEnableAutoHideMouse_OnToggled;
        ButtonEnableExternalShortcut.Toggled += ButtonEnableExternalShortcut_OnToggled;

        TextBoxCodecsChanged();

        // Initialize codec list view
        ListViewCodecs.ItemsSource = Util.GetAllCodecs();

        if (ComboMouseWheelBehaviourInfo.Description is string desc)
            ComboMouseWheelBehaviourInfo.Description = desc.Replace("%%", Environment.NewLine);

        if (PathResolver.IsPackagedApp)
        {
            SettingsCardDeveloperSupport.Visibility = Visibility.Collapsed;
            BtnCheckVersion.Visibility = Visibility.Collapsed;
        }

    }

    private void TextBoxCodecsChanged()
    {
        // keep the disclaimer in case it's used elsewhere; nothing to do here now
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
    }

    private async Task SetButtonIconFromAppData(Button btnShortcut, string appShortCut)
    {
        var app = await AppProvider.GetAppAsync(appShortCut);
        if (app != null)
        {
            var bmp = app.Icon;
            btnShortcut.Content = bmp != null
                ? new Image { Source = bmp, Width = 32, Height = 32 }
                : new FontIcon { Glyph = "\uED35", FontSize = 32 }; // Default icon
        }
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


    private async void ButtonRememberLastMonitor_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.RememberLastMonitor = ButtonRememberLastMonitor.IsOn;
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

        SetWindowTheme(themeEnum);
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

    private void SetWindowTheme(ElementTheme theme)
    {
        ((FrameworkElement)Content).RequestedTheme = theme;
    }

    private void Settings_ActualThemeChanged(FrameworkElement sender, object args)
    {
        SetConfigurationSourceTheme();
    }

    private void Settings_Activated(object sender, WindowActivatedEventArgs args)
    {
        _configurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
    }

    private void SetConfigurationSourceTheme()
    {
        _configurationSource.IsHighContrast = ThemeSettings.CreateForWindowId(AppWindow.Id).HighContrast;
        _configurationSource.Theme = (SystemBackdropTheme)((FrameworkElement)Content).ActualTheme;
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
}

internal static class ColorConverter
{
    public static Windows.UI.Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255; // Default alpha value
        byte r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);

        if (hex.Length == 8)
        {
            a = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
        }
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }
}
