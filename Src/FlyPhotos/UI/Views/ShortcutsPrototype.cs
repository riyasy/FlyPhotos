#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Windows.System;
using FlyPhotos.Infra.Utils;

namespace FlyPhotos.UI.Views;

/// <summary>
/// Converts between a chord's two representations, which must never be confused:
/// a <b>token</b> ("Ctrl+Add") is culture-invariant, is what identity and persistence use, and
/// never changes; the <b>display</b> form ("Ctrl + Num +") is for humans and will be localized.
/// Comparing display strings would silently orphan every binding when the UI language changes.
/// </summary>
public static class KeyChordText
{
    // Windows.System.VirtualKey does not name the OEM range, so (VirtualKey)187 would stringify as
    // "187". These two are the only OEM keys FlyPhotos binds by default; naming them keeps the
    // catalog readable. The real implementation derives them from the active keyboard layout.
    private const int OemPlus = 187;
    private const int OemMinus = 189;

    /// <summary>Builds the invariant token for a key plus the modifiers currently held.
    /// Modifier order is fixed so two presses of the same chord always produce the same token.</summary>
    public static string Token(VirtualKey key)
    {
        var parts = Modifiers();
        parts.Add(KeyToken(key));
        return string.Join("+", parts);
    }

    /// <summary>Renders a token for display.</summary>
    public static string Display(string? token)
    {
        // No enum name contains '+', so splitting on it is safe.
        if (string.IsNullOrEmpty(token)) return "...";
        var parts = token.Split('+');
        var shown = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length - 1; i++) shown.Add(parts[i]);
        shown.Add(KeyDisplay(parts[^1]));
        return string.Join(" + ", shown);
    }

    /// <summary>The live modifier-only preview shown before a key lands.</summary>
    public static string ModifierPreview()
    {
        var parts = Modifiers();
        parts.Add("...");
        return string.Join(" + ", parts);
    }

    private static List<string> Modifiers()
    {
        var parts = new List<string>(4);
        if (Util.IsControlPressed()) parts.Add("Ctrl");
        if (Util.IsAltPressed()) parts.Add("Alt");
        if (Util.IsShiftPressed()) parts.Add("Shift");
        return parts;
    }

    private static string KeyToken(VirtualKey k) => (int)k switch
    {
        OemPlus => nameof(OemPlus),
        OemMinus => nameof(OemMinus),
        _ => k.ToString()
    };

    private static string KeyDisplay(string keyToken) => keyToken switch
    {
        nameof(OemPlus) => "+",
        nameof(OemMinus) => "-",
        _ => Enum.TryParse<VirtualKey>(keyToken, out var vk) ? KeyName(vk) : keyToken
    };

    private static string KeyName(VirtualKey k) => k switch
    {
        VirtualKey.Left => "Left Arrow",
        VirtualKey.Right => "Right Arrow",
        VirtualKey.Up => "Up Arrow",
        VirtualKey.Down => "Down Arrow",
        VirtualKey.Escape => "Esc",
        VirtualKey.Back => "Backspace",
        VirtualKey.PageUp => "Page Up",
        VirtualKey.PageDown => "Page Down",
        VirtualKey.Add => "Num +",
        VirtualKey.Subtract => "Num -",
        VirtualKey.Multiply => "Num *",
        VirtualKey.Divide => "Num /",
        VirtualKey.Decimal => "Num .",
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => "Num " + (k - VirtualKey.NumberPad0),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(k - VirtualKey.Number0)).ToString(),
        _ => k.ToString()
    };
}

// ponytail: throw-away prototype for the Shortcuts tab. Mock data only - nothing here reads or
// writes AppConfig, and nothing is wired to PhotoDisplayWindow's real _keyActions table. Display
// names are hard-coded English on purpose so no .resw sweep across 20 locales is needed yet.
// Replace wholesale with Infra/Input/CommandCatalog when the real feature lands
// (see docs/03_think_later/20260818_shortcut_customization_design.md).

/// <summary>
/// Stable identity for a command. This - never the display name - is what persistence, conflict
/// lookup and any future migration key on, so a command can be renamed or translated without
/// orphaning the user's bindings.
///
/// Rules: never renumber, never reuse a removed name, and persist the MEMBER NAME rather than the
/// numeric value, so reordering this enum can never silently remap somebody's shortcuts.
/// </summary>
public enum CommandId
{
    NextPhoto, PrevPhoto, FirstPhoto, LastPhoto, NextPage, PrevPage,
    ZoomIn, ZoomOut, StepZoomIn, StepZoomOut, ActualSize, FitToWindow,
    PanUp, PanDown, PanLeft, PanRight,
    RotateLeft, RotateRight,
    FullScreen, MaximizeRestore, PhotoInfoPanel, CloseApp,
    CopyPhoto, DeletePhoto, RenamePhoto, PrintPhoto, SharePhoto, ShowInExplorer,
    FileProperties, FileDetails,
    OpenWithApp1, OpenWithApp2, OpenWithApp3, OpenWithApp4, OpenWithPanel,
    MoreActionsMenu
}

/// <summary>
/// One assigned chord. <see cref="Token"/> is the invariant identity ("Ctrl+Add");
/// <see cref="Text"/> is the localizable display form ("Ctrl + Num +") and is never compared.
/// The back-reference lets a chip's remove button find its command without the template
/// having to pass two values.
/// </summary>
public sealed class ShortcutKey(string token, ShortcutRow owner)
{
    public string Token { get; } = token;
    public ShortcutRow Owner { get; } = owner;
    public string Text => KeyChordText.Display(Token);
}

/// <summary>One command row: identity, display name, icon, and its current chords.</summary>
public sealed class ShortcutRow : INotifyPropertyChanged
{
    private readonly string[] _defaultTokens;

    public CommandId Id { get; }

    /// <summary>Display name. English-only in the prototype; the real catalog replaces this with a
    /// .resw key resolved through <c>L.Get</c>, reusing the strings that already exist for the
    /// toolbar and More menu (MenuItemPrint/Text, ButtonBackToolTip/Content, ...).</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>Segoe Fluent glyph for the row icon, built from a code point so the source file
    /// stays ASCII - a literal private-use character does not survive every editor round-trip.</summary>
    public string Glyph { get; }

    /// <summary>Reserved commands cannot be rebound at all - Esc is the universal way out of the
    /// app and of the capture dialog, so it must never be reassignable.</summary>
    public bool IsReserved { get; }

    public ObservableCollection<ShortcutKey> Keys { get; } = [];

    public ShortcutRow(CommandId id, string name, int glyphCode, string description, bool isReserved,
                       string[] defaultTokens)
    {
        Id = id;
        Name = name;
        Glyph = char.ConvertFromUtf32(glyphCode);
        Description = description;
        IsReserved = isReserved;
        _defaultTokens = defaultTokens;
        ResetToDefault();
        Keys.CollectionChanged += (_, _) => RaiseDerived();
    }

    public bool HasToken(string token) => Keys.Any(k => k.Token == token);

    // Visibility rather than bool so the DataTemplate needs no converter.
    public Visibility EditableVisibility => IsReserved ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoKeysVisibility => Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Add(string token)
    {
        if (!HasToken(token)) Keys.Add(new ShortcutKey(token, this));
    }

    public void Remove(ShortcutKey key) => Keys.Remove(key);

    public void RemoveToken(string token)
    {
        if (Keys.FirstOrDefault(k => k.Token == token) is { } stale) Keys.Remove(stale);
    }

    public void ResetToDefault()
    {
        Keys.Clear();
        foreach (var t in _defaultTokens) Keys.Add(new ShortcutKey(t, this));
    }

    /// <summary>Matches the search box against what the user can actually see - the display name,
    /// the hint, and the rendered chord text. Never the token.</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Keys.Any(k => k.Text.Contains(query, StringComparison.OrdinalIgnoreCase));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseDerived() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoKeysVisibility)));
}

/// <summary>A category heading plus the rows under it.</summary>
public sealed class ShortcutGroup(string name, ObservableCollection<ShortcutRow> rows)
{
    public string Name { get; } = name;
    public ObservableCollection<ShortcutRow> Rows { get; } = rows;
}

internal static class ShortcutsPrototypeCatalog
{
    /// <summary>Every command, in display order. Built once and kept - filtering produces a
    /// separate view over these same row instances, so edits survive a search.</summary>
    public static List<ShortcutGroup> BuildAll()
    {
        return
        [
            Group("Navigation",
                Row(CommandId.NextPhoto, "Next photo", 0xE970, "", "Right"),
                Row(CommandId.PrevPhoto, "Previous photo", 0xE96F, "", "Left"),
                Row(CommandId.FirstPhoto, "First photo", 0xE892, "", "Home"),
                Row(CommandId.LastPhoto, "Last photo", 0xE893, "", "End"),
                Row(CommandId.NextPage, "Next page", 0xF586, "Multi-page TIFF and ICO files", "Alt+Right"),
                Row(CommandId.PrevPage, "Previous page", 0xF587, "Multi-page TIFF and ICO files", "Alt+Left")),

            Group("Zoom and pan",
                Row(CommandId.ZoomIn, "Zoom in", 0xE8A3, "", "Up", "Ctrl+OemPlus", "Ctrl+Add"),
                Row(CommandId.ZoomOut, "Zoom out", 0xE71F, "", "Down", "Ctrl+OemMinus", "Ctrl+Subtract"),
                Row(CommandId.StepZoomIn, "Step zoom in", 0xE8A3, "Jumps to the next preset: 100%, 400%, Fit", "PageUp"),
                Row(CommandId.StepZoomOut, "Step zoom out", 0xE71F, "Jumps to the previous preset", "PageDown"),
                Row(CommandId.ActualSize, "Actual size (1:1)", 0xE799, "", "A"),
                Row(CommandId.FitToWindow, "Fit to window", 0xE9A6, "", "F"),
                Row(CommandId.PanUp, "Pan up", 0xE7C2, "", "Ctrl+Up"),
                Row(CommandId.PanDown, "Pan down", 0xE7C2, "", "Ctrl+Down"),
                Row(CommandId.PanLeft, "Pan left", 0xE7C2, "", "Ctrl+Left"),
                Row(CommandId.PanRight, "Pan right", 0xE7C2, "", "Ctrl+Right")),

            Group("Rotate",
                Row(CommandId.RotateLeft, "Rotate left", 0xE89E, "", "L"),
                Row(CommandId.RotateRight, "Rotate right", 0xE89E, "", "R")),

            Group("View",
                Row(CommandId.FullScreen, "Full screen", 0xE740, "", "F11"),
                Row(CommandId.MaximizeRestore, "Maximize or restore", 0xE922, "", "Enter"),
                Row(CommandId.PhotoInfoPanel, "Photo info panel", 0xE946, "Camera and EXIF metadata", "I"),
                Reserved(CommandId.CloseApp, "Close FlyPhotos", 0xE8BB, "Always available - cannot be reassigned", "Escape")),

            Group("File",
                Row(CommandId.CopyPhoto, "Copy photo", 0xE8C8, "Copies the file to the clipboard", "Ctrl+C"),
                Row(CommandId.DeletePhoto, "Delete photo", 0xE74D, "", "Delete"),
                Row(CommandId.RenamePhoto, "Rename photo", 0xE8AC, "", "F2"),
                Row(CommandId.PrintPhoto, "Print photo", 0xE749, "", "P"),
                Row(CommandId.SharePhoto, "Share photo", 0xE72D, "Opens the Windows share dialog", "S"),
                Row(CommandId.ShowInExplorer, "Show in Explorer", 0xE8DA, "", "W"),
                Row(CommandId.FileProperties, "File properties", 0xF167, "General tab", "Alt+Enter"),
                Row(CommandId.FileDetails, "File details", 0xF167, "Details tab", "D")),

            Group("Open with",
                Row(CommandId.OpenWithApp1, "Open with app 1", 0xE8A7, "", "Ctrl+Number1", "Ctrl+NumberPad1"),
                Row(CommandId.OpenWithApp2, "Open with app 2", 0xE8A7, "", "Ctrl+Number2", "Ctrl+NumberPad2"),
                Row(CommandId.OpenWithApp3, "Open with app 3", 0xE8A7, "", "Ctrl+Number3", "Ctrl+NumberPad3"),
                Row(CommandId.OpenWithApp4, "Open with app 4", 0xE8A7, "", "Ctrl+Number4", "Ctrl+NumberPad4"),
                Row(CommandId.OpenWithPanel, "Open with panel", 0xE71D, "Shows all configured apps", "E")),

            Group("Menus",
                Row(CommandId.MoreActionsMenu, "More actions menu", 0xE712, "", "M"))
        ];
    }

    /// <summary>The command that already owns <paramref name="token"/>, or null if it is free.</summary>
    public static ShortcutRow? FindOwner(List<ShortcutGroup> groups, string token) =>
        groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.HasToken(token));

    private static ShortcutGroup Group(string name, params ShortcutRow[] rows) =>
        new(name, new ObservableCollection<ShortcutRow>(rows));

    private static ShortcutRow Row(CommandId id, string name, int glyphCode, string description,
                                   params string[] defaultTokens) =>
        new(id, name, glyphCode, description, false, defaultTokens);

    /// <summary>A command whose keys are fixed and cannot be edited.</summary>
    private static ShortcutRow Reserved(CommandId id, string name, int glyphCode, string description,
                                        params string[] defaultTokens) =>
        new(id, name, glyphCode, description, true, defaultTokens);
}
