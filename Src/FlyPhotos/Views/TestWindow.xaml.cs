using FlyPhotos.Data;
using FlyPhotos.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Popups;

namespace FlyPhotos.Views;

public sealed partial class TestWindow
{
    public TestWindow()
    {
        InitializeComponent();
    }
    private void ButtonTest_OnClick(object sender, RoutedEventArgs e)
    {
    }
}

/*

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.
/// <summary>
/// An empty window that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class TestWindow
{
    private readonly Dictionary<string, DisplayItem> _photos = new();
    private List<string> _files;

    public TestWindow()
    {
        InitializeComponent();
    }

    private async void ButtonTest_OnClick(object sender, RoutedEventArgs e)
    {
        GetFileListFromExplorer();
        _photos.Clear();

        var stopwatch = new Stopwatch();

        for (var index = 0; index < Math.Min(_files.Count, 100); index++)
        {
            var file = _files[index];

            var res = Task.Run(() => GetInitialPreview(file));
            res.Wait();
            continue;

            async Task GetInitialPreview(string fileName)
            {
                var k = await ImageUtil.GetPreview(D2dCanvas, fileName);
                _photos.Add(fileName, k);
            }
        }

        stopwatch.Start();

        foreach (var photo in _photos)
        {
            var res = Task.Run(() => GetInitialPreview(photo.Key, photo.Value.Bitmap));
            res.Wait();
            continue;

            async Task GetInitialPreview(string fileName, CanvasBitmap bitmap)
            {
                PhotoDiskCacher.Instance.PutInCache(fileName, bitmap);
            }
        }

        //foreach (var file in _files)
        //{
        //    var k = await ImageUtil.GetHqImage(D2dCanvas, file);
        //    _photos.Add(k);
        //}



        stopwatch.Stop();

        var messageDialog = new MessageDialog($"Time elapsed: {stopwatch.ElapsedMilliseconds}");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(messageDialog, hwnd);
        await messageDialog.ShowAsync();
    }

    private void GetFileListFromExplorer()
    {
        //var supportedExtensions = Util.SupportedExtensions;
        //_files = App.Debug
        //    ? Util.FindAllFilesFromDirectory(App.DebugTestFolder)
        //    : Util.FindAllFilesFromExplorerWindowNative();
        //_files = _files.Where(s =>
        //    supportedExtensions.Contains(Path.GetExtension(s).ToUpperInvariant())).ToList();
    }
}

*/