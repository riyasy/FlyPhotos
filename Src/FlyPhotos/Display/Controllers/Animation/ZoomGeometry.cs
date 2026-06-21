using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers.Animation;

/// <summary>
/// Stateless geometry for zoom / fit: fit-scale, the anchor-preserving pan primitive, sticky-zoom snapping,
/// and step-zoom stops. Every member is a pure function of its arguments (canvas, image, rotation, scale)
/// with no view state, so <see cref="CanvasViewManager"/> and the animation paths can share one definition
/// of each rule instead of re-inlining it.
/// </summary>
internal static class ZoomGeometry
{
    // The scales the wheel "clicks" onto when StickyZoomLevels is enabled.
    private static readonly float[] ZoomSnapPoints = [0.5f, 1.0f, 2.0f, 5.0f, 10.0f];

    /// <summary>
    /// Returns the pan (image-centre) position that keeps the screen point <paramref name="anchor"/> over the
    /// same image pixel when the scale changes from <paramref name="oldScale"/> to <paramref name="newScale"/>.
    /// The core anchored-zoom primitive.
    /// </summary>
    public static Point AnchorPreservingPan(Point anchor, Point currentPos, float oldScale, float newScale)
    {
        var k = newScale / oldScale;
        return new Point(anchor.X - k * (anchor.X - currentPos.X),
                         anchor.Y - k * (anchor.Y - currentPos.Y));
    }

    /// <summary>
    /// Scale factor that fits <paramref name="imageSize"/> fully inside <paramref name="canvasSize"/>. Accounts
    /// for the current <paramref name="imageRotation"/> (a 90°/270° image swaps effective width/height) and the
    /// user's fit-padding (<c>ImageFitPercentage</c>). Returns the smaller of the horizontal/vertical fit so the
    /// whole image stays visible on both axes.
    /// </summary>
    public static float CalculateScreenFitScale(Size canvasSize, Size imageSize, int imageRotation)
    {
        // A vertical orientation (90°/270°) swaps the image's effective width and height.
        var isVertical = (imageRotation % 180) != 0;
        var effectiveWidth = isVertical ? imageSize.Height : imageSize.Width;
        var effectiveHeight = isVertical ? imageSize.Width : imageSize.Height;

        // Usable canvas after the user's fit padding.
        var paddedCanvasWidth = canvasSize.Width * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var paddedCanvasHeight = canvasSize.Height * (AppConfig.Settings.ImageFitPercentage / 100.0f);

        var horizontalScale = paddedCanvasWidth / effectiveWidth;
        var verticalScale = paddedCanvasHeight / effectiveHeight;

        // Smaller factor ⇒ the image fits without being cropped on either axis.
        return (float)Math.Min(horizontalScale, verticalScale);
    }

    /// <summary>
    /// When sticky zoom is enabled, snaps a freshly computed <paramref name="newScale"/> onto the first
    /// <see cref="ZoomSnapPoints"/> stop crossed in the zoom <paramref name="direction"/>; otherwise returns
    /// <paramref name="newScale"/> unchanged.
    /// </summary>
    public static float ApplyZoomSnap(float newScale, float oldScale, ZoomDirection direction)
    {
        if (direction == ZoomDirection.In)
        {
            float? snap = ZoomSnapPoints
                .Where(s => s > oldScale && s <= newScale)
                .Cast<float?>()
                .FirstOrDefault();
            if (snap.HasValue) return snap.Value;
        }
        else
        {
            float? snap = ZoomSnapPoints
                .Where(s => s < oldScale && s >= newScale)
                .Cast<float?>()
                .LastOrDefault();
            if (snap.HasValue) return snap.Value;
        }
        return newScale;
    }

    /// <summary>
    /// The ordered, de-duplicated step-zoom stops: the dynamic screen-fit scale plus 100% and 400%.
    /// </summary>
    public static List<float> BuildZoomStops(float screenFitScale) =>
        new List<float> { screenFitScale, 1.0f, 4.0f }.Distinct().OrderBy(s => s).ToList();
}
