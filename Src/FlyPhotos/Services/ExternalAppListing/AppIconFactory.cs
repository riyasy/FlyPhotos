#nullable enable
using Microsoft.UI.Xaml.Controls;

namespace FlyPhotos.Services.ExternalAppListing;

internal static class AppIconFactory
{
    private static readonly string DefaultAppIconGlyph = ((char)0xED35).ToString();

    public static IconElement Build(InstalledApp app, double? size = null)
    {
        if (app.Icon != null)
        {
            var icon = new ImageIcon { Source = app.Icon };
            if (!size.HasValue)
                return icon;
            icon.Width = size.Value;
            icon.Height = size.Value;
            return icon;
        }
        var fallback = new FontIcon { Glyph = DefaultAppIconGlyph, FontFamily = App.FluentIconFont };
        if (size.HasValue) fallback.FontSize = size.Value;
        return fallback;
    }
}
