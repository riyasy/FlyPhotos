using FlyPhotos.Core;
using Microsoft.Windows.Globalization;
using NLog;
using System;
using System.Linq;

namespace FlyPhotos.Infra.Localization;

internal class Localizer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string DefaultLang = "en-US";

    public static string ApplyLanguage(string currentLang)
    {
        try
        {
            // 1. If a valid, non-default setting exists, use it.
            if (!string.IsNullOrEmpty(currentLang) && currentLang != "Default" && Constants.SupportedLanguages.Contains(currentLang))
            {
                ApplicationLanguages.PrimaryLanguageOverride = currentLang;
                return currentLang;
            }
            // 2. Get the language the Framework decided to use (Result of OS preferences + App Resources)
            // ApplicationLanguages.Languages[0] is the "winner" determined by Windows.
            var resolved = ApplicationLanguages.Languages[0];
            // 3. Match the resolved language to your Supported list.
            // FIX: Compare the language code (part before '-') against language code to avoid "fil" matching "fi".
            var resolvedIso = resolved.Split('-')[0];
            var matched = Constants.SupportedLanguages.FirstOrDefault(l => l.Split('-')[0] == resolvedIso);
            // 4. Fallback to en-US if no match found (worst case), otherwise use the match
            var finalLanguage = matched ?? DefaultLang;
            // 5. Apply and Save
            ApplicationLanguages.PrimaryLanguageOverride = finalLanguage;
            return finalLanguage;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error applying language settings. Defaulting to en-US");
            ApplicationLanguages.PrimaryLanguageOverride = DefaultLang;
            return DefaultLang;
        }
    }
}