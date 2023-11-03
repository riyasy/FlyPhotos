using FlyPhotos.Utils;
using FlyPhotos.Views;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace FlyPhotos;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    public static string SelectedFileName;

    public static bool Debug = false;
    public static string DebugTestFolder = string.Empty;

    private static Mutex _mutex;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        KillOtherFlys();

        //var testPerformance = new TestPerformance();
        //testPerformance.Show();
        //return;

        if (Debug)
        {
            SelectedFileName = @"C:\Test\20211004_160211 (ILCE-6400).ARW";
            DebugTestFolder = Path.GetDirectoryName(SelectedFileName);
        }
        else
        {
            SelectedFileName = Util.GetFileNameFromCommandLine();
        }

        Window window;
        if (File.Exists(SelectedFileName))
            window = new PhotoDisplayWindow();
        else
            window = new HelpWindow();

        MainWindow = window;
        window.Show();
    }

    private static void KillOtherFlys()
    {
        const string appName = "FlyPhotosFlyPhotos";
        _mutex = new Mutex(true, appName, out var createdNew);
        if (!createdNew)
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
                if (process.Id != current.Id)
                    process.Kill();
        }
    }
}