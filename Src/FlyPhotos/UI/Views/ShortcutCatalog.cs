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

// The command table reads as a table only if the flags stay short. CommandFlags is declared in this
// same file, so the unqualified names below are never ambiguous.
using static FlyPhotos.UI.Views.CommandFlags;

namespace FlyPhotos.UI.Views;

/// <summary>
/// A key plus the modifiers held with it - the unit of identity for a shortcut.
///
/// This is a value type on purpose. Equality and hashing come free, so it drops into a dictionary
/// key with no allocation on the keypress path, and a chord no keyboard could ever send is simply
/// not expressible. Its two string forms are strictly boundaries and must never be confused:
/// <see cref="Format"/> is culture-invariant and is what usersettings.json stores;
/// <see cref="Display"/> is localized, is for humans, and is never compared. Comparing display text
/// would silently orphan every binding the moment the UI language changed.
/// </summary>
public readonly record struct KeyChord(VirtualKey Key, bool Ctrl, bool Alt, bool Shift)
{
    // Segment names for Format/TryParse. Invariant: these are identity, not UI text.
    private const string TokenCtrl = "Ctrl";
    private const string TokenAlt = "Alt";
    private const string TokenShift = "Shift";

    // The keys that type + and - are layout-dependent and Windows.System.VirtualKey does not name
    // the OEM range, so (VirtualKey)187 would format as "187". Naming them keeps the persisted file
    // readable and keeps a binding attached to the + key rather than to one layout's scan code.
    private const string TokenPlus = "OemPlus";
    private const string TokenMinus = "OemMinus";

    private static readonly (VirtualKey Key, bool NeedsShift) PlusKey = Util.GetKeyThatProduces('+');
    private static readonly (VirtualKey Key, bool NeedsShift) MinusKey = Util.GetKeyThatProduces('-');

    /// <summary>The chord for a key plus whatever modifiers are held at this instant. Correct at
    /// key-down; a caller committing on key-up must latch the chord rather than rebuild it, or a
    /// modifier released a moment early is lost.</summary>
    public static KeyChord FromCurrentModifiers(VirtualKey key) =>
        new(key, Util.IsControlPressed(), Util.IsAltPressed(), Util.IsShiftPressed());

    /// <summary>Culture-invariant identity, as persisted. Modifier order is fixed so the same chord
    /// always produces the same text.</summary>
    public string Format() => string.Join("+", Segments().Append(KeyToken(Key)));

    /// <summary>
    /// Reads a persisted chord. False for anything malformed, so a hand-edited usersettings.json
    /// costs the user that one binding instead of throwing at startup.
    /// </summary>
    public static bool TryParse(string? token, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('+');
        bool ctrl = false, alt = false, shift = false;

        for (var i = 0; i < parts.Length - 1; i++)
            switch (parts[i])
            {
                case TokenCtrl: ctrl = true; break;
                case TokenAlt: alt = true; break;
                case TokenShift: shift = true; break;
                default: return false;
            }

        var key = parts[^1] switch
        {
            TokenPlus => PlusKey.Key,
            TokenMinus => MinusKey.Key,
            _ => Enum.TryParse<VirtualKey>(parts[^1], out var vk) ? vk : VirtualKey.None
        };

        if (key == VirtualKey.None) return false;
        chord = new KeyChord(key, ctrl, alt, shift);
        return true;
    }

    /// <summary>Localized display form. Never compared, never persisted.</summary>
    public string Display() => Render(Segments(), KeyDisplay(Key));

    /// <summary>The modifier-only preview shown while the user is still holding modifiers and no
    /// key has landed yet.</summary>
    public static string ModifierPreview()
    {
        var held = new List<string>(3);
        if (Util.IsControlPressed()) held.Add(TokenCtrl);
        if (Util.IsAltPressed()) held.Add(TokenAlt);
        if (Util.IsShiftPressed()) held.Add(TokenShift);
        return Render(held, Pending);
    }

    /// <summary>
    /// Ctrl plus the key that types <paramref name="c"/>. Which key that is, and whether Shift is
    /// needed to reach it, are both layout-dependent, so a hand-written "Ctrl+OemPlus" is wrong on
    /// any layout where '+' is shifted.
    ///
    /// Where Shift is needed, both chords come back: Ctrl+Shift+= is what "Ctrl and +" actually
    /// sends, and plain Ctrl+= is what users press just as often. Both worked before Shift joined
    /// the chord and both still must. They are returned as real bindings rather than aliased in
    /// later, so the settings page can see them - an alias applied after resolution is invisible to
    /// conflict detection, which would let a user take Ctrl+Shift+= for another command and be told
    /// nothing.
    /// </summary>
    public static KeyChord[] CtrlChordsFor(char c)
    {
        var (key, needsShift) = c == '+' ? PlusKey : MinusKey;
        var plain = new KeyChord(key, Ctrl: true, Alt: false, Shift: false);
        return needsShift ? [plain with { Shift = true }, plain] : [plain];
    }

    /// <summary>Stands in for the key that has not been pressed yet.</summary>
    private const string Pending = "...";

    private List<string> Segments()
    {
        var parts = new List<string>(3);
        if (Ctrl) parts.Add(TokenCtrl);
        if (Alt) parts.Add(TokenAlt);
        if (Shift) parts.Add(TokenShift);
        return parts;
    }

    private static string Render(IEnumerable<string> modifierTokens, string keyText) =>
        string.Join(" + ", modifierTokens.Select(ModifierDisplay).Append(keyText));

    private static string KeyToken(VirtualKey k) =>
        k == PlusKey.Key ? TokenPlus :
        k == MinusKey.Key ? TokenMinus :
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

    /// <summary>Only the keys whose printed name is a word. Letters, digits and the function keys
    /// are the same everywhere and fall through to the enum name.</summary>
    private static string KeyDisplay(VirtualKey k) => k switch
    {
        // + and - are the same glyph in every language.
        _ when k == PlusKey.Key => "+",
        _ when k == MinusKey.Key => "-",
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
/// One assigned chord, as the settings page shows it. <see cref="Text"/> is fixed at construction
/// because rendering costs a resource lookup per segment and the search box re-reads every chip on
/// every keystroke.
/// </summary>
public sealed class ShortcutKey(KeyChord chord)
{
    public KeyChord Chord { get; } = chord;
    public string Text { get; } = chord.Display();
}

/// <summary>One command row: identity, display name, icon, and its current chords.</summary>
public sealed class ShortcutRow : INotifyPropertyChanged
{
    public CommandId Id { get; }

    /// <summary>Resolved once at construction. The app requires a restart to change language, so
    /// there is nothing to invalidate, and search re-reads these on every keystroke.</summary>
    public string Name { get; }

    public string Description { get; }

    /// <summary>Segoe Fluent glyph for the row icon, written as an escape so the source file stays
    /// ASCII - a literal private-use character does not survive every editor round-trip.</summary>
    public string Glyph { get; }

    /// <summary>Reserved commands cannot be rebound at all, so they have no edit button and ignore
    /// anything saved against them.</summary>
    public bool IsReserved { get; }

    public KeyChord[] DefaultChords { get; }

    public ObservableCollection<ShortcutKey> Keys { get; } = [];

    /// <summary>Takes the catalog's flags rather than bools unpacked from them, so the traits have
    /// one spelling. Internal because only the catalog builds rows.</summary>
    internal ShortcutRow(CommandId id, string glyph, CommandFlags flags, IEnumerable<KeyChord> chords,
                         KeyChord[] defaultChords)
    {
        Id = id;
        Glyph = glyph;
        IsReserved = flags.HasFlag(CommandFlags.Reserved);
        DefaultChords = defaultChords;
        Name = L.Get($"ShortcutName_{id}");

        // Whether a command has a hint is a fact about the resource file, so ask the resource file.
        // This used to be a Hinted flag on the row, which meant adding a description was a two-place
        // edit and forgetting the flag left a written string that nothing ever displayed.
        Description = L.GetOptional($"ShortcutDesc_{id}");

        SetChords(chords);
        Keys.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoKeysVisibility)));
    }

    public bool HasChord(KeyChord chord) => Keys.Any(k => k.Chord == chord);

    /// <summary>True once the user's chords differ from the shipped ones, which is the only case
    /// worth writing to usersettings.json.</summary>
    public bool IsModified => !Keys.Select(k => k.Chord).SequenceEqual(DefaultChords);

    // Visibility rather than bool so the DataTemplate needs no converter.
    public Visibility EditableVisibility => IsReserved ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoKeysVisibility => Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set by the search. The row stays in the tree and hides, rather than being filtered
    /// out of the bound collection — see <see cref="ApplyFilter"/>.</summary>
    public Visibility RowVisibility
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowVisibility)));
        }
    } = Visibility.Visible;

    public void Add(KeyChord chord)
    {
        if (!HasChord(chord)) Keys.Add(new ShortcutKey(chord));
    }

    public void RemoveChord(KeyChord chord)
    {
        if (Keys.FirstOrDefault(k => k.Chord == chord) is { } stale) Keys.Remove(stale);
    }

    public void ResetToDefault() => SetChords(DefaultChords);

    public void SetChords(IEnumerable<KeyChord> chords)
    {
        Keys.Clear();
        foreach (var c in chords) Keys.Add(new ShortcutKey(c));
    }

    /// <summary>Shows or hides the row for a search, and reports whether it survived. Matches on
    /// what the user can actually see - the display name, the hint, and the rendered chord text.
    /// Never the persisted form. Mirrors MouseRow.ApplyFilter.</summary>
    public bool ApplyFilter(string query)
    {
        var match = query.Length == 0
                    || Util.ContainsIgnoreCase(Name, query)
                    || Util.ContainsIgnoreCase(Description, query)
                    || Keys.Any(k => Util.ContainsIgnoreCase(k.Text, query));

        RowVisibility = match ? Visibility.Visible : Visibility.Collapsed;
        return match;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A category heading plus the rows under it.</summary>
public sealed class ShortcutGroup(string name, ObservableCollection<ShortcutRow> rows)
    : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public ObservableCollection<ShortcutRow> Rows { get; } = rows;

    /// <summary>Hidden when nothing under it survived the search, so the heading never sits alone
    /// above nothing.</summary>
    public Visibility GroupVisibility
    {
        get;
        private set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupVisibility)));
        }
    } = Visibility.Visible;

    /// <summary>Filters the rows and reports whether any survived. A match on the category name
    /// keeps the whole group, so "zoom" shows that section intact rather than shredding it.</summary>
    public bool ApplyFilter(string query)
    {
        var keepAll = query.Length == 0 || Util.ContainsIgnoreCase(Name, query);

        var anyVisible = false;
        foreach (var row in Rows) anyVisible |= row.ApplyFilter(keepAll ? string.Empty : query);

        GroupVisibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
        return anyVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Everything the catalog needs to say about a command beyond its keys and its icon. One flags enum
/// rather than a bool per trait: the traits compose (Next photo is both Burst and Repeat) and a new
/// one costs an enum member instead of a fourth mechanism.
///
/// Every member here is a behaviour. A trait that is really a fact about another file — "has a
/// description" was one, and lived here as Hinted — belongs to that file, not to this table.
/// Never persisted, so the bit values are free to move.
/// </summary>
[Flags]
internal enum CommandFlags
{
    None = 0,

    /// <summary>Keys are fixed: no edit button, and anything saved against it is ignored. Escape is
    /// the universal way out of the app and of the capture dialog; Delete keeps the key every file
    /// manager uses.</summary>
    Reserved = 1 << 0,

    /// <summary>Mark the key event handled so WinUI's own handling does not also run — Ctrl+C would
    /// trigger a browser-style copy, Enter would activate the focused button, Delete moves focus.</summary>
    Suppress = 1 << 1,

    /// <summary>Opens a navigation burst, which parks the HQ cache tier until a <c>Brake()</c>
    /// unwinds it, so key-up on whatever chord is bound to this has to brake.</summary>
    Burst = 1 << 2,

    /// <summary>Safe to fire again while the key is held. Windows repeats KeyDown for a held key,
    /// which is the point for navigation, zoom and pan, and wrong for anything that toggles.</summary>
    Repeat = 1 << 3
}

/// <summary>
/// The single source of truth for keyboard commands: which keys they ship with, how each one
/// behaves, and the defaults-plus-overrides resolution that <see cref="PhotoDisplayWindow"/> routes
/// on. Defaults live here in code and only the user's changes reach usersettings.json, so a command
/// added in a later release ships working keys to every existing user with no migration.
/// </summary>
internal static class ShortcutCatalog
{
    /// <summary>Pure data: no display strings and no UI objects, so the photo window can resolve
    /// its routing table at construction without touching the resource loader.</summary>
    private sealed record Def(CommandId Id, string Glyph, CommandFlags Flags, KeyChord[] Chords);

    // Defaults are typed rather than parsed from string literals, so a mistyped key name is a
    // compile error instead of a binding that silently never fires.
    // Repeat marks what is safe to fire again while the key is held: navigation, zoom and pan.
    // Everything else toggles or opens something, where a held key used to flicker it.
    private static readonly (string GroupKey, Def[] Rows)[] Table =
    [
        ("Navigation",
        [
            Row(CommandId.NextPhoto, "\uE970", Burst | Repeat, K(VirtualKey.Right)),
            Row(CommandId.PrevPhoto, "\uE96F", Burst | Repeat, K(VirtualKey.Left)),
            Row(CommandId.FirstPhoto, "\uE892", Repeat, K(VirtualKey.Home)),
            Row(CommandId.LastPhoto, "\uE893", Repeat, K(VirtualKey.End)),
            Row(CommandId.NextPage, "\uF586", Repeat, K(VirtualKey.Right, alt: true)),
            Row(CommandId.PrevPage, "\uF587", Repeat, K(VirtualKey.Left, alt: true))
        ]),

        ("ZoomPan",
        [
            Row(CommandId.ZoomIn, "\uE8A3", Repeat,
                [K(VirtualKey.Up), .. KeyChord.CtrlChordsFor('+'), K(VirtualKey.Add, ctrl: true)]),
            Row(CommandId.ZoomOut, "\uE71F", Repeat,
                [K(VirtualKey.Down), .. KeyChord.CtrlChordsFor('-'), K(VirtualKey.Subtract, ctrl: true)]),
            Row(CommandId.StepZoomIn, "\uE8A3", Repeat, K(VirtualKey.PageUp)),
            Row(CommandId.StepZoomOut, "\uE71F", Repeat, K(VirtualKey.PageDown)),
            Row(CommandId.ActualSize, "\uE799", None, K(VirtualKey.A)),
            Row(CommandId.FitToWindow, "\uE9A6", None, K(VirtualKey.F)),
            Row(CommandId.PanUp, "\uE7C2", Repeat, K(VirtualKey.Up, ctrl: true)),
            Row(CommandId.PanDown, "\uE7C2", Repeat, K(VirtualKey.Down, ctrl: true)),
            Row(CommandId.PanLeft, "\uE7C2", Repeat, K(VirtualKey.Left, ctrl: true)),
            Row(CommandId.PanRight, "\uE7C2", Repeat, K(VirtualKey.Right, ctrl: true))
        ]),

        ("Rotate",
        [
            Row(CommandId.RotateLeft, "\uE89E", None, K(VirtualKey.L)),
            Row(CommandId.RotateRight, "\uE89E", None, K(VirtualKey.R))
        ]),

        ("View",
        [
            Row(CommandId.FullScreen, "\uE740", None, K(VirtualKey.F11)),
            Row(CommandId.MaximizeRestore, "\uE922", Suppress, K(VirtualKey.Enter)),
            Row(CommandId.PhotoInfoPanel, "\uE946", None, K(VirtualKey.I)),
            Row(CommandId.CloseApp, "\uE8BB", Reserved, K(VirtualKey.Escape))
        ]),

        ("File",
        [
            Row(CommandId.CopyPhoto, "\uE8C8", Suppress, K(VirtualKey.C, ctrl: true)),
            Row(CommandId.DeletePhoto, "\uE74D", Reserved | Suppress, K(VirtualKey.Delete)),
            Row(CommandId.RenamePhoto, "\uE8AC", None, K(VirtualKey.F2)),
            Row(CommandId.PrintPhoto, "\uE749", None, K(VirtualKey.P)),
            Row(CommandId.SharePhoto, "\uE72D", None, K(VirtualKey.S)),
            Row(CommandId.ShowInExplorer, "\uE8DA", None, K(VirtualKey.W)),
            Row(CommandId.FileProperties, "\uF167", Suppress, K(VirtualKey.Enter, alt: true)),
            Row(CommandId.FileDetails, "\uF167", None, K(VirtualKey.D)),
            Row(CommandId.MoreActionsMenu, "\uE712", None, K(VirtualKey.M))
        ]),

        ("OpenWith",
        [
            Row(CommandId.OpenWithApp1, "\uE8A7", None,
                [K(VirtualKey.Number1, ctrl: true), K(VirtualKey.NumberPad1, ctrl: true)]),
            Row(CommandId.OpenWithApp2, "\uE8A7", None,
                [K(VirtualKey.Number2, ctrl: true), K(VirtualKey.NumberPad2, ctrl: true)]),
            Row(CommandId.OpenWithApp3, "\uE8A7", None,
                [K(VirtualKey.Number3, ctrl: true), K(VirtualKey.NumberPad3, ctrl: true)]),
            Row(CommandId.OpenWithApp4, "\uE8A7", None,
                [K(VirtualKey.Number4, ctrl: true), K(VirtualKey.NumberPad4, ctrl: true)]),
            Row(CommandId.OpenWithPanel, "\uE71D", None, K(VirtualKey.E))
        ])
    ];

    private static readonly Dictionary<CommandId, Def> ById =
        Table.SelectMany(g => g.Rows).ToDictionary(r => r.Id);

    public static bool Has(CommandId id, CommandFlags flag) => (ById[id].Flags & flag) != 0;

    /// <summary>Every command, in display order, already carrying whatever the user saved. Builds
    /// the display strings, so call it only from the settings window - never on the photo window's
    /// startup path.</summary>
    public static List<ShortcutGroup> BuildAll() =>
    [
        .. Table.Select(g => new ShortcutGroup(
            L.Get($"ShortcutGroup_{g.GroupKey}"),
            new ObservableCollection<ShortcutRow>(
                g.Rows.Select(r => new ShortcutRow(
                    r.Id, r.Glyph, r.Flags,
                    TryGetSaved(r.Id, out var saved) ? saved : r.Chords,
                    r.Chords)))))
    ];

    /// <summary>The command that already owns <paramref name="chord"/>, or null if it is free.</summary>
    public static ShortcutRow? FindOwner(IEnumerable<ShortcutRow> rows, KeyChord chord) =>
        rows.FirstOrDefault(r => r.HasChord(chord));

    /// <summary>Writes only what differs from the defaults, then persists. Chords are stored in
    /// their invariant text form: human-readable, hand-editable, and stable if VirtualKey names
    /// ever shift.</summary>
    public static async Task SaveBindingsAsync(IEnumerable<ShortcutRow> rows)
    {
        var overrides = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.IsModified))
            overrides[row.Id.ToString()] = row.Keys.Select(k => k.Chord.Format()).ToList();

        AppConfig.Settings.KeyBindings = overrides;
        await AppConfig.SaveAsync();
    }

    /// <summary>Defaults overlaid with the user's overrides, inverted into the lookup the key
    /// handler needs.</summary>
    public static Dictionary<KeyChord, CommandId> Resolve()
    {
        var routes = new Dictionary<KeyChord, CommandId>();

        foreach (var (id, def) in ById)
        {
            var chords = TryGetSaved(id, out var saved) ? saved : (IReadOnlyList<KeyChord>)def.Chords;
            foreach (var chord in chords) routes[chord] = id;
        }

        return routes;
    }

#if DEBUG
    /// <summary>
    /// Catches the table mistakes that have no other symptom until a user hits them: a command with
    /// no defaults, a chord claimed by two commands, an id nobody wrote a handler for, or a chord
    /// that does not survive the round trip through the persisted file. Runs once at startup, costs
    /// nothing in release, and needs no test framework.
    /// </summary>
    public static void AssertConsistent(ICollection<CommandId> handled)
    {
        var owner = new Dictionary<KeyChord, CommandId>();

        foreach (var id in Enum.GetValues<CommandId>())
        {
            Debug.Assert(ById.ContainsKey(id), $"{id} is missing from the catalog.");
            Debug.Assert(handled.Contains(id), $"{id} has no handler in PhotoDisplayWindow.");
        }

        foreach (var (id, def) in ById)
        {
            // Checks the resource itself, not a flag standing in for it. The flag could be set on a
            // command whose description was never written, which is the case this exists to catch.
            Debug.Assert(!def.Flags.HasFlag(Reserved) || L.GetOptional($"ShortcutDesc_{id}").Length > 0,
                $"{id} is reserved but has no ShortcutDesc_ resource explaining the missing edit button.");

            foreach (var chord in def.Chords)
            {
                Debug.Assert(KeyChord.TryParse(chord.Format(), out var back) && back == chord,
                    $"{id}'s chord \"{chord.Format()}\" does not survive a save and reload.");
                Debug.Assert(owner.TryAdd(chord, id),
                    $"{chord.Format()} is a default for both {id} and {owner.GetValueOrDefault(chord)}.");
            }
        }
    }
#endif

    /// <summary>
    /// The saved chords for a command, or false to use its defaults. This is the one place both
    /// readers funnel through, so <see cref="CommandFlags.Reserved"/> is enforced here rather than
    /// on each path — it only ever has to defend against a hand-edited file, since a reserved row
    /// has no edit button to write one in the first place. An entry that no longer parses is
    /// dropped rather than throwing.
    /// </summary>
    private static bool TryGetSaved(CommandId id, out List<KeyChord> chords)
    {
        chords = [];
        if (Has(id, Reserved)) return false;
        if (!AppConfig.Settings.KeyBindings.TryGetValue(id.ToString(), out var tokens) || tokens is null)
            return false;

        foreach (var token in tokens)
            if (KeyChord.TryParse(token, out var chord)) chords.Add(chord);

        return true;
    }

    /// <summary>Shorthand so the table above stays a table.</summary>
    private static KeyChord K(VirtualKey key, bool ctrl = false, bool alt = false, bool shift = false) =>
        new(key, ctrl, alt, shift);

    private static Def Row(CommandId id, string glyph, CommandFlags flags, params KeyChord[] chords) =>
        new(id, glyph, flags, chords);
}
