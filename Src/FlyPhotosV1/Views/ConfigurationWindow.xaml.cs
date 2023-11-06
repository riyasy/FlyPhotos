using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FlyPhotosV1.Utils;

namespace FlyPhotosV1.Views;

/// <summary>
/// Interaction logic for ConfigurationWindow.xaml
/// </summary>
public partial class ConfigurationWindow
{
    public ConfigurationWindow()
    {
        InitializeComponent();
    }

    private void BtnCodecsDisplay_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"This program doesn't install any codecs and uses codecs already present in the system.{Environment.NewLine}" +
            $"{Environment.NewLine}{Util.GetExtensionsDisplayString()}", "List of Codecs");
    }

    private void BtnShowLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = $"{Path.GetTempPath()}FlyPhotos{Path.DirectorySeparatorChar}FlyPhotos.log";
        if (File.Exists(logPath))
            Process.Start("notepad.exe", logPath);
        else
            MessageBox.Show("Log file doesn't exist");
    }

    private void BtnShowSource_Click(object sender, RoutedEventArgs e)
    {
        Util.OpenUrl("https://github.com/riyasy/FlyPhotos");
    }

    private void BtnShowReleases_Click(object sender, RoutedEventArgs e)
    {
        Util.OpenUrl("https://github.com/riyasy/FlyPhotos/releases");
    }

    private void BtnReportIssues_Click(object sender, RoutedEventArgs e)
    {
        Util.OpenUrl("https://github.com/riyasy/FlyPhotos/issues");
    }

    private void BtnCredits_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"Uses packages from {Environment.NewLine}libheif - https://github.com/strukturag/libheif " +
            $"{Environment.NewLine}libheif-sharp - https://github.com/0xC0000054/libheif-sharp " +
            $"{Environment.NewLine}nlog - https://github.com/NLog", "Credits");
    }
}