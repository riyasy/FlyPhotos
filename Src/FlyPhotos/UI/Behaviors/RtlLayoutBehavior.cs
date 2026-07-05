using Windows.Foundation;
using FlyPhotos.Infra.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlyPhotos.UI.Behaviors;

/// <summary>Applies RTL layout mirroring for languages like Arabic.</summary>
internal static class RtlLayoutBehavior
{
    /// <summary>Sets FlowDirection on <paramref name="root"/>; returns whether RTL was applied.</summary>
    public static bool ApplyFlowDirection(FrameworkElement root, string language)
    {
        var isRtl = Localizer.IsRtl(language);
        root.FlowDirection = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return isRtl;
    }

    /// <summary>Horizontally flips directional glyph icons, which FlowDirection alone doesn't mirror.</summary>
    public static void MirrorIcon(params FrameworkElement[] icons)
    {
        foreach (var icon in icons)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
    }
}
