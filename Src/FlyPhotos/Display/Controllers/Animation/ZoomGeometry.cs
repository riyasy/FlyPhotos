using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Controllers.Animation;

/// <summary>
/// The view's math, kept apart from its state: fit and default scales, zoom steps and stops, and how the pan and
/// scale carry over when the window, the photo or the image quality changes. Every member is a pure function of its
/// arguments (and of <see cref="AppConfig.Settings"/>, which tests set), so <see cref="CanvasViewManager"/>, the
/// animations and <see cref="PerPhotoViewStore"/> share one definition of each rule.
/// <para>
/// Units (see <see cref="Display.State.CanvasViewState"/>): an image size is in <i>layout pixels</i> (the photo's
/// full size; a preview is stretched over it), a canvas size and a pan position in physical screen pixels, a scale
/// in screen pixels per layout pixel, and a rotation in degrees (multiples of 90, not normalized).
/// </para>
/// </summary>
internal static class ZoomGeometry
{
    /// <summary>
    /// Tolerance for treating two scales as equal (fit / 1:1 / default checks). Scales are floats from divisions and
    /// log/exp round-trips, so exact equality never holds; 0.001 is far below a visible difference.
    /// </summary>
    public const float ScaleTolerance = 0.001f;

    // The factor of one wheel or keyboard zoom step.
    private const float ZoomInStep = 1.25f, ZoomOutStep = 0.8f;

    // The scales the wheel "clicks" onto when StickyZoomLevels is enabled.
    private static readonly float[] ZoomSnapPoints = [0.5f, 1.0f, 2.0f, 5.0f, 10.0f];

    // Zoom-out floor: 5%, or the scale that shrinks the image's longer side to 50 px when that is lower.
    private const float MinZoomOutFixedScale = 0.05f;
    private const float MinZoomOutLongSidePixels = 50f;

    /// <summary>Whether two scales are the same, within <see cref="ScaleTolerance"/>.</summary>
    public static bool SameScale(float a, float b) => Math.Abs(a - b) < ScaleTolerance;

    /// <summary>The canvas centre: where a centred image's centre sits.</summary>
    public static Point Centre(Size canvasSize) => new(canvasSize.Width / 2, canvasSize.Height / 2);

    /// <summary>
    /// Whether the image centre <paramref name="imagePos"/> sits at the canvas centre. 1 px covers float noise AND the
    /// ≤0.5 px pixel-grid alignment of a settled position.
    /// </summary>
    public static bool IsCentred(Point imagePos, Size canvasSize)
    {
        var c = Centre(canvasSize);
        return Math.Abs(imagePos.X - c.X) < 1.0 && Math.Abs(imagePos.Y - c.Y) < 1.0;
    }

    /// <summary>Degrees folded into 0–359, so 360 equals 0 and −90 equals 270.</summary>
    public static int NormalizeDegrees(int degrees) => ((degrees % 360) + 360) % 360;

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
        var (effectiveWidth, effectiveHeight) = OnScreenSize(imageSize, imageRotation);

        // Usable canvas after the user's fit padding.
        var paddedCanvasWidth = canvasSize.Width * (AppConfig.Settings.ImageFitPercentage / 100.0f);
        var paddedCanvasHeight = canvasSize.Height * (AppConfig.Settings.ImageFitPercentage / 100.0f);

        var horizontalScale = paddedCanvasWidth / effectiveWidth;
        var verticalScale = paddedCanvasHeight / effectiveHeight;

        // Smaller factor ⇒ the image fits without being cropped on either axis.
        return (float)Math.Min(horizontalScale, verticalScale);
    }

    /// <summary>
    /// The scale a photo opens at: the fit, but never above 100% unless <c>StretchSmallImages</c> is on. (The
    /// explicit Fit command does stretch small images.)
    /// </summary>
    public static float DefaultScale(Size canvasSize, Size imageSize, int imageRotation)
    {
        var fit = CalculateScreenFitScale(canvasSize, imageSize, imageRotation);
        return AppConfig.Settings.StretchSmallImages ? fit : Math.Min(fit, 1.0f);
    }

    /// <summary>
    /// Lowest scale a zoom-out may reach: 5%, or whatever shrinks the image's longer side to 50 px on screen when
    /// that is smaller. At 5% a 100k px image is still 5000 px wide, so very large images need the lower floor. It
    /// also sits below the fit scale for any usable window, so fit stays reachable by wheel. Zoom-in is never floored.
    /// </summary>
    public static float MinZoomOutScale(Size imageSize)
    {
        var longSide = Math.Max(imageSize.Width, imageSize.Height);
        return longSide > 0
            ? Math.Min(MinZoomOutFixedScale, MinZoomOutLongSidePixels / (float)longSide)
            : MinZoomOutFixedScale;
    }

    /// <summary>
    /// The target of one wheel/keyboard zoom step from <paramref name="fromScale"/> (the current target, so quick
    /// notches build on each other), snapped onto the sticky stops when that setting is on. Null when a zoom-out step
    /// would go below <see cref="MinZoomOutScale"/>.
    /// </summary>
    public static float? WheelStepTarget(float fromScale, ZoomDirection direction, Size imageSize)
    {
        var raw = fromScale * (direction == ZoomDirection.In ? ZoomInStep : ZoomOutStep);
        var target = AppConfig.Settings.StickyZoomLevels ? ApplyZoomSnap(raw, fromScale, direction) : raw;
        return direction == ZoomDirection.Out && target < MinZoomOutScale(imageSize) ? null : target;
    }

    /// <summary>
    /// When sticky zoom is enabled, snaps a freshly computed <paramref name="newScale"/> onto the first
    /// <see cref="ZoomSnapPoints"/> stop crossed in the zoom <paramref name="direction"/>; otherwise returns
    /// <paramref name="newScale"/> unchanged.
    /// </summary>
    public static float ApplyZoomSnap(float newScale, float oldScale, ZoomDirection direction)
    {
        // ZoomSnapPoints is ascending: zooming in takes the lowest stop crossed, zooming out the highest.
        if (direction == ZoomDirection.In)
        {
            foreach (var s in ZoomSnapPoints)
                if (s > oldScale && s <= newScale) return s;
        }
        else
        {
            for (var i = ZoomSnapPoints.Length - 1; i >= 0; i--)
                if (ZoomSnapPoints[i] < oldScale && ZoomSnapPoints[i] >= newScale) return ZoomSnapPoints[i];
        }
        return newScale;
    }

    /// <summary>
    /// Returns <paramref name="stop"/> if the step from <paramref name="oldScale"/> to <paramref name="newScale"/>
    /// crosses it (in either direction); otherwise <paramref name="newScale"/> unchanged.
    /// </summary>
    public static float StopOnCrossing(float newScale, float oldScale, float stop) =>
        (oldScale - stop) * (newScale - stop) < 0f ? stop : newScale;

    /// <summary>
    /// The ordered, de-duplicated step-zoom stops: the dynamic screen-fit scale plus 100% and 400%.
    /// </summary>
    public static List<float> BuildZoomStops(float screenFitScale) =>
        new List<float> { screenFitScale, 1.0f, 4.0f }.Distinct().OrderBy(s => s).ToList();

    /// <summary>
    /// The next step-zoom stop (fit, 100%, 400%) from <paramref name="fromScale"/> in <paramref name="direction"/>;
    /// null at either end.
    /// </summary>
    public static float? NextZoomStop(float fromScale, float screenFitScale, ZoomDirection direction)
    {
        var stops = BuildZoomStops(screenFitScale);
        var i = direction == ZoomDirection.In
            ? stops.FindIndex(s => s > fromScale + ScaleTolerance)
            : stops.FindLastIndex(s => s < fromScale - ScaleTolerance);
        return i < 0 ? null : stops[i];
    }

    /// <summary>
    /// The layout size of a preview bitmap of <paramref name="previewWidth"/>×<paramref name="previewHeight"/>: the
    /// photo's <paramref name="fullSize"/> in the preview's own axes, keeping the height and matching the width to the
    /// preview's aspect ratio. So 100% on the preview is one photo pixel per screen pixel, and the HQ replaces it at
    /// the same zoom. The full size can arrive in the other orientation: a preview that is already upright (the disk
    /// cache stores it so) next to a size in sensor orientation, or a preview still to be rotated next to an upright
    /// size. A preview and a full size of opposite orientations are taken as one of those, and the size turned to match.
    /// </summary>
    public static Size PreviewLayoutSize(Size fullSize, double previewWidth, double previewHeight)
    {
        var turned = (previewWidth - previewHeight) * (fullSize.Width - fullSize.Height) < 0;
        var height = turned ? fullSize.Width : fullSize.Height;
        return new Size(height * previewWidth / previewHeight, height);
    }

    /// <summary>The image's size on screen per unit of scale: a 90°/270° rotation swaps the axes.</summary>
    public static (double Width, double Height) OnScreenSize(Size imageSize, int imageRotation) =>
        imageRotation % 180 != 0 ? (imageSize.Height, imageSize.Width) : (imageSize.Width, imageSize.Height);

    /// <summary>
    /// What the scale is multiplied by when a better image of the same photo replaces the one on screen (preview →
    /// HQ), so the image doesn't jump when the HQ's decoder made another size than the preview was laid out at. A
    /// <paramref name="fitted"/> view stays fitted (the fit ratio). Any other keeps its size on the photo: the ratio of
    /// the two images' <see cref="ImagePixelsPerPhotoPixel"/>, or, when one isn't known, its on-screen height. 1 when
    /// a size is empty.
    /// </summary>
    public static float UpgradeScaleRatio(Size canvasSize, Size oldImageSize, int oldRotation, float? oldPixelsPerPhotoPixel,
        Size newImageSize, int newRotation, float? newPixelsPerPhotoPixel, bool fitted)
    {
        if (fitted)
        {
            var oldFit = CalculateScreenFitScale(canvasSize, oldImageSize, oldRotation);
            return oldFit > 0 ? CalculateScreenFitScale(canvasSize, newImageSize, newRotation) / oldFit : 1f;
        }
        if (oldPixelsPerPhotoPixel is { } oldPerPhoto && newPixelsPerPhotoPixel is { } newPerPhoto)
            return oldPerPhoto / newPerPhoto;
        var newHeight = OnScreenSize(newImageSize, newRotation).Height;
        return newHeight > 0 ? (float)(OnScreenSize(oldImageSize, oldRotation).Height / newHeight) : 1f;
    }

    /// <summary>
    /// The image's pixels per pixel of the photo (<paramref name="photoSize"/>: its size on screen by its metadata):
    /// 1 for a preview laid out at the photo's size, 0.996 for an ARW that Rawler cropped, 0.27 for a small embedded
    /// JPEG. The larger of the two axes' ratios: a decoder either crops the sensor border, a few pixels off each axis
    /// (the larger ratio is the closer to the truth, and the same whichever way the photo is turned), or scales the
    /// image down (both ratios alike). Null when the photo's size isn't known.
    /// </summary>
    public static float? ImagePixelsPerPhotoPixel(Size imageSize, int imageRotation, Size? photoSize)
    {
        if (photoSize is not { Width: > 0, Height: > 0 } photo || imageSize.Width <= 0 || imageSize.Height <= 0)
            return null;
        var (width, height) = OnScreenSize(imageSize, imageRotation);
        return (float)Math.Max(width / photo.Width, height / photo.Height);
    }

    /// <summary>
    /// Where the canvas centre falls on the image: a fraction (0–1 per axis) of the image's box on screen, so (0.5, 0.5)
    /// when the image is centred. Unlike the pan position it doesn't depend on the image's size, the scale or the
    /// canvas, so it carries "the same part of the photo at the centre" over a preview → HQ swap, to the next photo
    /// (RetainFromLastPhoto) and into a remembered view. (0.5, 0.5) for an empty image or scale.
    /// </summary>
    public static Point CentreOnImage(Point imagePos, float scale, Size imageSize, int rotation, Size canvasSize)
    {
        var (w, h) = OnScreenSize(imageSize, rotation);
        if (w <= 0 || h <= 0 || scale <= 0) return new Point(0.5, 0.5);
        var c = Centre(canvasSize);
        return new Point(0.5 + (c.X - imagePos.X) / (w * scale), 0.5 + (c.Y - imagePos.Y) / (h * scale));
    }

    /// <summary>The pan (the image centre's position) that puts <paramref name="centreOnImage"/> at the canvas centre.</summary>
    public static Point ImagePosFor(Point centreOnImage, float scale, Size imageSize, int rotation, Size canvasSize)
    {
        var (w, h) = OnScreenSize(imageSize, rotation);
        var c = Centre(canvasSize);
        return new Point(c.X - (centreOnImage.X - 0.5) * w * scale, c.Y - (centreOnImage.Y - 0.5) * h * scale);
    }

    /// <summary>
    /// The pan after the canvas is resized: the image centre keeps its place relative to the canvas (equivalently,
    /// its offset from the centre scales with the canvas). Unchanged when the old size is empty.
    /// </summary>
    public static Point ResizedPan(Point imagePos, Size oldCanvasSize, Size newCanvasSize)
    {
        if (oldCanvasSize.Width <= 0 || oldCanvasSize.Height <= 0) return imagePos;
        return new Point(imagePos.X * newCanvasSize.Width / oldCanvasSize.Width,
                         imagePos.Y * newCanvasSize.Height / oldCanvasSize.Height);
    }
}
