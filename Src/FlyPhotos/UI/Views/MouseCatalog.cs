#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;

namespace FlyPhotos.UI.Views;

/// <summary>
/// One row of the Shortcuts tab's Mouse section: either a picker, whose options are backed by a
/// setting, or a fixed behaviour the user can see but not change.
///
/// Both shapes are one type so the page renders them from one template and searches them the same
/// way it searches command rows - by asking the data. The hand-written cards this replaces could
/// only be searched by walking the control tree, which silently missed any card that was nested,
/// carried a non-string header, or held a control the matcher had not been taught about.
/// </summary>
public sealed class MouseRow : INotifyPropertyChanged
{
    /// <summary>Writes the chosen option to <see cref="AppConfig"/>. Null on a fixed row.</summary>
    private readonly Action<int>? _apply;

    private Visibility _rowVisibility = Visibility.Visible;

    public string Header { get; }
    public string Description { get; }

    /// <summary>What the gesture does, on a row with nothing to choose. Empty on a picker.</summary>
    public string FixedAction { get; }

    /// <summary>Declared as the concrete type: handing an interface-typed collection to ItemsSource
    /// crashes with E_INVALIDARG under AOT.</summary>
    public List<string> Options { get; }

    public int SelectedIndex { get; private set; }

    public bool IsPicker => _apply != null;

    // Visibility rather than bool so the DataTemplate needs no converter, matching ShortcutRow.
    public Visibility PickerVisibility => IsPicker ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FixedVisibility => IsPicker ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RowVisibility
    {
        get => _rowVisibility;
        private set
        {
            if (_rowVisibility == value) return;
            _rowVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowVisibility)));
        }
    }

    private MouseRow(string headerKey, bool hasDescription, string? fixedActionKey,
                     List<string> options, int selectedIndex, Action<int>? apply)
    {
        Header = L.Get($"{headerKey}/Header");
        Description = hasDescription ? L.Get($"{headerKey}/Description") : string.Empty;
        FixedAction = fixedActionKey is null ? string.Empty : L.Get($"{fixedActionKey}/Text");
        Options = options;
        SelectedIndex = selectedIndex;
        _apply = apply;
    }

    /// <summary>
    /// Applies a picker selection. Ignores anything that is not a real change: a ComboBox raises
    /// SelectionChanged while the template binds its initial value, and saving there would rewrite
    /// usersettings.json every time the Settings window opens.
    /// </summary>
    public async Task SelectAsync(int index)
    {
        if (_apply is null || index < 0 || index == SelectedIndex) return;
        SelectedIndex = index;
        _apply(index);
        await AppConfig.SaveAsync();
    }

    /// <summary>Shows or hides the row for a search, and reports whether it survived. A picker
    /// matches on every option it offers, so "zoom" finds the wheel setting even while it is set
    /// to Navigate.</summary>
    public bool ApplyFilter(string query)
    {
        var match = query.Length == 0
                    || Util.ContainsIgnoreCase(Header, query)
                    || Util.ContainsIgnoreCase(Description, query)
                    || Util.ContainsIgnoreCase(FixedAction, query)
                    || Options.Any(o => Util.ContainsIgnoreCase(o, query));

        RowVisibility = match ? Visibility.Visible : Visibility.Collapsed;
        return match;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A gesture the user chooses the behaviour of. Every picker here lists its options in
    /// enum order, so the selected index is the enum value.</summary>
    internal static MouseRow Picker(string headerKey, bool hasDescription, string[] optionKeys,
                                    int selected, Action<int> apply) =>
        new(headerKey, hasDescription, null,
            optionKeys.Select(k => L.Get($"{k}/Content")).ToList(), selected, apply);

    /// <summary>A gesture that is hardcoded, listed so the user can see what the mouse does without
    /// being able to break it.</summary>
    internal static MouseRow Fixed(string headerKey, string actionKey, bool hasDescription = false) =>
        new(headerKey, hasDescription, actionKey, [], -1, null);
}

/// <summary>
/// The Mouse section of the Shortcuts tab, in display order. Resource keys are the ones the cards
/// already used as x:Uid, so moving to a template cost no translation work in any locale.
/// </summary>
internal static class MouseCatalog
{
    public static List<MouseRow> BuildAll() =>
    [
        MouseRow.Picker("SettingsCardMouseWheelBehaviour", true,
            ["ComboMouseWheelItemZoom", "ComboMouseWheelItemNav"],
            (int)AppConfig.Settings.DefaultMouseWheelBehavior,
            i => AppConfig.Settings.DefaultMouseWheelBehavior = (DefaultMouseWheelBehavior)i),

        // The fixed wheel behaviours sit next to the wheel setting they qualify.
        MouseRow.Fixed("SettingsCardCtrlMouseWheel", "TextCtrlMouseWheelAction"),
        MouseRow.Fixed("SettingsCardAltMouseWheel", "TextAltMouseWheelAction"),
        MouseRow.Fixed("SettingsCardTiltWheel", "TextTiltWheelAction"),

        MouseRow.Picker("SettingsCardMiddleClick", false,
            ["ComboMiddleClickItemFullScreen", "ComboMiddleClickItemMaximize", "ComboMiddleClickItemNothing"],
            (int)AppConfig.Settings.MiddleClickBehavior,
            i => AppConfig.Settings.MiddleClickBehavior = (MiddleClickBehavior)i),

        MouseRow.Fixed("SettingsCardLeftClickDrag", "TextLeftClickDragAction", hasDescription: true),
        MouseRow.Fixed("SettingsCardCtrlDragToMoveWindow", "TextCtrlDragAction"),
        MouseRow.Fixed("SettingsCardDoubleClick", "TextDoubleClickAction"),
        MouseRow.Fixed("SettingsCardRightClick", "TextRightClickAction"),

        MouseRow.Picker("SettingsCardRightClickHold", false,
            ["ComboRightClickHoldItemZoomIn", "ComboRightClickHoldItemNothing"],
            (int)AppConfig.Settings.RightClickHoldBehavior,
            i => AppConfig.Settings.RightClickHoldBehavior = (RightClickHoldBehavior)i),

        MouseRow.Picker("SettingsCardMouseFwdBackBehaviour", true,
            ["ComboMouseFwdBackItemNav", "ComboMouseFwdBackItemStepZoom"],
            (int)AppConfig.Settings.MouseFwdBackBehavior,
            i => AppConfig.Settings.MouseFwdBackBehavior = (MouseFwdBackBehavior)i)
    ];
}
