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

namespace FlyPhotos.UI.Views;

/// <summary>
/// One row of the Mouse tab: either a picker, whose options are backed by a setting, or a fixed
/// behaviour the user can see but not change.
///
/// Both shapes are one type so the page renders them from one template, which is what keeps a new
/// gesture to a single line in <see cref="MouseCatalog"/> with no XAML at all.
/// </summary>
public sealed class MouseRow : INotifyPropertyChanged
{
    /// <summary>Writes the chosen option to <see cref="AppConfig"/>. Null on a fixed row.</summary>
    private readonly Action<int>? _apply;

    private string _fixedAction;

    public string Header { get; }
    public string Description { get; }

    /// <summary>What the gesture does, on a row with nothing to choose. Empty on a picker. Settable
    /// because one fixed row only reports what another row's picker decides.</summary>
    public string FixedAction
    {
        get => _fixedAction;
        private set
        {
            if (_fixedAction == value) return;
            _fixedAction = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FixedAction)));
        }
    }

    /// <summary>Declared as the concrete type: handing an interface-typed collection to ItemsSource
    /// crashes with E_INVALIDARG under AOT.</summary>
    public List<string> Options { get; }

    public int SelectedIndex { get; private set; }

    public bool IsPicker => _apply != null;

    // Visibility rather than bool so the DataTemplate needs no converter, matching ShortcutRow.
    public Visibility PickerVisibility => IsPicker ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FixedVisibility => IsPicker ? Visibility.Collapsed : Visibility.Visible;

    private MouseRow(string headerKey, string? fixedActionKey,
                     List<string> options, int selectedIndex, Action<int>? apply)
    {
        Header = L.Get($"{headerKey}/Header");
        Description = L.GetOptional($"{headerKey}/Description");
        _fixedAction = fixedActionKey is null ? string.Empty : L.Get($"{fixedActionKey}/Text");
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

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A gesture the user chooses the behaviour of. Every picker here lists its options in
    /// enum order, so the selected index is the enum value.</summary>
    internal static MouseRow Picker(string headerKey, string[] optionKeys,
                                    int selected, Action<int> apply) =>
        new(headerKey, null,
            optionKeys.Select(k => L.Get($"{k}/Content")).ToList(), selected, apply);

    /// <summary>A gesture that is hardcoded, listed so the user can see what the mouse does without
    /// being able to break it.</summary>
    internal static MouseRow Fixed(string headerKey, string actionKey) =>
        new(headerKey, actionKey, [], -1, null);

    /// <summary>Retargets a fixed row whose behaviour is decided by another row's picker.</summary>
    internal void SetFixedAction(string actionKey) => FixedAction = L.Get($"{actionKey}/Text");
}

/// <summary>
/// The Mouse tab, in display order. Resource keys are the ones the cards already used as x:Uid, so
/// moving to a template cost no translation work in any locale.
/// </summary>
internal static class MouseCatalog
{
    /// <summary>Double-click outside the photo maximizes only while the single-click-outside
    /// gesture is enabled - the window code gates both on the same setting - so the row reports
    /// that setting rather than owning one of its own.</summary>
    private static string DoubleClickOutsideActionKey(bool clickOutsideEnabled) =>
        clickOutsideEnabled ? "TextDoubleClickOutsideActionMaximize" : "TextDoubleClickOutsideActionNothing";

    public static List<MouseRow> BuildAll()
    {
        var doubleClickOutside = MouseRow.Fixed("SettingsCardDoubleClickOutside",
            DoubleClickOutsideActionKey(AppConfig.Settings.ClickOutsideImageToRestoreWindow));

        return
        [
            MouseRow.Picker("SettingsCardMouseWheelBehaviour",
                ["ComboMouseWheelItemZoom", "ComboMouseWheelItemNav"],
                (int)AppConfig.Settings.DefaultMouseWheelBehavior,
                i => AppConfig.Settings.DefaultMouseWheelBehavior = (DefaultMouseWheelBehavior)i),

            // The fixed wheel behaviours sit next to the wheel setting they qualify.
            MouseRow.Fixed("SettingsCardCtrlMouseWheel", "TextCtrlMouseWheelAction"),
            MouseRow.Fixed("SettingsCardAltMouseWheel", "TextAltMouseWheelAction"),
            MouseRow.Fixed("SettingsCardTiltWheel", "TextTiltWheelAction"),

            MouseRow.Picker("SettingsCardMiddleClick",
                ["ComboMiddleClickItemFullScreen", "ComboMiddleClickItemMaximize", "ComboMiddleClickItemNothing"],
                (int)AppConfig.Settings.MiddleClickBehavior,
                i => AppConfig.Settings.MiddleClickBehavior = (MiddleClickBehavior)i),

            MouseRow.Fixed("SettingsCardLeftClickDrag", "TextLeftClickDragAction"),
            MouseRow.Fixed("SettingsCardCtrlDragToMoveWindow", "TextCtrlDragAction"),

            // Backed by a bool, so index 0 is "Restore window" and index 1 is "Nothing".
            MouseRow.Picker("SettingsCardClickOutsideImageToRestoreWindow",
                ["ComboClickOutsideItemRestore", "ComboClickOutsideItemNothing"],
                AppConfig.Settings.ClickOutsideImageToRestoreWindow ? 0 : 1,
                i =>
                {
                    AppConfig.Settings.ClickOutsideImageToRestoreWindow = i == 0;
                    doubleClickOutside.SetFixedAction(DoubleClickOutsideActionKey(i == 0));
                }),

            MouseRow.Fixed("SettingsCardDoubleClick", "TextDoubleClickAction"),
            doubleClickOutside,
            MouseRow.Fixed("SettingsCardRightClick", "TextRightClickAction"),

            MouseRow.Picker("SettingsCardRightClickHold",
                ["ComboRightClickHoldItemZoomIn", "ComboRightClickHoldItemNothing"],
                (int)AppConfig.Settings.RightClickHoldBehavior,
                i => AppConfig.Settings.RightClickHoldBehavior = (RightClickHoldBehavior)i),

            MouseRow.Picker("SettingsCardMouseFwdBackBehaviour",
                ["ComboMouseFwdBackItemNav", "ComboMouseFwdBackItemStepZoom"],
                (int)AppConfig.Settings.MouseFwdBackBehavior,
                i => AppConfig.Settings.MouseFwdBackBehavior = (MouseFwdBackBehavior)i)
        ];
    }
}
