#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.UI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NLog;
using Windows.System;

namespace FlyPhotos.UI.Controls;

/// <summary>
/// Rename flyout, anchored to a caller-supplied element. The extension is fixed and shown
/// read-only — only the file-name stem is editable — so a rename can never change the file's
/// format. The control owns its layout, validation, shake ("shrug") animation and error dialog;
/// the host performs the actual rename via <see cref="RenameRequested"/>.
/// </summary>
public sealed partial class RenameFlyoutControl : UserControl
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private Flyout? _flyout;
    private string? _targetPath;

    /// <summary>Host performs the rename for the given new file name and returns the result.</summary>
    internal event Func<string, Task<RenameResult>>? RenameRequested;

    public RenameFlyoutControl()
    {
        InitializeComponent();
        RtlLayoutBehavior.ApplyFlowDirection(this, AppConfig.Settings.Language);
        RenameOkButton.Content = L.Get("RenameFlyout/OkButton");
    }

    public void Show(FrameworkElement anchor, string filePath)
    {
        _targetPath = filePath;
        RenameTextBox.Text = Path.GetFileNameWithoutExtension(filePath);
        RenameExtensionText.Text = Path.GetExtension(filePath);

        if (_flyout == null)
        {
            _flyout = new Flyout { Content = this, Placement = FlyoutPlacementMode.Top };
            _flyout.Opened += (_, _) => RenameTextBox.SelectAll();
        }
        _flyout.ShowAt(anchor);
    }

    // Dismiss when the app navigates to a different file — the flyout's target must not silently
    // drift onto whatever photo happens to be displayed by the time OK is pressed (same rationale
    // as ExifPanel.CloseIfFileChanged). Full paths are unique, so they distinguish real navigation.
    public void CloseIfFileChanged(string currentFilePath)
    {
        if (_targetPath != null &&
            !string.Equals(_targetPath, currentFilePath, StringComparison.OrdinalIgnoreCase))
            _flyout?.Hide();
    }

    private async void RenameOk_OnClick(object sender, RoutedEventArgs e) => await TrySubmitAsync();

    private async void RenameTextBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await TrySubmitAsync();
    }

    private async Task TrySubmitAsync()
    {
        var newStem = RenameTextBox.Text.Trim();
        var currentStem = _targetPath == null ? "" : Path.GetFileNameWithoutExtension(_targetPath);

        if (newStem.Length == 0 || newStem == currentStem)
        {
            _flyout?.Hide();
            return;
        }
        if (newStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            await ShrugAsync();
            await ShowErrorAsync(L.Get("RenameFailed/InvalidCharacters"));
            return;
        }

        var newFileName = newStem + Path.GetExtension(_targetPath ?? "");
        var result = RenameRequested == null
            ? new RenameResult(false, "No rename handler.")
            : await RenameRequested(newFileName);

        if (result.Success)
        {
            _flyout?.Hide();
        }
        else
        {
            Logger.Error($"Rename failed: {result.FailMessage}");
            await ShrugAsync();
            await ShowErrorAsync(result.FailMessage);
        }
    }

    /// <summary>Shows the rename-failure dialog. The flyout is left open behind it so the user can retry.</summary>
    private async Task ShowErrorAsync(string failMessage)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Get("RenameFailed/Title"),
            Content = $"{L.Get("RenameFailed/Message")}{Environment.NewLine}{failMessage}",
            CloseButtonText = L.Get("RenameFailed/CloseButton")
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// Decaying horizontal shake on the flyout content, signalling a rejected action. Awaitable so a
    /// caller can show the error dialog only once the shake has actually played out.
    /// </summary>
    private Task ShrugAsync()
    {
        var tcs = new TaskCompletionSource();
        void OnDone(object? s, object e)
        {
            ShrugStoryboard.Completed -= OnDone;
            tcs.TrySetResult();
        }
        ShrugStoryboard.Completed += OnDone;
        ShrugStoryboard.Begin();
        return tcs.Task;
    }
}
