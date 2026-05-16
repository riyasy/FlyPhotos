#nullable enable
using Windows.Foundation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Infra.Utils;

internal static class DpiExtensions
{
    // ── CanvasControl overloads (unchanged, still used by D2dCanvasThumbNail) ──────────────────

    public static Size GetSize(this CanvasControl canvasControl)
    {
        // Calculate the physical pixel size based on DPI
        double width = canvasControl.ActualWidth * canvasControl.Dpi / 96.0;
        double height = canvasControl.ActualHeight * canvasControl.Dpi / 96.0;

        return new Size(width, height);
    }

    public static Point AdjustForDpi(this Point logicalPoint, CanvasControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return new Point(logicalPoint.X * scale, logicalPoint.Y * scale);
    }

    public static double AdjustForDpi(this double val, CanvasControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return val * scale;
    }

    public static Size AdjustForDpi(this Size logicalSize, CanvasControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return new Size(logicalSize.Width * scale, logicalSize.Height * scale);
    }

    public static Size AdjustForDpi(this Windows.Foundation.Size foundationSize, CanvasControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return new Size(foundationSize.Width * scale, foundationSize.Height * scale);
    }

    // ── CanvasAnimatedControl overloads (new, used by the migrated D2dCanvas) ─────────────────

    public static Size GetSize(this CanvasAnimatedControl canvasControl)
    {
        // CanvasAnimatedControl.Size is already in DIPs; multiply by DPI scale for physical pixels.
        double scale  = canvasControl.Dpi / 96.0;
        double width  = canvasControl.Size.Width  * scale;
        double height = canvasControl.Size.Height * scale;
        return new Size(width, height);
    }

    public static Point AdjustForDpi(this Point logicalPoint, CanvasAnimatedControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return new Point(logicalPoint.X * scale, logicalPoint.Y * scale);
    }

    public static double AdjustForDpi(this double val, CanvasAnimatedControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return val * scale;
    }

    public static Size AdjustForDpi(this Size logicalSize, CanvasAnimatedControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return new Size(logicalSize.Width * scale, logicalSize.Height * scale);
    }

    public static Size AdjustForDpi(this Windows.Foundation.Size foundationSize, CanvasAnimatedControl canvasControl)
    {
        double scale = canvasControl.Dpi / 96.0;
        return new Size(foundationSize.Width * scale, foundationSize.Height * scale);
    }
}