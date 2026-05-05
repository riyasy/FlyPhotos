using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.UI.Xaml;
using WinRT.Interop;

// Required for WindowNative

namespace FlyPhotos.Services;

    /// <summary>
    /// Provides services for invoking the native Windows Share Dialog in a WinUI 3 application.
    /// It handles the interop required to connect the Windows.ApplicationModel.DataTransfer APIs 
    /// with the window handle of a WinUI 3 application.
    /// </summary>
    public static class FileShareDialogService
    {
    /// <summary>
    /// Opens the Windows Share Dialog for the specified file path.
    /// </summary>
    /// <param name="window">The current WinUI 3 Window.</param>
    /// <param name="filePath">The absolute path to the file you want to share.</param>
    public static void ShareFile(Window window, string filePath)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        // 1. Get the Window Handle (HWND) of the provided WinUI 3 window
        IntPtr hwnd = WindowNative.GetWindowHandle(window);

        // 2. Get the DataTransferManager for this specific window using Interop
        DataTransferManager dataTransferManager = DataTransferManagerInterop.GetForWindow(hwnd);

        // 3. Define the event handler as a local function to capture the 'filePath' and 'dataTransferManager'
        async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            // Unsubscribe immediately so it doesn't trigger on subsequent shares
            dataTransferManager.DataRequested -= OnDataRequested;

            DataRequest request = args.Request;

            // A title is REQUIRED by the Share API. If you don't set it, the dialog will crash.
            request.Data.Properties.Title = "Share File";
            request.Data.Properties.Description = "Sharing a file from FlyPhotos";

            // Because getting a StorageFile is an async operation, you MUST request a deferral.
            DataRequestDeferral deferral = request.GetDeferral();

            try
            {
                // Convert the string path into a Windows.Storage.StorageFile
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);

                // Add the file to the share payload
                request.Data.SetStorageItems(new List<IStorageItem> { file });
            }
            catch (Exception ex)
            {
                // If the file is locked, doesn't exist, etc.
                request.FailWithDisplayText($"Failed to prepare file for sharing: {ex.Message}");
            }
            finally
            {
                // Always complete the deferral so the UI knows we are done loading data
                deferral.Complete();
            }
        }

        // 4. Subscribe to the event before triggering the UI
        dataTransferManager.DataRequested += OnDataRequested;

        // 5. Trigger the native Windows Share UI using Interop
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
    }
}