#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Services;

namespace FlyPhotos.UI.Actions;

/// <summary>
/// Coordinates the startup trial-expiry check. The dialog can only be shown once two independent
/// startup steps have both completed — the license state has been refreshed AND the first photo has
/// loaded — so whichever finishes last triggers the check. Both notifications arrive on the UI
/// thread, so the two flags need no locking. The host supplies the XamlRoot and the "close window"
/// follow-up as delegates.
/// </summary>
internal class ActionCheckLicense(Func<XamlRoot?> xamlRootProvider, Func<Task> onTrialExpired)
{
    private bool _stateRefreshed;
    private bool _photoReady;
    private bool _checked;

    /// <summary>Signal that the first photo has loaded. Must be called on the UI thread.</summary>
    public Task PhotoLoadedAsync()
    {
        _photoReady = true;
        return TryCheckAsync();
    }

    /// <summary>
    /// Signal that the window has loaded: refresh the license state (a Store API call, so done once)
    /// then check. Must be called on the UI thread. Guards against WinUI re-raising Loaded — the
    /// refresh runs at most once.
    /// </summary>
    public async Task WindowLoadedAsync()
    {
        if (_stateRefreshed) return;
        await LicenseService.Instance.RefreshLicenseStateAsync();
        _stateRefreshed = true;
        await TryCheckAsync();
    }

    // Both prerequisites gate the check; whichever signal arrives last lets it through.
    private Task TryCheckAsync() =>
        _stateRefreshed && _photoReady ? CheckAsync() : Task.CompletedTask;

    private async Task CheckAsync()
    {
        if (_checked) return; // show at most once, however the two signals interleave or re-fire
        _checked = true;

        if (LicenseService.Instance.State != LicenseState.TrialExpired) return;

        var xamlRoot = xamlRootProvider();
        if (xamlRoot == null) return; // window may be closing

        var dialog = new ContentDialog
        {
            Title = L.Get("TrialExpiredMessage/Title"),
            Content = L.Get("TrialExpiredMessage/Content"),
            CloseButtonText = L.Get("TrialExpiredMessage/CloseButton"),
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
        await onTrialExpired();
    }
}
