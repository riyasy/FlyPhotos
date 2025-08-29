// InitWindow.xaml.cs
using FlyPhotos.AppSettings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents; 
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using FlyPhotos.Utils;
using WinRT.Interop;

namespace FlyPhotos.Views
{
    public sealed partial class InitWindow : Window
    {
        // This is the list of supported file extensions you can modify.
        private readonly List<string> _supportedFileExtensions;

        public InitWindow()
        {
            this.InitializeComponent();

            _supportedFileExtensions = Util.SupportedExtensions;

            var hWnd = WindowNative.GetWindowHandle(this);
            var myWndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(myWndId);

            var titleBar = appWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.Gray;

            ((FrameworkElement)Content).RequestedTheme = AppConfig.Settings.Theme;
        }

        public string SelectedFile { get; private set; }

        private async void OpenFileHyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            await PickAndProcessFileAsync();
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            // Check if the dragged content contains storage items (files)
            // If it's a file, show the "copy" icon.
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                e.AcceptedOperation = DataPackageOperation.Copy;

        }

        private async void DropArea_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (!items.Any()) return;
            var file = items.First() as StorageFile;

            if (file != null && _supportedFileExtensions.Contains(file.FileType.ToLowerInvariant()))
                ProcessSelectedFile(file);
            else
                await ShowMessageDialog("Unsupported File", "The dragged file is not a supported image type.");

        }

        // Shared logic for opening the file picker
        private async Task PickAndProcessFileAsync()
        {
            var filePicker = new FileOpenPicker();

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(filePicker, windowHandle);

            filePicker.ViewMode = PickerViewMode.Thumbnail;
            filePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

            foreach (var ext in _supportedFileExtensions)
                filePicker.FileTypeFilter.Add(ext);


            StorageFile file = await filePicker.PickSingleFileAsync();
            if (file != null)
                ProcessSelectedFile(file);

        }

        private void ProcessSelectedFile(StorageFile file)
        {
            SelectedFile = file.Path;
            this.Close();
        }

        private async Task ShowMessageDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}