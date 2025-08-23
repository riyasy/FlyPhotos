using System.Diagnostics;
using System.Threading;
using FlyPhotos.AppSettings;
using FlyPhotos.Controllers;
using FlyPhotos.Utils;
using FlyPhotos.Views;
using Microsoft.UI.Xaml;
using WinUIEx;

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

    private readonly string _selectedFileName;
    private static Mutex _mutex;
    private Window _mWindow;


    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        if (Debug)
            //SelectedFileName = @"C:\Users\Riyas\Desktop\SingleGIF\output.gif";
            _selectedFileName = @"C:\Users\Riyas\Desktop\TestImages\SVG\NewHomepage_Illustration_oem_dark.svg";
        //SelectedFileName = @"C:\Users\Riyas\Desktop\APNG\dancing-fruits.png";
        else
            _selectedFileName = Util.GetFileNameFromCommandLine();

        KillOtherFlys();
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
}