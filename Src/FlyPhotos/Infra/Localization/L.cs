using System;
using Microsoft.Windows.ApplicationModel.Resources;

namespace FlyPhotos.Infra.Localization;

public static class L
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
        => Loader.GetString(key);

    /// <summary>
    /// A string that may or may not exist, empty when it does not. Lets a caller ask for an optional
    /// resource — a hint under a row, say — instead of maintaining a second list of which ones have
    /// one, which is a list that only ever drifts.
    ///
    /// GetString is documented to return "" for a missing key rather than throw. The catch is here
    /// so that stays a guarantee we make rather than a behaviour we depend on: the callers use this
    /// while building a settings page, where a throw would cost the whole page.
    /// </summary>
    public static string GetOptional(string key)
    {
        try
        {
            return Loader.GetString(key);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}