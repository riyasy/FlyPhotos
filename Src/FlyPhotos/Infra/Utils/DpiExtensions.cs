#nullable enable
using Windows.Foundation;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Infra.Utils;

internal static class DpiExtensions
{
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
}