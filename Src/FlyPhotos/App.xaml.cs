using FlyPhotos.AppSettings;
using FlyPhotos.Utils;
using FlyPhotos.Views;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using NLog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel.Activation;
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
    public static readonly bool Packaged = false;
    public static readonly bool Debug = false;

    private string _selectedFileName;
    private static Mutex _mutex;
    private Window _mWindow;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        NLog.GlobalDiagnosticsContext.Set("LogPath", PathResolver.GetLogFolderPath());

        KillOtherFlys();
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Packaged)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            _selectedFileName = GetFileNameFromArgsPackaged(activationArgs.Data as IFileActivatedEventArgs);
        }
        else
        {
            _selectedFileName = GetFileNameFromCommandLine();
        }


        if (Debug)
        {
            //SelectedFileName = @"C:\Users\Riyas\Desktop\SingleGIF\output.gif";
            _selectedFileName = @"C:\Users\Riyas\Desktop\TestImages\SVG\New folder\NewHomepage_Illustration_dark.svg";
            //SelectedFileName = @"C:\Users\Riyas\Desktop\APNG\dancing-fruits.png";
        }

        AppConfig.Initialize();
        _mWindow = new PhotoDisplayWindow(_selectedFileName);
        _mWindow.Maximize();
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

    private static string GetFileNameFromCommandLine()
    {
        return Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
    }

    private static string GetFileNameFromArgsPackaged(IFileActivatedEventArgs fileArgs)
    {
        if (fileArgs == null || fileArgs.Files.Count <= 0)
            return string.Empty;
        else
            return fileArgs.Files[0].Path;
    }
}