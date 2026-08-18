#nullable enable
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FlyPhotos.UI.Views;

// Prototype editor. It really captures keys and really reports conflicts, but every
// change lands on the in-memory ShortcutRow only - nothing is persisted and no real binding
// changes. See docs/03_think_later/20260818_shortcut_customization_design.md.

/// <summary>
/// Edits one command's shortcuts: lists what it has, removes any of them, captures a new one, and
/// resets to defaults. Changes apply immediately, like every other setting in this window, so the
/// only dialog button is Done.
/// </summary>
public sealed partial class ShortcutEditDialog : ContentDialog
{
    /// <summary>Finds the command that currently owns a chord token, or null when free.</summary>
    private readonly Func<string, ShortcutRow?> _findOwner;

    private bool _capturing;
    private VirtualKey _pendingKey = VirtualKey.None;

    /// <summary>Chord captured but not yet applied because it clashed and is awaiting Reassign.</summary>
    private string? _conflictToken;

    public ShortcutRow Row { get; }

    public ShortcutEditDialog(ShortcutRow row, Func<string, ShortcutRow?> findOwner)
    {
        Row = row;
        _findOwner = findOwner;
        InitializeComponent();

        Title = row.Name;
        ButtonReassign.Visibility = Visibility.Collapsed;

        Opened += (_, _) => CaptureBox.Focus(FocusState.Programmatic);

        // Capture runs only while the box has focus, so Tab always gets the user back out.
        CaptureBox.GotFocus += (_, _) => BeginCapture();
        CaptureBox.LostFocus += (_, _) => EndCapture();
        CaptureBox.Tapped += (_, _) => CaptureBox.Focus(FocusState.Programmatic);
        CaptureBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        CaptureBox.AddHandler(KeyUpEvent, new KeyEventHandler(OnKeyUp), true);
    }

    private void BeginCapture()
    {
        _capturing = true;
        _pendingKey = VirtualKey.None;
        TxtCapture.Text = "Waiting for keys...";
    }

    private void EndCapture()
    {
        _capturing = false;
        _pendingKey = VirtualKey.None;
        TxtCapture.Text = "Click here to add a shortcut";
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;

        // Tab is deliberately not captured: it is the only way for a keyboard user to leave the
        // capture box and reach the chips and the reset button. The cost is that Tab itself can
        // never be bound, which is an acceptable trade in a photo viewer.
        if (e.Key == VirtualKey.Tab) return;

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            return;
        }

        if (IsModifier(e.Key))
        {
            TxtCapture.Text = KeyChordText.ModifierPreview();
            return;
        }

        if (e.Key is VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            Reject("Windows reserves shortcuts that use the Windows key.");
            return;
        }

        // 229 = VK_PROCESSKEY, raised while an IME is composing.
        if ((int)e.Key == 229)
        {
            Reject("Switch to a Latin input method to set a shortcut.");
            return;
        }

        _pendingKey = e.Key;
        TxtCapture.Text = KeyChordText.Display(KeyChordText.Token(e.Key));
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing || _pendingKey == VirtualKey.None || e.Key != _pendingKey) return;
        e.Handled = true;
        Commit(KeyChordText.Token(_pendingKey));
        _pendingKey = VirtualKey.None;
    }

    private void Commit(string token)
    {
        var display = KeyChordText.Display(token);

        if (Row.HasToken(token))
        {
            Info($"{display} is already assigned to this command.");
            return;
        }

        var owner = _findOwner(token);
        if (owner != null && owner != Row)
        {
            _conflictToken = token;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = $"{display} is already used by \"{owner.Name}\". Reassigning it will leave that command without this shortcut.";
            ButtonReassign.Visibility = Visibility.Visible;
            StatusBar.IsOpen = true;
            return;
        }

        Apply(token);
    }

    private void ButtonReassign_OnClick(object sender, RoutedEventArgs e)
    {
        if (_conflictToken is not { } token) return;
        _findOwner(token)?.RemoveToken(token);
        Apply(token);
        CaptureBox.Focus(FocusState.Programmatic);
    }

    private void Apply(string token)
    {
        Row.Add(token);
        _conflictToken = null;
        StatusBar.IsOpen = false;
        ButtonReassign.Visibility = Visibility.Collapsed;
        TxtCapture.Text = "Waiting for keys...";
    }

    private void ChipRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShortcutKey key) Row.Remove(key);
    }

    private void ButtonResetDefault_OnClick(object sender, RoutedEventArgs e)
    {
        Row.ResetToDefault();
        _conflictToken = null;
        StatusBar.IsOpen = false;
        ButtonReassign.Visibility = Visibility.Collapsed;
    }

    private void Reject(string reason)
    {
        _pendingKey = VirtualKey.None;
        _conflictToken = null;
        ButtonReassign.Visibility = Visibility.Collapsed;
        TxtCapture.Text = "Waiting for keys...";
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = reason;
        StatusBar.IsOpen = true;
    }

    private void Info(string message)
    {
        _conflictToken = null;
        ButtonReassign.Visibility = Visibility.Collapsed;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static bool IsModifier(VirtualKey k) =>
        k is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
          or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
          or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu;
}
