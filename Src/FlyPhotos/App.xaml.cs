using FlyPhotos.Utils;
using FlyPhotos.Views;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.IO;
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
    public static string DebugTestFolder = string.Empty;

    private static Mutex _mutex;
    private Window _mWindow;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        if (Debug)
        {
            //selectedFileName = @"C:\Test\20211004_160211 (ILCE-6400).ARW";
            //selectedFileName = @"C:\Test2\20211004_171521 (ILCE-6400).JPG";
            //selectedFileName = @"M:\Photos\Photos\Digicam\D7000\2023\20230121_105835 (D7000).NEF";
            SelectedFileName = @"M:\Photos\Photos\Digicam\A6400\2023\20230101_225733 (ILCE-6400).ARW";
            //SelectedFileName = @"C:\SampleImages\TestFailCase\20130313_124412 (Galaxy.Ace).JPG";
            //SelectedFileName = @"C:\SampleImages\TestJpegOnly\1.JPG";
            DebugTestFolder = Path.GetDirectoryName(SelectedFileName);
        }
        else
        {
            SelectedFileName = Util.GetFileNameFromCommandLine();
        }

        KillOtherFlys();
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mWindow = new PhotoDisplayWindow();
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