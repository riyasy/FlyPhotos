using System;
using System.Diagnostics;
using System.IO;
using FlyPhotos.Controllers;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Linq;
using FlyPhotos.Utils;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos.Views;

/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
internal sealed partial class Settings
{
    private IThumbnailDisplayChangeable _thumbnailDisplayChanger;

    internal Settings(IThumbnailDisplayChangeable thumbnailDisplayChanger)
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


        SliderHighResCacheSize.Value = App.Settings.CacheSizeOneSideHqImages;
        SliderLowResCacheSize.Value = App.Settings.CacheSizeOneSidePreviews;
        ButtonResetPanZoom.IsOn = App.Settings.ResetPanZoomOnNextPhoto;
        ComboTheme.SelectedIndex = FindIndexOfItemInComboBox(ComboTheme, App.Settings.Theme);
        ComboBackGround.SelectedIndex = FindIndexOfItemInComboBox(ComboBackGround, App.Settings.WindowBackGround);
        ButtonShowThumbnail.IsOn = App.Settings.ShowThumbNails;

        SliderHighResCacheSize.ValueChanged += SliderHighResCacheSize_OnValueChanged;
        SliderLowResCacheSize.ValueChanged += SliderLowResCacheSize_OnValueChanged;
        ButtonResetPanZoom.Toggled += ButtonResetPanZoom_OnToggled;
        ComboTheme.SelectionChanged += ComboTheme_OnSelectionChanged;
        ComboBackGround.SelectionChanged += ComboBackGround_OnSelectionChanged;
        ButtonShowThumbnail.Toggled += ButtonShowThumbnail_OnToggled;


        SettingsCardCredits.Description = $"Uses packages from " +
                                          $"{Environment.NewLine}libheif (For HEIC) - https://github.com/strukturag/libheif " +
                                          $"{Environment.NewLine}libheif-sharp (For HEIC) - https://github.com/0xC0000054/libheif-sharp " +
                                          $"{Environment.NewLine}Magick.NET (For PSD) - https://github.com/dlemstra/Magick.NET" +
                                          $"{Environment.NewLine}nlog - https://github.com/NLog" +
                                          $"{Environment.NewLine}Vanara - https://github.com/dahall/Vanara";
        TextBoxCodecs.Text =
            $"This program doesn't install any codecs and uses codecs already present in the system.{Environment.NewLine}" +
            $"{Environment.NewLine}{Util.GetExtensionsDisplayString()}";
    }

    private static int FindIndexOfItemInComboBox(Selector comboBox, string value)
    {
        var comboBoxItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => x.Content.ToString() == value);
        return comboBox.SelectedIndex = comboBox.Items.IndexOf(comboBoxItem);
    }

    private void ComboTheme_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var themeName = (ComboTheme.SelectedItem as ComboBoxItem)?.Content as string;
        App.Settings.Theme = themeName;
        Properties.UserSettings.Default.Theme = themeName;
        Properties.UserSettings.Default.Save();
        ThemeController.Instance.SetTheme(themeName);
    }

    private void ComboBackGround_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var backGround = (ComboBackGround.SelectedItem as ComboBoxItem)?.Content as string;
        App.Settings.WindowBackGround = backGround;
        Properties.UserSettings.Default.WindowBackGround = backGround;
        Properties.UserSettings.Default.Save();
        ThemeController.Instance.SetBackGround(backGround);
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
}