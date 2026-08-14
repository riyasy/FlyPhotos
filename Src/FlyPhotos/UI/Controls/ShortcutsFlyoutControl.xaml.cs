#nullable enable
using System;
using System.Collections.Generic;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Services.ExternalAppListing;
using FlyPhotos.UI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FlyPhotos.UI.Controls;

/// <summary>
/// A Flyout of configured external-app shortcuts, anchored to a caller-supplied element. The host
/// resolves the apps (shared with the More menu / Ctrl+1..4 cache) and passes them in; the control
/// only lays out the buttons and raises <see cref="AppLaunchRequested"/> on click.
/// </summary>
public sealed partial class ShortcutsFlyoutControl : UserControl
{
    private Flyout? _flyout;

    /// <summary>Raised with the app whose button was clicked. The host performs the launch.</summary>
    public event Action<InstalledApp>? AppLaunchRequested;

    public ShortcutsFlyoutControl()
    {
        InitializeComponent();
        RtlLayoutBehavior.ApplyFlowDirection(this, AppConfig.Settings.Language);
    }

    public void Show(FrameworkElement anchor, IReadOnlyList<InstalledApp?> apps)
    {
        ShortcutsPanel.Children.Clear();
        foreach (var app in apps)
        {
            if (app == null) continue;

            var button = new Button { Width = 60, Height = 50, Tag = app, Content = AppIconFactory.Build(app, 32) };
            ToolTipService.SetToolTip(button, app.DisplayName);
            button.Click += ShortcutButton_OnClick;
            ShortcutsPanel.Children.Add(button);
        }

        if (ShortcutsPanel.Children.Count == 0)
            ShortcutsPanel.Children.Add(new TextBlock { Text = L.Get("NoShortcutsCreated/Message") });

        _flyout ??= new Flyout { Content = this, Placement = FlyoutPlacementMode.Top };
        _flyout.ShowAt(anchor);
    }

    private void ShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstalledApp app })
            AppLaunchRequested?.Invoke(app);
        _flyout?.Hide();
    }
}
