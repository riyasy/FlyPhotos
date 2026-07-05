using Windows.Foundation;
using FlyPhotos.Infra.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>Forces LTR on windows that must never mirror (e.g. a canvas rendering photo pixels).</summary>
    public static void ForceLeftToRight(FrameworkElement root) => root.FlowDirection = FlowDirection.LeftToRight;

    /// <summary>
    /// Scopes RTL mirroring to a single flyout's items, without affecting the owning window.
    /// Sets FlowDirection directly on each item rather than via MenuFlyoutPresenterStyle, since the
    /// presenter is recreated fresh on every popup open and doesn't reliably pick up a style-set value.
    /// </summary>
    public static bool ApplyFlowDirection(MenuFlyout menu, string language)
    {
        var isRtl = Localizer.IsRtl(language);
        if (isRtl)
        {
            foreach (var item in menu.Items)
                item.FlowDirection = FlowDirection.RightToLeft;
        }
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
