using Microsoft.Windows.ApplicationModel.Resources;

namespace FlyPhotos.Utils;

public static class L
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
        => Loader.GetString(key);
}