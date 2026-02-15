using Microsoft.Windows.ApplicationModel.Resources;

namespace FlyPhotos.Infra.Localization;

public static class L
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
        => Loader.GetString(key);
}