#nullable enable
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FlyPhotos.UI.Controls;

/// <summary>
/// Lays children out left to right, starting a new line when the next one no longer fits. WinUI
/// ships no wrapping panel and the toolkit's lives in a package this project does not reference,
/// so it is these two overrides rather than a dependency.
///
/// Children keep their own desired size, which is the point: a row of chips of differing widths is
/// what this exists for, and a uniform-grid layout would size every one of them to the widest.
/// </summary>
internal sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, widest = 0, stackedHeight = 0;

        foreach (var child in Children)
        {
            // Unconstrained height: how many lines this needs is the answer, not an input.
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + HorizontalSpacing + size.Width > availableSize.Width)
            {
                widest = Math.Max(widest, lineWidth);
                stackedHeight += lineHeight + VerticalSpacing;
                lineWidth = lineHeight = 0;
            }

            lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return new Size(Math.Max(widest, lineWidth), stackedHeight + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            if (x > 0 && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            if (x > 0) x += HorizontalSpacing;
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
