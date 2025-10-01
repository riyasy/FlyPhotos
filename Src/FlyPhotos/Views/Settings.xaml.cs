#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.System;
using Windows.UI;
using FlyPhotos.AppSettings;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
internal sealed partial class Settings
{
    public event Action<ElementTheme>? ThemeChanged;
    public event Action<WindowBackdropType>? BackdropChanged;
    public event Action<bool>? ShowCheckeredBackgroundChanged;
    public event Action<int>? BackDropTransparencyChanged;
    public event Action<ThumbnailSetting>? ThumbnailSettingChanged;

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

    internal Settings()
    {
        InitializeComponent();
        Title = "Fly Photos - Settings";

        var hWnd = WindowNative.GetWindowHandle(this);
        var myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(myWndId);

        var titleBar = appWindow.TitleBar;
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
        ButtonShowCheckeredBackground.IsOn = AppConfig.Settings.CheckeredBackground;
        SliderImageFitPercentage.Value = AppConfig.Settings.ImageFitPercentage;
        SliderTransparentBackgroundIntensity.Value = AppConfig.Settings.TransparentBackgroundIntensity;
        RectThumbnailSelection.Stroke = new SolidColorBrush(ColorConverter.FromHex(AppConfig.Settings.ThumbnailSelectionColor));
        SliderThumbnailSize.Value = AppConfig.Settings.ThumbnailSize;
        ButtonRememberLastMonitor.IsOn = AppConfig.Settings.RememberLastMonitor;

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
        ButtonShowCheckeredBackground.Toggled += ButtonShowCheckeredBackground_OnToggled;
        SliderImageFitPercentage.ValueChanged += SliderImageFitPercentage_ValueChanged;
        SliderTransparentBackgroundIntensity.ValueChanged += SliderTransparentBackgroundIntensity_ValueChanged;
        SliderThumbnailSize.ValueChanged += SliderThumbnailSize_ValueChanged;
        ButtonRememberLastMonitor.Toggled += ButtonRememberLastMonitor_OnToggled;



        SettingsCardKeyboardShortCuts.Description = $"{Environment.NewLine}Left/Right Arrow Keys : Navigate Photos" +
                                                    $"{Environment.NewLine}Mouse Left Click and Drag : Pan Photo" +
                                                    Environment.NewLine+
                                                    $"{Environment.NewLine}Mouse Wheel : Zoom In or Out/ Navigate Photos - based on setting" +
                                                    $"{Environment.NewLine}Ctrl + Mouse Wheel : Zoom In or Out" +
                                                    $"{Environment.NewLine}Alt + Mouse Wheel : Navigate Photos" +
                                                    Environment.NewLine +
                                                    $"{Environment.NewLine}Ctrl + 'Arrow Keys' : Pan Photo" +
                                                    $"{Environment.NewLine}Ctrl + '+' : Zoom In" +
                                                    $"{Environment.NewLine}Ctrl + '-' : Zoom Out" +
                                                    Environment.NewLine +
                                                    $"{Environment.NewLine}Mouse wheel on Thumbnail strip: Navigate Photos" +
                                                    $"{Environment.NewLine}Mouse wheel on On Screen Left/Right Button: Navigate Photos" +
                                                    $"{Environment.NewLine}Mouse wheel on On Screen Rotate Button: Rotate Photo" +
                                                    Environment.NewLine +
                                                    $"{Environment.NewLine}Home - Navigate to first photo" +
                                                    $"{Environment.NewLine}End - Navigate to last photo" 
                                                    ;

        SettingsCardCredits.Description = $"Uses packages from " +
                                          $"{Environment.NewLine}libheif (For HEIC) - https://github.com/strukturag/libheif " +
                                          $"{Environment.NewLine}libheif-sharp (For HEIC) - https://github.com/0xC0000054/libheif-sharp " +
                                          $"{Environment.NewLine}Magick.NET (For PSD) - https://github.com/dlemstra/Magick.NET" +
                                          $"{Environment.NewLine}SkiaSharp (For SVG) - https://github.com/mono/SkiaSharp" +
                                          $"{Environment.NewLine}MagicScaler - https://github.com/saucecontrol/PhotoSauce" +
                                          $"{Environment.NewLine}WinUIEx - https://github.com/dotMorten/WinUIEx" +
                                          $"{Environment.NewLine}nlog - https://github.com/NLog";
        TextBoxCodecs.Text =
            $"This program doesn't install any codecs and uses codecs already present in the system.{Environment.NewLine}" +
            $"{Environment.NewLine}{Util.GetExtensionsDisplayString()}";

    
        if (SettingsCardMouseWheelBehaviour.Description is string desc)
            SettingsCardMouseWheelBehaviour.Description = desc.Replace("%%", Environment.NewLine);
                
    }

    private async void ButtonRememberLastMonitor_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.RememberLastMonitor = ButtonRememberLastMonitor.IsOn;
        await AppConfig.SaveAsync();
    }

    private async void SliderThumbnailSize_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.ThumbnailSize = (int)SliderThumbnailSize.Value;
        ThumbnailSettingChanged?.Invoke(ThumbnailSetting.Size);
        await AppConfig.SaveAsync();
    }

    private async void SliderTransparentBackgroundIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        AppConfig.Settings.TransparentBackgroundIntensity = (int)SliderTransparentBackgroundIntensity.Value;
        BackDropTransparencyChanged?.Invoke(AppConfig.Settings.TransparentBackgroundIntensity);
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
        ShowCheckeredBackgroundChanged?.Invoke(ButtonShowCheckeredBackground.IsOn);
        await AppConfig.SaveAsync();
    }

    private async void ButtonHighQualityInterpolation_OnToggled(object sender, RoutedEventArgs e)
    {
        AppConfig.Settings.HighQualityInterpolation = ButtonHighQualityInterpolation.IsOn;
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
        ThemeChanged?.Invoke(themeEnum); 
        await AppConfig.SaveAsync();
    }

    private async void ComboBackGround_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var backGroundDisplayName = (ComboBackGround.SelectedItem as ComboBoxItem)?.Content as string;
        var backGroundEnum = _backdropTranslator.ToEnum(backGroundDisplayName);
        AppConfig.Settings.WindowBackdrop = backGroundEnum;

        BackdropChanged?.Invoke(backGroundEnum);
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
        ThumbnailSettingChanged?.Invoke(ThumbnailSetting.ShowHide);
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

    private void MainLayout_OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
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

    private Visibility ConvertBoolToVisibility(bool value)
    {
        return value ? Visibility.Visible : Visibility.Collapsed;
    }


    private Visibility ShouldDisplayTransparencySlider(int index)
    {
        return index == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ColorFlyOutOkButton_Click(object sender, RoutedEventArgs e)
    {
        Color newColor = FlyoutColorPicker.Color;
        RectThumbnailSelection.Stroke = new SolidColorBrush(Color.FromArgb(255, newColor.R, newColor.G, newColor.B));
        ColorPickerFlyout.Hide();
        AppConfig.Settings.ThumbnailSelectionColor = $"#{newColor.R:X2}{newColor.G:X2}{newColor.B:X2}";
        ThumbnailSettingChanged?.Invoke(ThumbnailSetting.SelectionColor);
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
}

internal static class ColorConverter
{
    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte a = 255; // Default alpha value
        byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);

        if (hex.Length == 8)
        {
            a = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber);
        }
        return Color.FromArgb(a, r, g, b);
    }
}