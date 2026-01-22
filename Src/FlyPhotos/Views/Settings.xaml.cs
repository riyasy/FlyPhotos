#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.System;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using FlyPhotos.Utils;
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

namespace FlyPhotos.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
internal sealed partial class Settings
{
    public event Action<Setting>? SettingChanged;

    private readonly SystemBackdropConfiguration _configurationSource;

    // Backdrop translator
    private readonly EnumStringTranslator<WindowBackdropType> _backdropTranslator = new(
        new Dictionary<WindowBackdropType, string>
        {
            { WindowBackdropType.Transparent, "Transparent" },
            { WindowBackdropType.Frozen, "Frozen" },
            { WindowBackdropType.Acrylic, "Acrylic" },
            { WindowBackdropType.AcrylicThin, "Acrylic Thin" },
            { WindowBackdropType.Mica, "Mica" },
            { WindowBackdropType.MicaAlt, "Mica Alt" },
            { WindowBackdropType.None, "None" }
        }
    );

    // Theme translator
    private readonly EnumStringTranslator<ElementTheme> _themeTranslator = new(
        new Dictionary<ElementTheme, string>
        {
            { ElementTheme.Default, "Default" },
            { ElementTheme.Dark, "Dark" },
            { ElementTheme.Light, "Light" }
        }
    );

    private readonly EnumStringTranslator<DefaultMouseWheelBehavior> _mouseWheelBehaviourTranslator = new(
        new Dictionary<DefaultMouseWheelBehavior, string>
        {
            { DefaultMouseWheelBehavior.Zoom, "Zoom In or Out" },
            { DefaultMouseWheelBehavior.Navigate, "View Next or Previous" }
        }
    );

    // Pan/Zoom Navigation Behavior translator
    private readonly EnumStringTranslator<PanZoomBehaviourOnNavigation> _panZoomBehaviourTranslator = new(
        new Dictionary<PanZoomBehaviourOnNavigation, string>
        {
            { PanZoomBehaviourOnNavigation.Reset, "Reset Pan/Zoom/Rotation" },
            { PanZoomBehaviourOnNavigation.RememberPerPhoto, "Remember Pan/Zoom/Rotation per photo" },
            { PanZoomBehaviourOnNavigation.RetainFromLastPhoto, "Retain Pan/Zoom from previous photo" }
        }
    );

    internal Settings()
    {
        InitializeComponent();
        Title = "Fly Photos - Settings";

        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Gray;

        _configurationSource = new SystemBackdropConfiguration
        {
            IsHighContrast = ThemeSettings.CreateForWindowId(this.AppWindow.Id).HighContrast,
            IsInputActive = true
        };
        this.Activated += Settings_Activated;
        MainLayout.Loaded += Settings_Loaded;
        ((FrameworkElement)Content).ActualThemeChanged += Settings_ActualThemeChanged;
        SetConfigurationSourceTheme();
        SetWindowTheme(AppConfig.Settings.Theme);
        DispatcherQueue.EnsureSystemDispatcherQueue();

        SliderHighResCacheSize.Value = AppConfig.Settings.CacheSizeOneSideHqImages;
        SliderLowResCacheSize.Value = AppConfig.Settings.CacheSizeOneSidePreviews;
        ComboTheme.SelectedIndex = FindIndexOfItemInComboBox(ComboTheme, _themeTranslator.ToString(AppConfig.Settings.Theme));
        ComboBackGround.SelectedIndex = FindIndexOfItemInComboBox(ComboBackGround, _backdropTranslator.ToString(AppConfig.Settings.WindowBackdrop));
        ComboMouseWheelBehaviour.SelectedIndex = FindIndexOfItemInComboBox(ComboMouseWheelBehaviour, _mouseWheelBehaviourTranslator.ToString(AppConfig.Settings.DefaultMouseWheelBehavior));
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
        ComboPanZoomNavBehaviour.SelectedIndex = FindIndexOfItemInComboBox(ComboPanZoomNavBehaviour, _panZoomBehaviourTranslator.ToString(AppConfig.Settings.PanZoomBehaviourOnNavigation));
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

        SettingsCardKeyboardShortCuts.Description = Constants.ShortCuts;
        SettingsCardCredits.Description = Constants.Credits;
        TextBoxCodecs.Text = Constants.CodecDisclaimer;

        if (ComboMouseWheelBehaviourInfo.Description is string desc)
            ComboMouseWheelBehaviourInfo.Description = desc.Replace("%%", Environment.NewLine);

    }

    private async void ButtonEnableExternalShortcut_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.ShowExternalAppShortcuts = ButtonEnableExternalShortcut.IsOn;
        SettingChanged?.Invoke(Setting.ExtShortcutsShowHide);
        await AppConfig.SaveAsync();
    }

    private async void Settings_Loaded(object sender, RoutedEventArgs e)
    {
        await Util.SetButtonIconFromExeAsync(BtnShortcut1, AppConfig.Settings.ExternalApp1);
        await Util.SetButtonIconFromExeAsync(BtnShortcut2, AppConfig.Settings.ExternalApp2);
        await Util.SetButtonIconFromExeAsync(BtnShortcut3, AppConfig.Settings.ExternalApp3);
        await Util.SetButtonIconFromExeAsync(BtnShortcut4, AppConfig.Settings.ExternalApp4);
    }

    private async void ButtonEnableAutoHideMouse_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.AutoHideMouse = ButtonEnableAutoHideMouse.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void ComboPanZoomNavBehaviour_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((ComboPanZoomNavBehaviour.SelectedItem as ComboBoxItem)?.Content is not string comboSelectionAsString) return;

        var panZoomEnum = _panZoomBehaviourTranslator.ToEnum(comboSelectionAsString);
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

    private static int FindIndexOfItemInComboBox(Selector comboBox, string value)
    {
        var comboBoxItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => x.Content.ToString() == value);
        return comboBox.SelectedIndex = comboBox.Items.IndexOf(comboBoxItem);
    }

    private async void ComboTheme_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var themeDisplayName = (ComboTheme.SelectedItem as ComboBoxItem)?.Content as string;
        var themeEnum = _themeTranslator.ToEnum(themeDisplayName);
        AppConfig.Settings.Theme = themeEnum;

        SetWindowTheme(themeEnum);
        SettingChanged?.Invoke(Setting.Theme);
        await AppConfig.SaveAsync();
    }

    private async void ComboBackGround_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var backGroundDisplayName = (ComboBackGround.SelectedItem as ComboBoxItem)?.Content as string;
        var backGroundEnum = _backdropTranslator.ToEnum(backGroundDisplayName);
        AppConfig.Settings.WindowBackdrop = backGroundEnum;
        SettingChanged?.Invoke(Setting.BackDrop);
        await AppConfig.SaveAsync();
    }
    private async void ComboMouseWheel_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var comboSelectionAsString = (ComboMouseWheelBehaviour.SelectedItem as ComboBoxItem)?.Content as string;
        var mouseWheelEnum = _mouseWheelBehaviourTranslator.ToEnum(comboSelectionAsString);
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
        if (e.Key == VirtualKey.Escape) this.Close();
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
        _configurationSource.IsHighContrast = ThemeSettings.CreateForWindowId(this.AppWindow.Id).HighContrast;
        _configurationSource.Theme = (SystemBackdropTheme)((FrameworkElement)Content).ActualTheme;
    }

    private bool ShouldEnableTransparencySlider(int index)
    {
        return index == 0;
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
        var button = (Button)sender;
        var exe = await PickExeAsync();
        if (exe != null)
        {
            SetShortCutSettingForButton(button, exe);
            await AppConfig.SaveAsync();
            await Util.SetButtonIconFromExeAsync(button, exe);
        }
    }

    private static void SetShortCutSettingForButton(Button button, string exe)
    {
        switch (button.Name)
        {
            case "BtnShortcut1":
                AppConfig.Settings.ExternalApp1 = exe;
                break;
            case "BtnShortcut2":
                AppConfig.Settings.ExternalApp2 = exe;
                break;
            case "BtnShortcut3":
                AppConfig.Settings.ExternalApp3 = exe;
                break;
            case "BtnShortcut4":
                AppConfig.Settings.ExternalApp4 = exe;
                break;
        }
    }

    private async Task<string?> PickExeAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
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
