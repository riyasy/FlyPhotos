using System;
using System.Runtime.InteropServices;
using NLog;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.MFT;
using static TerraFX.Interop.Windows.Windows;

namespace FlyPhotos.Readers
{
    /// <summary>
    /// A utility class to check for the availability of the HEVC video decoder
    /// using the Windows Media Foundation (MF) framework.
    /// The result is cached after the first check.
    /// </summary>
    public static class HeifCodecResolver
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Use Lazy<T> to ensure the expensive codec check is performed only once.
        // The factory function will be executed on the first access to the .Value property.
        private static readonly Lazy<bool> IsHevcDecoderAvailableLazy = new(PerformHevcDecoderCheck);

        /// <summary>
        /// Gets a value indicating whether the HEVC (H.265) video decoder is available on the system.
        /// The check is performed only once; subsequent calls return a cached result.
        /// </summary>
        public static bool IsHevcDecoderAvailable => IsHevcDecoderAvailableLazy.Value;

        private static bool PerformHevcDecoderCheck()
        {
            try
            {
                // The mfplat.dll library is required for Media Foundation.
                // This is not present on "N" editions of Windows unless the
                // "Media Feature Pack" has been installed by the user.
                if (NativeLibrary.TryLoad("mfplat.dll", typeof(HeifCodecResolver).Assembly, DllImportSearchPath.System32, out _)) 
                    return QueryForHevcDecoder();
                Logger.Warn("mfplat.dll could not be loaded. HEVC codec is considered unavailable. This may be a Windows 'N' edition without the Media Feature Pack.");
                return false;

                // Call the core method to query for the codec.
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "An unexpected error occurred while checking for HEVC decoder availability.");
                return false;
            }
        }

        /// <summary>
        /// The core method to query Media Foundation for the HEVC decoder.
        /// </summary>
        private static unsafe bool QueryForHevcDecoder()
        {
            MFT_REGISTER_TYPE_INFO inputType;
            inputType.guidMajorType = MFMediaType_Video;
            inputType.guidSubtype = MFVideoFormat.MFVideoFormat_HEVC; // Hardcoded to HEVC

            IMFActivate** ppActivates = null;
            uint cActivates = 0;

            try
            {
                // MFTEnumEx finds Media Foundation Transforms (MFTs) that match the criteria.
                // We are looking for a synchronous video decoder for the HEVC format.
                HRESULT hr = MFTEnumEx(
                    MFT_CATEGORY_VIDEO_DECODER,
                    (uint)_MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT,
                    &inputType, // Decoders take the compressed format as input.
                    null,       // Output type is not specified for a general query.
                    &ppActivates,
                    &cActivates);

                bool isFound = hr.SUCCEEDED && cActivates > 0;
                // Logger.Info($"HEVC decoder check result: {(isFound ? "Available" : "Not Available")}. (HRESULT: {hr}, Count: {cActivates})");
                return isFound;
            }
            finally
            {
                // Clean up the COM objects returned by MFTEnumEx to prevent memory leaks.
                if (ppActivates != null)
                {
                    for (uint i = 0; i < cActivates; ++i)
                    {
                        if (ppActivates[i] != null)
                        {
                            ppActivates[i]->Release();
                        }
                    }
                    CoTaskMemFree(ppActivates);
                }
            }
        }
    }
}