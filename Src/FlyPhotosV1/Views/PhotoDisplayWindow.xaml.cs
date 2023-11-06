using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FlyPhotosV1.Controllers;
using FlyPhotosV1.Utils;
using NLog;
using static FlyPhotosV1.Controllers.PhotoDisplayController;

namespace FlyPhotosV1.Views;

public partial class PhotoDisplayWindow
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly SolidColorBrush? _transparentBg;
    private readonly SolidColorBrush? _nonTransparentBg;
    private readonly PhotoDisplayController _photoController;
    private readonly WpfImageController _canvasController;

    public PhotoDisplayWindow()
    {
        InitializeComponent();

        _transparentBg = FindResource("TransparentBgBrush") as SolidColorBrush;
        _nonTransparentBg = FindResource("NonTransparentBgBrush") as SolidColorBrush;
        PreviewKeyDown += HandleKeyDown;
        PreviewKeyUp += HandleKeyUp;

        _canvasController = new WpfImageController(ImgDsp);
        _photoController = new PhotoDisplayController(_canvasController, UpdateStatus, Dispatcher);
        _photoController.LoadFirstPhoto();
    }

    public void UpdateStatus(string currentFileName, string currentCacheStatus)
    {
        TxtFileName.Text = currentFileName;
        CacheStatusProgress.Text = currentCacheStatus;
    }

    private void HandleKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    break;
                case Key.Right:
                    _photoController.Fly(NavDirection.Next);
                    break;
                case Key.Left:
                    _photoController.Fly(NavDirection.Prev);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void HandleKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Right or Key.Left)) return;
        _photoController.Brake();
    }

    private void ButtonBack_Click(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        _photoController.Fly(NavDirection.Prev);
    }

    private void ButtonNext_Click(object sender, RoutedEventArgs e)
    {
        if (_photoController.IsSinglePhoto()) return;
        _photoController.Fly(NavDirection.Next);
    }

    private void ButtonBack_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _photoController.Brake();
    }

    private void ButtonNext_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _photoController.Brake();
    }

    private void ButtonRotate_Click(object sender, RoutedEventArgs e)
    {
        _canvasController.RotateCurrentPhotoBy90();
    }

    private void ButtonHelp_Click(object sender, RoutedEventArgs e)
    {
        var helpWindow = new HelpWindow();
        helpWindow.ShowDialog();
    }

    private void ButtonSettings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        var settings = new ConfigurationWindow();
        settings.ShowDialog();
        Close();
    }

    private void ButtonCoffee_Click(object sender, RoutedEventArgs e)
    {
        Util.OpenUrl("www.google.com");
    }

    private void CommandBinding_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
    }

    private void CommandBinding_Executed_Minimize(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void CommandBinding_Executed_Maximize(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.MaximizeWindow(this);
    }

    private void CommandBinding_Executed_Restore(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.RestoreWindow(this);
    }

    private void CommandBinding_Executed_Close(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MainWindowStateChangeRaised(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MainWindowBorder.BorderThickness = new Thickness(8);
            RestoreButton.Visibility = Visibility.Visible;
            MaximizeButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MainWindowBorder.BorderThickness = new Thickness(0);
            RestoreButton.Visibility = Visibility.Collapsed;
            MaximizeButton.Visibility = Visibility.Visible;
        }

        ControlButtonGrid.Visibility = WindowState == WindowState.Maximized ? Visibility.Collapsed : Visibility.Visible;
        Background = WindowState == WindowState.Maximized ? _transparentBg : _nonTransparentBg;
    }
}