using FlyPhotos.Controllers;
using FlyPhotos.Utils;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FlyPhotos.Data;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
internal sealed partial class Settings : IThemeChangeable
{
    private readonly IThumbnailController _thumbnailDisplayChanger;
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

    internal Settings(IThumbnailController thumbnailDisplayChanger)
    {
        _thumbnailDisplayChanger = thumbnailDisplayChanger;
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
        SetWindowTheme(App.Settings.Theme); 
        DispatcherQueue.EnsureSystemDispatcherQueue();


        SliderHighResCacheSize.Value = App.Settings.CacheSizeOneSideHqImages;
        SliderLowResCacheSize.Value = App.Settings.CacheSizeOneSidePreviews;
        ButtonResetPanZoom.IsOn = App.Settings.ResetPanZoomOnNextPhoto;
        ComboTheme.SelectedIndex = FindIndexOfItemInComboBox(ComboTheme, _themeTranslator.ToString(App.Settings.Theme));
        ComboBackGround.SelectedIndex = FindIndexOfItemInComboBox(ComboBackGround, _backdropTranslator.ToString(App.Settings.WindowBackGround));
        ButtonShowThumbnail.IsOn = App.Settings.ShowThumbNails;
        CheckBoxEnableAutoFade.IsChecked = App.Settings.AutoFade;
        ButtonOpenExitZoom.IsOn = App.Settings.OpenExitZoom;
        SliderFadeIntensity.Value = App.Settings.FadeIntensity;

        SliderHighResCacheSize.ValueChanged += SliderHighResCacheSize_OnValueChanged;
        SliderLowResCacheSize.ValueChanged += SliderLowResCacheSize_OnValueChanged;
        ButtonResetPanZoom.Toggled += ButtonResetPanZoom_OnToggled;
        ComboTheme.SelectionChanged += ComboTheme_OnSelectionChanged;
        ComboBackGround.SelectionChanged += ComboBackGround_OnSelectionChanged;
        ButtonShowThumbnail.Toggled += ButtonShowThumbnail_OnToggled;
        ButtonOpenExitZoom.Toggled += ButtonOpenExitZoom_OnToggled;
        CheckBoxEnableAutoFade.Checked += CheckBoxEnableAutoFade_Checked;
        CheckBoxEnableAutoFade.Unchecked += CheckBoxEnableAutoFade_Checked;
        SliderFadeIntensity.ValueChanged += SliderFadeIntensity_ValueChanged;

        SettingsCardKeyboardShortCuts.Description = $"{Environment.NewLine}Left/Right Arrow Keys : Navigate Photos" +
                                                    $"{Environment.NewLine}Mouse Wheel : Zoom In/Out" +
                                                    $"{Environment.NewLine}Mouse Left Click and Drag : Pan Photo" +
                                                    $"{Environment.NewLine}Ctrl + Mouse Wheel : Navigate Photos" +
                                                    $"{Environment.NewLine}Ctrl + '+' : Zoom In" +
                                                    $"{Environment.NewLine}Ctrl + '-' : Zoom Out" +
                                                    $"{Environment.NewLine}Ctrl + 'Arrow Keys' : Pan Photo" +
                                                    $"{Environment.NewLine}Thumbnail Click : Navigate to specific Photo" +
                                                    $"{Environment.NewLine}Thumbnail Mouse wheel : Navigate Photos";

        SettingsCardCredits.Description = $"Uses packages from " +
                                          $"{Environment.NewLine}libheif (For HEIC) - https://github.com/strukturag/libheif " +
                                          $"{Environment.NewLine}libheif-sharp (For HEIC) - https://github.com/0xC0000054/libheif-sharp " +
                                          $"{Environment.NewLine}Magick.NET (For PSD) - https://github.com/dlemstra/Magick.NET" +
                                          $"{Environment.NewLine}MagicScaler - https://github.com/saucecontrol/PhotoSauce" +
                                          $"{Environment.NewLine}SkiaSharp - https://github.com/mono/SkiaSharp" +
                                          $"{Environment.NewLine}nlog - https://github.com/NLog" +                                          
                                          $"{Environment.NewLine}LiteDB - https://github.com/litedb-org/LiteDB" +
                                          $"{Environment.NewLine}Vanara - https://github.com/dahall/Vanara";
        TextBoxCodecs.Text =
            $"This program doesn't install any codecs and uses codecs already present in the system.{Environment.NewLine}" +
            $"{Environment.NewLine}{Util.GetExtensionsDisplayString()}";
    }



    private void ButtonOpenExitZoom_OnToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.OpenExitZoom = ButtonOpenExitZoom.IsOn;
        Properties.UserSettings.Default.OpenExitZoom = ButtonOpenExitZoom.IsOn;
        Properties.UserSettings.Default.Save();
    }

    private void CheckBoxEnableAutoFade_Checked(object sender, RoutedEventArgs e)
    {
        App.Settings.AutoFade = (CheckBoxEnableAutoFade.IsChecked == true);
        Properties.UserSettings.Default.AutoFade = (CheckBoxEnableAutoFade.IsChecked == true);
        Properties.UserSettings.Default.Save();
    }

    private void SliderFadeIntensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        App.Settings.FadeIntensity = (int)SliderFadeIntensity.Value;
        Properties.UserSettings.Default.FadeIntensity = (int)SliderFadeIntensity.Value;
        Properties.UserSettings.Default.Save();
    }

    private static int FindIndexOfItemInComboBox(Selector comboBox, string value)
    {
        var comboBoxItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => x.Content.ToString() == value);
        return comboBox.SelectedIndex = comboBox.Items.IndexOf(comboBoxItem);
    }

    private void ComboTheme_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var themeDisplayName = (ComboTheme.SelectedItem as ComboBoxItem)?.Content as string;
        var themeEnum = _themeTranslator.ToEnum(themeDisplayName);
        App.Settings.Theme = themeEnum;
        Properties.UserSettings.Default.Theme = themeEnum.ToString();
        Properties.UserSettings.Default.Save();
        ThemeController.Instance.SetTheme(themeEnum);
    }

    private void ComboBackGround_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var backGroundDisplayName = (ComboBackGround.SelectedItem as ComboBoxItem)?.Content as string;
        var backGroundEnum = _backdropTranslator.ToEnum(backGroundDisplayName);
        App.Settings.WindowBackGround = backGroundEnum;
        Properties.UserSettings.Default.WindowBackGroundType = backGroundEnum.ToString();
        Properties.UserSettings.Default.Save();
        ThemeController.Instance.SetBackGround(backGroundEnum);
    }

    private void ButtonResetPanZoom_OnToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.ResetPanZoomOnNextPhoto = ButtonResetPanZoom.IsOn;
        Properties.UserSettings.Default.ResetPanZoomOnNextPhoto = ButtonResetPanZoom.IsOn;
        Properties.UserSettings.Default.Save();
    }

    private void ButtonShowThumbnail_OnToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.ShowThumbNails = ButtonShowThumbnail.IsOn;
        Properties.UserSettings.Default.ShowThumbnails = ButtonShowThumbnail.IsOn;
        Properties.UserSettings.Default.Save();
        _thumbnailDisplayChanger.ShowThumbnailBasedOnSettings();
    }

    private void SliderLowResCacheSize_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        App.Settings.CacheSizeOneSidePreviews = (int)SliderLowResCacheSize.Value;
        Properties.UserSettings.Default.CacheSizeOneSidePreviews = (int)SliderLowResCacheSize.Value;
        Properties.UserSettings.Default.Save();
    }

    private void SliderHighResCacheSize_OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        App.Settings.CacheSizeOneSideHqImages = (int)SliderHighResCacheSize.Value;
        Properties.UserSettings.Default.CacheSizeOneSideHqImages = (int)SliderHighResCacheSize.Value;
        Properties.UserSettings.Default.Save();
    }

    private void ButtonOpenLog_OnClick(object sender, RoutedEventArgs e)
    {
        var logPath = $"{Path.GetTempPath()}FlyPhotos{Path.DirectorySeparatorChar}FlyPhotos.log";
        if (File.Exists(logPath))
            Process.Start("notepad.exe", logPath);
    }

    public void SetWindowTheme(ElementTheme theme)
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
}