#nullable enable
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using FlyPhotos.Infra.Localization;

namespace FlyPhotos.UI.Views;

// Mutates the ShortcutRow in place; the caller persists once the dialog closes, because a Reassign
// can strip a chord off a second command.

/// <summary>
/// Edits one command's shortcuts: lists what it has, removes any of them, captures a new one, and
/// resets to defaults. Changes apply immediately, like every other setting in this window, so the
/// only dialog button is Done.
/// </summary>
public sealed partial class ShortcutEditDialog : ContentDialog
{
    /// <summary>Finds the command that currently owns a chord, or null when free.</summary>
    private readonly Func<KeyChord, ShortcutRow?> _findOwner;

    private bool _capturing;
    private VirtualKey _pendingKey = VirtualKey.None;

    /// <summary>The chord as it stood when the key went down. Commit happens on key-up, by which
    /// point the user may already have let go of Ctrl — reading modifiers then would silently
    /// capture a bare letter.</summary>
    private KeyChord? _pendingChord;

    /// <summary>Chord captured but not yet applied because it clashed and is awaiting Reassign.</summary>
    private KeyChord? _conflictChord;

    public ShortcutRow Row { get; }

    public ShortcutEditDialog(ShortcutRow row, Func<KeyChord, ShortcutRow?> findOwner)
    {
        Row = row;
        _findOwner = findOwner;
        InitializeComponent();

        Title = row.Name;
        CloseButtonText = L.Get("ShortcutCapture_DoneButton");
        TxtCapture.Text = L.Get("ShortcutCapture_Idle");
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
        TxtCapture.Text = L.Get("ShortcutCapture_Waiting");
    }

    private void EndCapture()
    {
        _capturing = false;
        _pendingKey = VirtualKey.None;
        TxtCapture.Text = L.Get("ShortcutCapture_Idle");
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
            TxtCapture.Text = KeyChord.ModifierPreview();
            return;
        }

        if (e.Key is VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            Reject(L.Get("ShortcutCapture_WinKeyRejected"));
            return;
        }

        // 229 = VK_PROCESSKEY, raised while an IME is composing.
        if ((int)e.Key == 229)
        {
            Reject(L.Get("ShortcutCapture_ImeRejected"));
            return;
        }

        _pendingKey = e.Key;
        var chord = KeyChord.FromCurrentModifiers(e.Key);
        _pendingChord = chord;
        TxtCapture.Text = chord.Display();
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing || e.Key != _pendingKey || _pendingChord is not { } chord) return;
        e.Handled = true;
        _pendingKey = VirtualKey.None;
        _pendingChord = null;
        Commit(chord);
    }

    private void Commit(KeyChord chord)
    {
        var display = chord.Display();

        if (Row.HasChord(chord))
        {
            Info(string.Format(L.Get("ShortcutCapture_AlreadyOnThisCommand"), display));
            return;
        }

        // Not "owner != Row": the HasChord check above already returned for chords this row owns.
        var owner = _findOwner(chord);
        if (owner is not null)
        {
            // A reserved command's chord can be found but never taken from it.
            if (owner.IsReserved)
            {
                Info(string.Format(L.Get("ShortcutCapture_ReservedByOther"), display, owner.Name));
                return;
            }

            _conflictChord = chord;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = string.Format(L.Get("ShortcutCapture_Conflict"), display, owner.Name);
            ButtonReassign.Visibility = Visibility.Visible;
            StatusBar.IsOpen = true;
            return;
        }

        Apply(chord);
    }

    private void ButtonReassign_OnClick(object sender, RoutedEventArgs e)
    {
        if (_conflictChord is not { } chord) return;
        _findOwner(chord)?.RemoveChord(chord);
        Apply(chord);
        CaptureBox.Focus(FocusState.Programmatic);
    }

    private void Apply(KeyChord chord)
    {
        Row.Add(chord);
        _conflictChord = null;
        StatusBar.IsOpen = false;
        ButtonReassign.Visibility = Visibility.Collapsed;
        TxtCapture.Text = L.Get("ShortcutCapture_Waiting");
    }

    private void ChipRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShortcutKey key) Row.Keys.Remove(key);
    }

    private void ButtonResetDefault_OnClick(object sender, RoutedEventArgs e)
    {
        // Reset is an assignment like every other, so it has to clear the way first. Swap two
        // commands' keys, reset one of them, and without this both rows show the same chord - and
        // the routing table has already silently given it to whichever command it built last.
        // No Reassign prompt here: the user asked for the defaults back, and the defaults are what
        // the other command was holding on borrowed time.
        foreach (var chord in Row.DefaultChords)
            _findOwner(chord)?.RemoveChord(chord);

        Row.ResetToDefault();
        _conflictChord = null;
        StatusBar.IsOpen = false;
        ButtonReassign.Visibility = Visibility.Collapsed;
    }

    private void Reject(string reason)
    {
        _pendingKey = VirtualKey.None;
        _conflictChord = null;
        ButtonReassign.Visibility = Visibility.Collapsed;
        TxtCapture.Text = L.Get("ShortcutCapture_Waiting");
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = reason;
        StatusBar.IsOpen = true;
    }

    private void Info(string message)
    {
        _conflictChord = null;
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
