// InitWindow.xaml.cs

using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;

namespace FlyPhotos.UI.Views
{
    public sealed partial class InitWindow
    {
        public InitWindow()
        {
            InitializeComponent();
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = Colors.Gray;

            ((FrameworkElement)Content).RequestedTheme = AppConfig.Settings.Theme;

            MainLayout.KeyDown += delegate(object _, KeyRoutedEventArgs args)
            {
                if (args.Key == VirtualKey.Escape) Close();
            };
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
            if (items.Count == 0) return;
            var file = items[0] as StorageFile;
            if (file != null && Util.SupportedExtensions.Contains(file.FileType))
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

            foreach (var ext in Util.SupportedExtensions)
                filePicker.FileTypeFilter.Add(ext);


            StorageFile file = await filePicker.PickSingleFileAsync();
            if (file != null)
                ProcessSelectedFile(file);

        }

        private void ProcessSelectedFile(StorageFile file)
        {
            SelectedFile = file.Path;
            Close();
        }

        private async Task ShowMessageDialog(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}