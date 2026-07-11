#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.UI.Controls;

namespace FlyPhotos.UI.Actions;

/// <summary>
/// Orchestrates the "rename current photo" flow: validation guard, then showing the rename
/// flyout anchored to a caller-supplied element. Unlike <see cref="FileActionDelete"/>, the
/// user-interaction pieces (layout, validation, shake, error dialog) live in
/// <see cref="RenameFlyoutControl"/>; this service owns that control's lifecycle and wires the
/// host's rename handler to it. The host supplies each step as a delegate.
/// </summary>
internal class FileActionRename
{
    private readonly Func<bool> _preExecute;
    private readonly Action _preExecuteFailed;
    private readonly Func<FrameworkElement> _anchorProvider;
    private readonly Func<string> _filePathProvider;
    private readonly Func<string, Task<RenameResult>> _execute;

    private RenameFlyoutControl? _control;

    public FileActionRename(
        Func<bool> preExecute,
        Action preExecuteFailed,
        Func<FrameworkElement> anchorProvider,
        Func<string> filePathProvider,
        Func<string, Task<RenameResult>> execute)
    {
        _preExecute = preExecute;
        _preExecuteFailed = preExecuteFailed;
        _anchorProvider = anchorProvider;
        _filePathProvider = filePathProvider;
        _execute = execute;
    }

    public Task ExecuteAsync()
    {
        if (AppConfig.Volatile.IsSecondaryInstance) return Task.CompletedTask;

        if (!_preExecute())
        {
            _preExecuteFailed();
            return Task.CompletedTask;
        }

        _control ??= CreateControl();
        _control.Show(_anchorProvider(), _filePathProvider());
        return Task.CompletedTask;
    }

    // Dismiss the flyout when the app navigates to a different file (see
    // RenameFlyoutControl.CloseIfFileChanged for the rationale).
    public void Close(string currentFilePath) => _control?.CloseIfFileChanged(currentFilePath);

    private RenameFlyoutControl CreateControl()
    {
        var control = new RenameFlyoutControl();
        control.RenameRequested += _execute;
        return control;
    }
}
