using System;

namespace FlyPhotosV1.Views;

/// <summary>
/// Interaction logic for HelpWindow.xaml
/// </summary>
public partial class HelpWindow
{
    public HelpWindow()
    {
        InitializeComponent();
        TxtHelpContent.Text =
            $"{Environment.NewLine}•Help Videos at https://www.youtube.com/channel/UCHw_F7RQ7L5_NvvWsbPTOzg" +
            $"{Environment.NewLine}•In Windows 7,8,10, right click on any image file and click [Open with Fly] menu option." +
            $"{Environment.NewLine}•In Windows 11, some times the option will be visible only on clicking [Show more options] menu item." +
            $"{Environment.NewLine}•Press escape key to close." +
            $"{Environment.NewLine}•Press outside image to get a normal window." +
            $"{Environment.NewLine}•Use < or > keys on screen or on keyboard to navigate between photos." +
            $"{Environment.NewLine}•Long press < or > to move swiftly through all cached images on left and right." +
            $"{Environment.NewLine}•Use mouse wheel to zoom in on any portion of the image." +
            $"{Environment.NewLine}•Open expander to see the preview caching status on both left and right." +
            $"{Environment.NewLine}•This app works by right clicking on explorer." +
            $"{Environment.NewLine}•App loads whatever image files are visible on the explorer. For e.g " +
            $"if we use the App from a search filtered explorer window, it loads only files which are available in that explorer window." +
            $"{Environment.NewLine}•App shows photos in the same order as what is seen in explorer" +
            $"{Environment.NewLine}••Limitations••" +
            $"{Environment.NewLine}•Supports path length of only up to 256 characters" +
            $"{Environment.NewLine}•Supports only image formats available in [Windows Imaging component]. Supported formats" +
            $" can be seen by going to [Settings > Show Codecs]";
    }
}