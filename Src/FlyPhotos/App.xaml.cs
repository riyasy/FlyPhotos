using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel.Activation;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;
using FlyPhotos.UI.Views;
using Microsoft.Windows.AppLifecycle;
using NLog;
using WinUIEx;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FlyPhotos;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App
{
    private string _selectedFilePath;
    private static Mutex _mutex;
    private PhotoDisplayWindow _photoDisplayWindow;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        GlobalDiagnosticsContext.Set("LogPath", PathResolver.GetLogFolderPath());
        KillOtherFlys();

        AppConfig.Initialize();

        var appliedLanguage = Localizer.ApplyLanguage(AppConfig.Settings.Language);
        if (appliedLanguage != AppConfig.Settings.Language)
        {
            AppConfig.Settings.Language = appliedLanguage;
            _ = AppConfig.SaveAsync();
        }

        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _selectedFilePath = PathResolver.IsPackagedApp ? 
            GetFilePathFromArgsPackaged(AppInstance.GetCurrent().GetActivatedEventArgs().Data as IFileActivatedEventArgs) : 
            GetFilePathFromCommandLine();

        if (string.IsNullOrEmpty(_selectedFilePath))
        {
            var initWindow = new InitWindow();
            initWindow.SetWindowSize(600, 600);
            initWindow.CenterOnScreen();
            initWindow.Activate();
            initWindow.Closed += delegate
            {
                if (File.Exists(initWindow.SelectedFile))
                    LaunchPhotoDisplayWindow(initWindow.SelectedFile, false);
            };

        }
        else
        {
            LaunchPhotoDisplayWindow(_selectedFilePath, true);
        }
    }

    private void LaunchPhotoDisplayWindow(string selectedFilePath, bool extLaunch)
    {
        _photoDisplayWindow = new PhotoDisplayWindow(selectedFilePath, extLaunch);        
        if (AppConfig.Settings.RememberLastMonitor) 
            Util.MoveWindowToMonitor(_photoDisplayWindow, AppConfig.Settings.LastUsedMonitorId);
        _photoDisplayWindow.Maximize();
        _photoDisplayWindow.Activate();
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

    private static string GetFilePathFromCommandLine()
    {
        return Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
    }

    private static string GetFilePathFromArgsPackaged(IFileActivatedEventArgs fileArgs)
    {
        if (fileArgs == null || fileArgs.Files.Count <= 0)
            return string.Empty;
        else
            return fileArgs.Files[0].Path;
    }
}