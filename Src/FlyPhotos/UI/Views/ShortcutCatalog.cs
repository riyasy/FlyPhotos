#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.System;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;

namespace FlyPhotos.UI.Views;

/// <summary>
/// Converts between a chord's two representations, which must never be confused:
/// a <b>token</b> ("Ctrl+Add") is culture-invariant, is what identity and persistence use, and
/// never changes; the <b>display</b> form ("Ctrl + Num +") is localized and is never compared.
/// Comparing display strings would silently orphan every binding when the UI language changes.
/// </summary>
public static class KeyChordText
{
    // The keys that type + and - are layout-dependent and Windows.System.VirtualKey does not name
    // the OEM range, so (VirtualKey)187 would stringify as "187". Naming them keeps tokens readable
    // and keeps a binding attached to the + key rather than to one layout's scan code.
    private const string TokenPlus = "OemPlus";
    private const string TokenMinus = "OemMinus";

    private const string TokenCtrl = "Ctrl";
    private const string TokenAlt = "Alt";
    private const string TokenShift = "Shift";

    private static readonly VirtualKey PlusKey = Util.GetKeyThatProduces('+');
    private static readonly VirtualKey MinusKey = Util.GetKeyThatProduces('-');

    /// <summary>Builds the invariant token for a key plus the modifiers currently held.
    /// Modifier order is fixed so two presses of the same chord always produce the same token.</summary>
    public static string Token(VirtualKey key)
    {
        var parts = Modifiers();
        parts.Add(KeyToken(key));
        return string.Join("+", parts);
    }

    /// <summary>Renders a token for display, in the user's language.</summary>
    public static string Display(string? token)
    {
        // No enum name contains '+', so splitting on it is safe.
        if (string.IsNullOrEmpty(token)) return "...";
        var parts = token.Split('+');
        var shown = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length - 1; i++) shown.Add(ModifierDisplay(parts[i]));
        shown.Add(KeyDisplay(parts[^1]));
        return string.Join(" + ", shown);
    }

    /// <summary>The live modifier-only preview shown before a key lands.</summary>
    public static string ModifierPreview()
    {
        var parts = Modifiers().Select(ModifierDisplay).ToList();
        parts.Add("...");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// The same token with Shift added. Modifier order puts Shift immediately before the key, so
    /// inserting it there is all that is needed.
    /// </summary>
    public static string WithShift(string token)
    {
        var i = token.LastIndexOf('+');
        return i < 0 ? TokenShift + "+" + token : string.Concat(token.AsSpan(0, i), "+Shift+", token.AsSpan(i + 1));
    }

    /// <summary>
    /// True when every segment of a token is something <see cref="Token"/> could itself have
    /// produced. A hand-written default that fails this never fires and reports nothing, so the
    /// catalog's startup self-check leans on it.
    /// </summary>
    public static bool IsWellFormed(string token)
    {
        var parts = token.Split('+');
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] is not (TokenCtrl or TokenAlt or TokenShift)) return false;

        return parts[^1] is TokenPlus or TokenMinus || Enum.TryParse<VirtualKey>(parts[^1], out _);
    }

    private static List<string> Modifiers()
    {
        var parts = new List<string>(4);
        if (Util.IsControlPressed()) parts.Add(TokenCtrl);
        if (Util.IsAltPressed()) parts.Add(TokenAlt);
        if (Util.IsShiftPressed()) parts.Add(TokenShift);
        return parts;
    }

    private static string KeyToken(VirtualKey k) =>
        k == PlusKey ? TokenPlus :
        k == MinusKey ? TokenMinus :
        k.ToString();

    // Windows names the modifier keys differently per language (Strg, Maj, ...), so these are
    // looked up rather than printed. The token spelling above never changes.
    private static string ModifierDisplay(string modifierToken) => modifierToken switch
    {
        TokenCtrl => L.Get("ShortcutMod_Ctrl"),
        TokenAlt => L.Get("ShortcutMod_Alt"),
        TokenShift => L.Get("ShortcutMod_Shift"),
        _ => modifierToken
    };

    private static string KeyDisplay(string keyToken) => keyToken switch
    {
        // + and - are the same glyph in every language.
        TokenPlus => "+",
        TokenMinus => "-",
        _ => Enum.TryParse<VirtualKey>(keyToken, out var vk) ? KeyName(vk) : keyToken
    };

    /// <summary>Only the keys whose printed name is a word. Letters, digits and the function keys
    /// are the same everywhere and fall through to the enum name.</summary>
    private static string KeyName(VirtualKey k) => k switch
    {
        VirtualKey.Left => L.Get("ShortcutKey_LeftArrow"),
        VirtualKey.Right => L.Get("ShortcutKey_RightArrow"),
        VirtualKey.Up => L.Get("ShortcutKey_UpArrow"),
        VirtualKey.Down => L.Get("ShortcutKey_DownArrow"),
        VirtualKey.Escape => L.Get("ShortcutKey_Esc"),
        VirtualKey.Back => L.Get("ShortcutKey_Backspace"),
        VirtualKey.PageUp => L.Get("ShortcutKey_PageUp"),
        VirtualKey.PageDown => L.Get("ShortcutKey_PageDown"),
        VirtualKey.Home => L.Get("ShortcutKey_Home"),
        VirtualKey.End => L.Get("ShortcutKey_End"),
        VirtualKey.Enter => L.Get("ShortcutKey_Enter"),
        VirtualKey.Delete => L.Get("ShortcutKey_Delete"),
        VirtualKey.Space => L.Get("ShortcutKey_Space"),
        VirtualKey.Tab => L.Get("ShortcutKey_Tab"),
        VirtualKey.Add => L.Get("ShortcutKey_NumAdd"),
        VirtualKey.Subtract => L.Get("ShortcutKey_NumSubtract"),
        VirtualKey.Multiply => L.Get("ShortcutKey_NumMultiply"),
        VirtualKey.Divide => L.Get("ShortcutKey_NumDivide"),
        VirtualKey.Decimal => L.Get("ShortcutKey_NumDecimal"),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 =>
            string.Format(L.Get("ShortcutKey_NumPad"), k - VirtualKey.NumberPad0),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(k - VirtualKey.Number0)).ToString(),
        _ => k.ToString()
    };
}

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
/// <see cref="Text"/> is the localized display form ("Ctrl + Num +") and is never compared.
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
    public CommandId Id { get; }

    /// <summary>Resolved once at construction. The app requires a restart to change language, so
    /// there is nothing to invalidate, and search re-reads these on every keystroke.</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>Segoe Fluent glyph for the row icon, built from a code point so the source file
    /// stays ASCII - a literal private-use character does not survive every editor round-trip.</summary>
    public string Glyph { get; }

    /// <summary>Reserved commands cannot be rebound at all, so they have no edit button and ignore
    /// anything saved against them.</summary>
    public bool IsReserved { get; }

    public string[] DefaultTokens { get; }

    public ObservableCollection<ShortcutKey> Keys { get; } = [];

    public ShortcutRow(CommandId id, int glyphCode, bool isReserved, bool hasDescription,
                       string[] defaultTokens)
    {
        Id = id;
        Glyph = char.ConvertFromUtf32(glyphCode);
        IsReserved = isReserved;
        DefaultTokens = defaultTokens;
        Name = L.Get($"ShortcutName_{id}");
        Description = hasDescription ? L.Get($"ShortcutDesc_{id}") : string.Empty;
        ResetToDefault();
        Keys.CollectionChanged += (_, _) => RaiseDerived();
    }

    public bool HasToken(string token) => Keys.Any(k => k.Token == token);

    /// <summary>True once the user's chords differ from the shipped ones, which is the only case
    /// worth writing to usersettings.json.</summary>
    public bool IsModified => !Keys.Select(k => k.Token).SequenceEqual(DefaultTokens);

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

    public void ResetToDefault() => SetTokens(DefaultTokens);

    public void SetTokens(IEnumerable<string> tokens)
    {
        Keys.Clear();
        foreach (var t in tokens) Keys.Add(new ShortcutKey(t, this));
    }

    /// <summary>Matches the search box against what the user can actually see - the display name,
    /// the hint, and the rendered chord text. Never the token.</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Keys.Any(k => k.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase));

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

/// <summary>
/// The single source of truth for keyboard commands: which keys they ship with, which of them can
/// be rebound, and the defaults-plus-overrides resolution that <see cref="PhotoDisplayWindow"/>
/// routes on. Defaults live here in code and only the user's changes reach usersettings.json, so a
/// command added in a later release ships working keys to every existing user with no migration.
/// </summary>
internal static class ShortcutCatalog
{
    /// <summary>Pure data: no display strings and no UI objects, so the photo window can resolve
    /// its routing table at construction without touching the resource loader.</summary>
    private sealed record Def(CommandId Id, int Glyph, bool Reserved, bool HasDescription, string[] Tokens);

    private static readonly (string GroupKey, Def[] Rows)[] Table =
    [
        ("Navigation",
        [
            Row(CommandId.NextPhoto, 0xE970, "Right"),
            Row(CommandId.PrevPhoto, 0xE96F, "Left"),
            Row(CommandId.FirstPhoto, 0xE892, "Home"),
            Row(CommandId.LastPhoto, 0xE893, "End"),
            Hinted(CommandId.NextPage, 0xF586, "Alt+Right"),
            Hinted(CommandId.PrevPage, 0xF587, "Alt+Left")
        ]),

        ("ZoomPan",
        [
            Row(CommandId.ZoomIn, 0xE8A3, "Up", "Ctrl+OemPlus", "Ctrl+Add"),
            Row(CommandId.ZoomOut, 0xE71F, "Down", "Ctrl+OemMinus", "Ctrl+Subtract"),
            Hinted(CommandId.StepZoomIn, 0xE8A3, "PageUp"),
            Hinted(CommandId.StepZoomOut, 0xE71F, "PageDown"),
            Row(CommandId.ActualSize, 0xE799, "A"),
            Row(CommandId.FitToWindow, 0xE9A6, "F"),
            Row(CommandId.PanUp, 0xE7C2, "Ctrl+Up"),
            Row(CommandId.PanDown, 0xE7C2, "Ctrl+Down"),
            Row(CommandId.PanLeft, 0xE7C2, "Ctrl+Left"),
            Row(CommandId.PanRight, 0xE7C2, "Ctrl+Right")
        ]),

        ("Rotate",
        [
            Row(CommandId.RotateLeft, 0xE89E, "L"),
            Row(CommandId.RotateRight, 0xE89E, "R")
        ]),

        ("View",
        [
            Row(CommandId.FullScreen, 0xE740, "F11"),
            Row(CommandId.MaximizeRestore, 0xE922, "Enter"),
            Hinted(CommandId.PhotoInfoPanel, 0xE946, "I"),
            Reserved(CommandId.CloseApp, 0xE8BB, "Escape")
        ]),

        ("File",
        [
            Hinted(CommandId.CopyPhoto, 0xE8C8, "Ctrl+C"),
            Reserved(CommandId.DeletePhoto, 0xE74D, "Delete"),
            Row(CommandId.RenamePhoto, 0xE8AC, "F2"),
            Row(CommandId.PrintPhoto, 0xE749, "P"),
            Hinted(CommandId.SharePhoto, 0xE72D, "S"),
            Row(CommandId.ShowInExplorer, 0xE8DA, "W"),
            Hinted(CommandId.FileProperties, 0xF167, "Alt+Enter"),
            Hinted(CommandId.FileDetails, 0xF167, "D"),
            Row(CommandId.MoreActionsMenu, 0xE712, "M")
        ]),

        ("OpenWith",
        [
            Row(CommandId.OpenWithApp1, 0xE8A7, "Ctrl+Number1", "Ctrl+NumberPad1"),
            Row(CommandId.OpenWithApp2, 0xE8A7, "Ctrl+Number2", "Ctrl+NumberPad2"),
            Row(CommandId.OpenWithApp3, 0xE8A7, "Ctrl+Number3", "Ctrl+NumberPad3"),
            Row(CommandId.OpenWithApp4, 0xE8A7, "Ctrl+Number4", "Ctrl+NumberPad4"),
            Hinted(CommandId.OpenWithPanel, 0xE71D, "E")
        ])
    ];

    /// <summary>Chords whose key must not also reach WinUI's own handling: Ctrl+C would trigger a
    /// browser-style copy, Enter would activate the focused button, and Delete moves focus.</summary>
    private static readonly HashSet<CommandId> SuppressDefault =
        [CommandId.CopyPhoto, CommandId.DeletePhoto, CommandId.MaximizeRestore, CommandId.FileProperties];

    /// <summary>Commands that open a navigation burst, which parks the HQ cache tier until a
    /// <c>Brake()</c> unwinds it. Key-up on whatever chord is bound to one has to brake, or every
    /// photo stays at preview quality for the rest of the session.</summary>
    private static readonly HashSet<CommandId> Burst = [CommandId.NextPhoto, CommandId.PrevPhoto];

    private static readonly Dictionary<CommandId, string[]> DefaultsById =
        Table.SelectMany(g => g.Rows).ToDictionary(r => r.Id, r => r.Tokens);

    private static readonly HashSet<CommandId> ReservedIds =
        Table.SelectMany(g => g.Rows).Where(r => r.Reserved).Select(r => r.Id).ToHashSet();

    public static bool Suppresses(CommandId id) => SuppressDefault.Contains(id);

    public static bool IsBurst(CommandId id) => Burst.Contains(id);

    /// <summary>Every command, in display order, at its default chords. Builds the display strings,
    /// so call it only from the settings window - never on the photo window's startup path.</summary>
    public static List<ShortcutGroup> BuildAll() =>
    [
        .. Table.Select(g => new ShortcutGroup(
            L.Get($"ShortcutGroup_{g.GroupKey}"),
            new ObservableCollection<ShortcutRow>(
                g.Rows.Select(r => new ShortcutRow(r.Id, r.Glyph, r.Reserved, r.HasDescription, r.Tokens)))))
    ];

    /// <summary>The command that already owns <paramref name="token"/>, or null if it is free.</summary>
    public static ShortcutRow? FindOwner(List<ShortcutGroup> groups, string token) =>
        groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.HasToken(token));

    /// <summary>Overlays what the user saved onto freshly built default rows.</summary>
    public static void ApplySavedBindings(List<ShortcutGroup> groups)
    {
        foreach (var row in groups.SelectMany(g => g.Rows))
            if (TryGetSaved(row.Id, out var tokens))
                row.SetTokens(tokens);
    }

    /// <summary>Writes only what differs from the defaults, then persists.</summary>
    public static async Task SaveBindingsAsync(List<ShortcutGroup> groups)
    {
        var overrides = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in groups.SelectMany(g => g.Rows).Where(r => r.IsModified))
            overrides[row.Id.ToString()] = row.Keys.Select(k => k.Token).ToList();

        AppConfig.Settings.KeyBindings = overrides;
        await AppConfig.SaveAsync();
    }

    /// <summary>
    /// Defaults overlaid with the user's overrides, inverted into the lookup the key handler needs.
    /// A token that no longer parses to anything simply never matches, so a hand-mangled
    /// usersettings.json costs the user that one binding rather than throwing at startup.
    /// </summary>
    public static Dictionary<string, CommandId> Resolve()
    {
        var routes = new Dictionary<string, CommandId>(StringComparer.Ordinal);

        foreach (var (id, defaults) in DefaultsById)
        {
            var tokens = TryGetSaved(id, out var saved) ? saved : (IReadOnlyList<string>)defaults;
            foreach (var token in tokens)
                if (!string.IsNullOrWhiteSpace(token)) routes[token] = id;
        }

        // The keys that type + and - need Shift on US and most EU layouts, and the layout lookup
        // discards that bit. Registering the Shift variant keeps Ctrl++ zooming exactly as it did
        // before Shift became part of the chord.
        // ponytail: only these two keys need it - no other default involves a shifted character.
        foreach (var (token, id) in routes.ToArray())
            if (token.Contains("Oem", StringComparison.Ordinal) && !token.Contains("Shift", StringComparison.Ordinal))
                routes.TryAdd(KeyChordText.WithShift(token), id);

        return routes;
    }

#if DEBUG
    /// <summary>
    /// Catches the table mistakes that have no other symptom until a user hits them: a command with
    /// no defaults, a chord claimed by two commands, or an id nobody wrote a handler for. Runs once
    /// at startup, costs nothing in release, and needs no test framework.
    /// </summary>
    public static void AssertConsistent(ICollection<CommandId> handled)
    {
        var owner = new Dictionary<string, CommandId>(StringComparer.Ordinal);

        foreach (var id in Enum.GetValues<CommandId>())
        {
            Debug.Assert(DefaultsById.ContainsKey(id), $"{id} is missing from the catalog.");
            Debug.Assert(handled.Contains(id), $"{id} has no handler in PhotoDisplayWindow.");
        }

        foreach (var (id, tokens) in DefaultsById)
            foreach (var token in tokens)
            {
                Debug.Assert(KeyChordText.IsWellFormed(token),
                    $"{id}'s default \"{token}\" is not a chord this app can ever receive.");
                Debug.Assert(owner.TryAdd(token, id),
                    $"{token} is a default for both {id} and {owner.GetValueOrDefault(token)}.");
            }
    }
#endif

    /// <summary>Reserved commands ignore saved bindings outright - Escape must always close the app,
    /// including when it is the only way out of a capture dialog, and Delete keeps the key every
    /// file manager uses.</summary>
    private static bool TryGetSaved(CommandId id, out List<string> tokens)
    {
        tokens = [];
        if (ReservedIds.Contains(id)) return false;
        return AppConfig.Settings.KeyBindings.TryGetValue(id.ToString(), out tokens!);
    }

    private static Def Row(CommandId id, int glyph, params string[] tokens) =>
        new(id, glyph, false, false, tokens);

    /// <summary>A command whose name alone does not explain it, so it also has a description.</summary>
    private static Def Hinted(CommandId id, int glyph, params string[] tokens) =>
        new(id, glyph, false, true, tokens);

    /// <summary>A command whose keys are fixed and cannot be edited. Always described, since the
    /// missing edit button needs explaining.</summary>
    private static Def Reserved(CommandId id, int glyph, params string[] tokens) =>
        new(id, glyph, true, true, tokens);
}
