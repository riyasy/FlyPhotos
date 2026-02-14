#nullable enable

using System;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace FlyPhotos.Utils
{
    public sealed class LicenseService
    {
        private readonly StoreContext? _storeContext;
        private StoreAppLicense? _license;

        public static LicenseService Instance { get; } = new();

        private LicenseService()
        {
            try
            {
                _storeContext = StoreContext.GetDefault();
            }
            catch
            {
                _storeContext = null;
            }
        }

        public bool IsTrial => _license != null && _license.IsTrial;

        public bool IsActive => !PathResolver.IsPackagedApp || _license == null || _license.IsActive;

        public DateTimeOffset ExpirationDate =>
            _license?.ExpirationDate ?? DateTimeOffset.MaxValue;

        public async Task InitializeAsync()
        {
            if (_storeContext == null)
                return;

            try
            {
                _license = await _storeContext.GetAppLicenseAsync();
            }
            catch
            {
                _license = null;
            }
        }
    }
}