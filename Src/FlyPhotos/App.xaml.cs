using FlyPhotos.Controllers;
using FlyPhotos.Data;
using FlyPhotos.Utils;
using FlyPhotos.Views;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App
{
    public static string SelectedFileName;

    public static bool Packaged = false;
    public static bool Debug = false;

    private static Mutex _mutex;
    private Window _mWindow;

    public static SettingsData Settings { get; set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Settings = LoadSettings();

        if (Debug)
        {
            //SelectedFileName = @"C:\Users\Riyas\Desktop\SingleGIF\output.gif";
            SelectedFileName = @"C:\Users\Riyas\Desktop\TestImages\JPEGS\20250703_070510 (ILCE-6400).JPG";
            //SelectedFileName = @"C:\Users\Riyas\Desktop\APNG\dancing-fruits.png";
        }
        else
        {
            SelectedFileName = Util.GetFileNameFromCommandLine();
        }

        KillOtherFlys();
        InitializeComponent();
    }

    private static SettingsData LoadSettings()
    {
        return new SettingsData
        {
            Theme = (ElementTheme)Enum.Parse(typeof(ElementTheme), Properties.UserSettings.Default.Theme),
            WindowBackGround = (WindowBackdropType)Enum.Parse(typeof(WindowBackdropType), Properties.UserSettings.Default.WindowBackGroundType),
            ResetPanZoomOnNextPhoto = Properties.UserSettings.Default.ResetPanZoomOnNextPhoto,
            CacheSizeOneSideHqImages = Properties.UserSettings.Default.CacheSizeOneSideHqImages,
            CacheSizeOneSidePreviews = Properties.UserSettings.Default.CacheSizeOneSidePreviews,
            ShowThumbNails = Properties.UserSettings.Default.ShowThumbnails,
            AutoFade =  Properties.UserSettings.Default.AutoFade,
            FadeIntensity = Properties.UserSettings.Default.FadeIntensity,
            OpenExitZoom = Properties.UserSettings.Default.OpenExitZoom,
        };

    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mWindow = new PhotoDisplayWindow();
        ThemeController.Instance.AddWindow(_mWindow);
        _mWindow.Activate();
    }

    private static void KillOtherFlys()
    {
        const string appName = "FlyPhotosFlyPhotosWinUI";
        _mutex = new Mutex(true, appName, out var createdNew);
        if (createdNew) return;
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
            if (process.Id != current.Id)
                process.Kill();
    }
}