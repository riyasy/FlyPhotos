#nullable enable
using System;
using System.Threading.Tasks;
using Windows.Services.Store;
using NLog;

namespace FlyPhotos.Services;

public enum LicenseState
{
    Full,
    TrialActive,
    TrialExpired
}

public sealed class LicenseService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    LicenseState _licenseState = LicenseState.Full;

    public static LicenseService Instance { get; } = new();

    private LicenseService() { }

    /// <summary>
    /// Returns the cached license state. 
    /// </summary>
    public LicenseState State => _licenseState;

    public async Task RefreshLicenseStateAsync()
    {
        _licenseState = await FetchLicenseStateInternalAsync();
    }

    private async Task<LicenseState> FetchLicenseStateInternalAsync()
    {
        if (!PathResolver.IsPackagedApp)
            return LicenseState.Full;
        try
        {
            var context = StoreContext.GetDefault();
            var license = await context.GetAppLicenseAsync();

            if (license.IsActive)
                return license.IsTrial ? LicenseState.TrialActive : LicenseState.Full;
            else
                return LicenseState.TrialExpired;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to get license info, assuming full license. Exception: {0}", ex);
            return LicenseState.Full;
        }
    }
}