#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NLog;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;

namespace FlyPhotos.UI.Actions;

/// <summary>
/// Orchestrates the "delete current photo" flow: validation, optional confirmation dialog,
/// the delete itself, and the success/failure follow-ups. The host wires up each lifecycle
/// step as a delegate; this class owns only the two dialogs (so it needs a <see cref="XamlRoot"/>).
/// </summary>
internal class FileActionDelete
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly Func<XamlRoot?> _xamlRootProvider;
    private readonly Func<bool> _preExecute;
    private readonly Action _preExecuteFailed;
    private readonly Func<Task<DeleteResult>> _execute;
    private readonly Action _executeFailed;
    private readonly Func<Task> _postExecute;
    private readonly Func<Task> _postExecuteLastItem;

    public FileActionDelete(
        Func<XamlRoot?> xamlRootProvider,
        Func<bool> preExecute,
        Action preExecuteFailed,
        Func<Task<DeleteResult>> execute,
        Action executeFailed,
        Func<Task> postExecute,
        Func<Task> postExecuteLastItem)
    {
        _xamlRootProvider = xamlRootProvider;
        _preExecute = preExecute;
        _preExecuteFailed = preExecuteFailed;
        _execute = execute;
        _executeFailed = executeFailed;
        _postExecute = postExecute;
        _postExecuteLastItem = postExecuteLastItem;
    }

    public async Task ExecuteAsync()
    {
        if (AppConfig.Volatile.IsSecondaryInstance) return;

        // 1. Pre-Execute validation
        if (!_preExecute())
        {
            _preExecuteFailed();
            return;
        }

        var xamlRoot = _xamlRootProvider();
        if (xamlRoot == null) return; // Failsafe if window isn't loaded

        // 2. Confirmation dialog
        if (AppConfig.Settings.ConfirmForDelete)
        {
            var confirmDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.Get("ConfirmDeletion/Title"),
                Content = L.Get("ConfirmDeletion/Message"),
                PrimaryButtonText = L.Get("ConfirmDeletion/DeleteButton"),
                CloseButtonText = L.Get("ConfirmDeletion/CancelButton"),
                DefaultButton = ContentDialogButton.Close
            };

            var dialogResult = await confirmDialog.ShowAsync();
            if (dialogResult != ContentDialogResult.Primary)
            {
                Logger.Info("User cancelled file deletion.");
                return;
            }
        }

        // 3. Execute
        var result = await _execute();

        // 4. Post-Execute
        if (result.DeleteSuccess)
        {
            if (result.IsLastPhoto)
                await _postExecuteLastItem();
            else
                await _postExecute();
        }
        else
        {
            _executeFailed();
            Logger.Error("Failed to delete file");

            var errorDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.Get("DeletionFailed/Title"),
                Content = $"{L.Get("DeletionFailed/Message")}{Environment.NewLine}{result.FailMessage}",
                CloseButtonText = L.Get("DeletionFailed/CloseButton")
            };
            await errorDialog.ShowAsync();
        }
    }
}
