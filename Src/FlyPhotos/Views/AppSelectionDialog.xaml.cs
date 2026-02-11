#nullable enable

using FlyPhotos.ExternalApps;
using FlyPhotos.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace FlyPhotos.Views;

/// <summary>
/// Dialog for selecting an application from the list of installed apps.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AppSelectionDialog : ContentDialog
{
    /// <summary>
    /// Logger instance for logging errors.
    /// </summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Gets the collection of applications to display.
    /// </summary>
    public ObservableCollection<InstalledApp> Apps { get; } = [];
    private readonly List<InstalledApp> _allApps = [];

    /// <summary>
    /// Gets the application selected by the user.
    /// </summary>
    public InstalledApp? SelectedApp { get; private set; }

    // Temporary pending selection from the list or browse picker
    private InstalledApp? _pendingSelection;

    // Parent window reference for file picker initialization
    private readonly Window _parentWindow;

    public AppSelectionDialog(Window parentWindow)
    {
        _parentWindow = parentWindow;
        InitializeComponent();
        AppListView.ItemsSource = Apps;
        Loaded += AppSelectionDialog_Loaded;
        PrimaryButtonClick += AppSelectionDialog_PrimaryButtonClick;
        SecondaryButtonClick += AppSelectionDialog_SecondaryButtonClick;
    }

    private async void AppSelectionDialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadingPanel.Visibility = Visibility.Visible;
            AppListView.Visibility = Visibility.Collapsed;

            var win32Provider = new Win32AppProvider();
            var storeProvider = new StoreAppProvider();

            var tasks = new List<Task<IEnumerable<InstalledApp>>>
            {
                win32Provider.GetAppsAsync(),
                storeProvider.GetAppsAsync()
            };

            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
                _allApps.AddRange(result);

            var iconTasks = new List<Task>();
            foreach (var app in _allApps)
                iconTasks.Add(app.DecodeIconAsync());

            await Task.WhenAll(iconTasks);

            _allApps.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            ApplyFilter(string.Empty);

            LoadingPanel.Visibility = Visibility.Collapsed;
            AppListView.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AppSelectionDialog_Loaded Error");
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------
    // UI Interaction
    // ---------------------------------------------------------
    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_parentWindow));
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            
            var icon = await Util.ExtractIconFromExe(file.Path);

            _pendingSelection = new Win32App
            {
                Type = AppType.Win32,
                DisplayName = file.DisplayName,
                ExePath = file.Path,
                Icon = icon
            };

            SelectedApp = _pendingSelection;
            Hide();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "AppSelectionDialog - BrowseBtn_Click Error");
        }
    }

    private void AppListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppListView.SelectedItem is not InstalledApp app)
        {
            _pendingSelection = null;
            IsPrimaryButtonEnabled = false;
            return;
        }

        // store pending selection and enable Select button
        _pendingSelection = app;
        IsPrimaryButtonEnabled = true;
    }

    private void AppSelectionDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_pendingSelection != null)
            SelectedApp = _pendingSelection;
        Hide();
    }

    private void AppSelectionDialog_SecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _pendingSelection = null;
        SelectedApp = null;
        Hide();
    }

    private void ApplyFilter(string filter)
    {
        Apps.Clear();
        if (string.IsNullOrWhiteSpace(filter))
        {
            foreach (var a in _allApps) 
                Apps.Add(a);
            return;
        }

        var q = filter.Trim();
        foreach (var a in _allApps)
        {
            if (a.DisplayName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                Apps.Add(a);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = sender as TextBox;
        ApplyFilter(tb?.Text ?? string.Empty);
    }
}
