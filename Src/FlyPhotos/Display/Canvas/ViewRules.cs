using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.Canvas;

/// <summary>
/// The view's math, as static functions of their arguments and <c>AppConfig.Settings</c>, so they are tested on their
/// own (<c>ViewMathTests</c>). Only <see cref="LayoutSize"/> and <see cref="PhotoSizeOnScreen"/> read a photo's slots.
/// <para>
/// Units: image sizes in layout pixels, canvas sizes and positions in canvas pixels, scale = canvas pixels per layout
/// pixel, rotation in degrees (90° steps).
/// </para>
/// </summary>
internal static class ViewRules
{
    /// <summary>Two scales closer than this are the same (they come from divisions and log/exp round trips).</summary>
    public const float ScaleTolerance = 0.001f;

    /// <summary>The scale factor of one wheel notch or zoom key, in and out (inverses, so in-then-out returns).</summary>
    private const float ZoomInStep = 1.25f, ZoomOutStep = 0.8f;

    /// <summary>The scales a wheel step stops on when it crosses them, with sticky zoom levels on.</summary>
    private static readonly float[] StickyStops = [0.5f, 1.0f, 2.0f, 5.0f, 10.0f];

    /// <summary>Zooming out stops at 5%...</summary>
    private const float MinZoomOutFixedScale = 0.05f;
    /// <summary>...or lower for a huge image, as long as its long side stays at least this many pixels on screen.</summary>
    private const float MinZoomOutLongSidePixels = 50f;

    /// <summary>Equal within <see cref="ScaleTolerance"/>; use it instead of == for any two scales.</summary>
    public static bool SameScale(float a, float b) => Math.Abs(a - b) < ScaleTolerance;

    /// <summary>The canvas centre, in canvas pixels: where a fitted or centred image's centre goes.</summary>
    public static Point Centre(Size canvasSize) => new(canvasSize.Width / 2, canvasSize.Height / 2);

    /// <summary>Within 1 px of the canvas centre: allows for float noise and the ≤ 0.5 px pixel-grid nudge.</summary>
    public static bool IsCentred(Point imagePos, Size canvasSize)
    {
        var c = Centre(canvasSize);
        return Math.Abs(imagePos.X - c.X) < 1.0 && Math.Abs(imagePos.Y - c.Y) < 1.0;
    }

    /// <summary>Degrees in [0, 360), for comparing rotations that went round more than once.</summary>
    public static int NormalizeDegrees(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>The pan that keeps screen point <paramref name="anchor"/> over the same image pixel through a scale change.</summary>
    public static Point AnchorPreservingPan(Point anchor, Point currentPos, float oldScale, float newScale)
    {
        var k = newScale / oldScale;
        return new Point(anchor.X - k * (anchor.X - currentPos.X), anchor.Y - k * (anchor.Y - currentPos.Y));
    }

    /// <summary>The scale that fits the rotated image inside the canvas, less the user's fit padding (ImageFitPercentage).</summary>
    public static float FitScale(Size canvasSize, Size imageSize, int imageRotation)
    {
        var (width, height) = OnScreenSize(imageSize, imageRotation);
        var padding = AppConfig.Settings.ImageFitPercentage / 100.0f;
        return (float)Math.Min(canvasSize.Width * padding / width, canvasSize.Height * padding / height);
    }

    /// <summary>The scale a photo opens at: the fit, but not above 100% unless small images stretch.</summary>
    public static float DefaultScale(Size canvasSize, Size imageSize, int imageRotation)
    {
        var fit = FitScale(canvasSize, imageSize, imageRotation);
        return AppConfig.Settings.StretchSmallImages ? fit : Math.Min(fit, 1.0f);
    }

    /// <summary>The smallest scale a zoom out may reach (see <see cref="MinZoomOutFixedScale"/>).</summary>
    public static float MinZoomOutScale(Size imageSize)
    {
        var longSide = Math.Max(imageSize.Width, imageSize.Height);
        return longSide > 0
            ? Math.Min(MinZoomOutFixedScale, MinZoomOutLongSidePixels / (float)longSide)
            : MinZoomOutFixedScale;
    }

    /// <summary>
    /// The target of one wheel step from <paramref name="fromScale"/>, stopping on a sticky stop when that setting is
    /// on. Pass the animation's target, not the current scale, so quick notches compound. Null when a step out would
    /// pass the floor (<see cref="MinZoomOutScale"/>).
    /// </summary>
    public static float? WheelStepTarget(float fromScale, ZoomDirection direction, Size imageSize)
    {
        var raw = fromScale * (direction == ZoomDirection.In ? ZoomInStep : ZoomOutStep);
        var target = AppConfig.Settings.StickyZoomLevels ? SnapToStickyStop(raw, fromScale, direction) : raw;
        return direction == ZoomDirection.Out && target < MinZoomOutScale(imageSize) ? null : target;
    }

    /// <summary>
    /// The first sticky stop crossed going from <paramref name="oldScale"/> to <paramref name="newScale"/>, else
    /// <paramref name="newScale"/>. A step that starts on a stop leaves it (the comparison is strict on that side).
    /// </summary>
    public static float SnapToStickyStop(float newScale, float oldScale, ZoomDirection direction)
    {
        if (direction == ZoomDirection.In)
        {
            foreach (var s in StickyStops)
                if (s > oldScale && s <= newScale) return s;
        }
        else
        {
            for (var i = StickyStops.Length - 1; i >= 0; i--)
                if (StickyStops[i] < oldScale && StickyStops[i] >= newScale) return StickyStops[i];
        }
        return newScale;
    }

    /// <summary><paramref name="stop"/> if the step from <paramref name="oldScale"/> to <paramref name="newScale"/> crosses it, else <paramref name="newScale"/>.</summary>
    public static float StopOnCrossing(float newScale, float oldScale, float stop) =>
        (oldScale - stop) * (newScale - stop) < 0f ? stop : newScale;

    /// <summary>The step-zoom stops: fit, 100%, 400%, sorted, and once each when the fit is one of the others.</summary>
    public static List<float> StepZoomStops(float screenFitScale) =>
        new List<float> { screenFitScale, 1.0f, 4.0f }.Distinct().OrderBy(s => s).ToList();

    /// <summary>The next step-zoom stop from <paramref name="fromScale"/> in <paramref name="direction"/>; null past either end.</summary>
    public static float? NextZoomStop(float fromScale, float screenFitScale, ZoomDirection direction)
    {
        var stops = StepZoomStops(screenFitScale);
        var i = direction == ZoomDirection.In
            ? stops.FindIndex(s => s > fromScale + ScaleTolerance)
            : stops.FindLastIndex(s => s < fromScale - ScaleTolerance);
        return i < 0 ? null : stops[i];
    }

    /// <summary>
    /// The layout size of a preview bitmap: the photo's full size in the preview's own axes, with the height kept and
    /// the width matched to the preview's aspect, so 100% means one photo pixel on the preview and on the HQ alike.
    /// A preview and a full size of opposite orientations (an upright cached preview, a size in sensor axes) are
    /// taken as one photo, and the size is turned to match.
    /// </summary>
    public static Size PreviewLayoutSize(Size fullSize, double previewWidth, double previewHeight)
    {
        var turned = (previewWidth - previewHeight) * (fullSize.Width - fullSize.Height) < 0;
        var height = turned ? fullSize.Width : fullSize.Height;
        return new Size(height * previewWidth / previewHeight, height);
    }

    /// <summary>The image's width and height on screen at scale 1: 90° and 270° swap the axes.</summary>
    public static (double Width, double Height) OnScreenSize(Size imageSize, int imageRotation) =>
        imageRotation % 180 != 0 ? (imageSize.Height, imageSize.Width) : (imageSize.Width, imageSize.Height);

    // ---- A better image of the same photo, and the photo's size (#195) ----
    // A preview is laid out at the photo's size by its metadata; an HQ at the size its decoder made (a RAW decoder may
    // crop the sensor border, or give a small embedded JPEG). The view keeps the photo's size on screen across both.

    /// <summary>
    /// What the scale is multiplied by when a better image replaces the one on screen. Fitted: the ratio of the fits.
    /// Otherwise the photo keeps its size on screen: by the images' pixels per photo pixel, or by on-screen height
    /// when either isn't known.
    /// </summary>
    public static float UpgradeScaleRatio(Size canvasSize, Size oldImageSize, int oldRotation, float? oldPixelsPerPhotoPixel,
        Size newImageSize, int newRotation, float? newPixelsPerPhotoPixel, bool fitted)
    {
        if (fitted)
        {
            var oldFit = FitScale(canvasSize, oldImageSize, oldRotation);
            return oldFit > 0 ? FitScale(canvasSize, newImageSize, newRotation) / oldFit : 1f;
        }
        if (oldPixelsPerPhotoPixel is { } oldPerPhoto && newPixelsPerPhotoPixel is { } newPerPhoto)
            return oldPerPhoto / newPerPhoto;
        var newHeight = OnScreenSize(newImageSize, newRotation).Height;
        return newHeight > 0 ? (float)(OnScreenSize(oldImageSize, oldRotation).Height / newHeight) : 1f;
    }

    /// <summary>
    /// The size an item is laid out at: the photo's full size, so a better image replaces it in place. A preview keeps
    /// its own aspect ratio (it can differ slightly from the HQ's), so it isn't distorted. Reads the photo's slots.
    /// </summary>
    public static Size LayoutSize(Photo photo, DisplayItem item)
    {
        var (width, height) = photo.Slots.GetActualSize();
        var full = new Size(width, height);
        return item is PreviewDisplayItem { Bitmap: { } bitmap }
            ? PreviewLayoutSize(full, bitmap.Bounds.Width, bitmap.Bounds.Height)
            : full;
    }

    /// <summary>
    /// The photo's size on screen by its metadata: its preview's layout size, turned by the preview's rotation. Null
    /// while no preview gives the full size. Leases the preview for the read, so any thread may call it.
    /// </summary>
    public static Size PhotoSizeOnScreen(Photo photo)
    {
        if (photo.Slots.Preview is not { } preview || !preview.TryAcquire()) return null;
        try
        {
            if (preview.Metadata is not { HasFullSize: true } m || preview.Bitmap is not { } bitmap) return null;
            var layout = PreviewLayoutSize(new Size(m.FullWidth, m.FullHeight), bitmap.Bounds.Width, bitmap.Bounds.Height);
            var (width, height) = OnScreenSize(layout, preview.Rotation);
            return new Size(width, height);
        }
        finally
        {
            preview.Release();
        }
    }

    /// <summary>
    /// The image's pixels per photo pixel (1 for a preview, 0.996 for a cropped RAW, 0.27 for a small embedded JPEG).
    /// The larger axis ratio, as a border crop changes it least. Null when the photo's size isn't known.
    /// </summary>
    public static float? ImagePixelsPerPhotoPixel(Size imageSize, int imageRotation, Size photoSize)
    {
        if (photoSize is not { Width: > 0, Height: > 0 } photo || imageSize.Width <= 0 || imageSize.Height <= 0)
            return null;
        var (width, height) = OnScreenSize(imageSize, imageRotation);
        return (float)Math.Max(width / photo.Width, height / photo.Height);
    }

    // ---- Where the view looks ----

    /// <summary>
    /// Where the canvas centre falls on the image, as a fraction (0–1) of its box on screen; (0.5, 0.5) when centred.
    /// Independent of the image's size and the scale, so it carries "the same part at the centre" to another image.
    /// </summary>
    public static Point CentreOnImage(Point imagePos, float scale, Size imageSize, int rotation, Size canvasSize)
    {
        var (w, h) = OnScreenSize(imageSize, rotation);
        if (w <= 0 || h <= 0 || scale <= 0) return new Point(0.5, 0.5);
        var c = Centre(canvasSize);
        return new Point(0.5 + (c.X - imagePos.X) / (w * scale), 0.5 + (c.Y - imagePos.Y) / (h * scale));
    }

    /// <summary>The image position that puts <paramref name="centreOnImage"/> (see <see cref="CentreOnImage"/>) at the canvas centre.</summary>
    public static Point ImagePosFor(Point centreOnImage, float scale, Size imageSize, int rotation, Size canvasSize)
    {
        var (w, h) = OnScreenSize(imageSize, rotation);
        var c = Centre(canvasSize);
        return new Point(c.X - (centreOnImage.X - 0.5) * w * scale, c.Y - (centreOnImage.Y - 0.5) * h * scale);
    }

    /// <summary>The image position after a resize: the image centre keeps its place relative to the canvas.</summary>
    public static Point ResizedPan(Point imagePos, Size oldCanvasSize, Size newCanvasSize)
    {
        if (oldCanvasSize.Width <= 0 || oldCanvasSize.Height <= 0) return imagePos;
        return new Point(imagePos.X * newCanvasSize.Width / oldCanvasSize.Width,
                         imagePos.Y * newCanvasSize.Height / oldCanvasSize.Height);
    }
}
