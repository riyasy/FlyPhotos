using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageRendering;

internal static class ImageInterpolationMapper
{
    /// <summary>
    /// Maps the user-chosen <see cref="ImageInterpolation"/> setting to a Win2D
    /// <see cref="CanvasImageInterpolation"/>. HighQualityCubic is too costly to run every frame,
    /// so it is clamped to Linear while a pan/zoom animation is ongoing.
    /// </summary>
    public static CanvasImageInterpolation ToCanvasInterpolation(this ImageInterpolation quality, bool isAnimating)
        => quality switch
        {
            ImageInterpolation.NearestNeighbor => CanvasImageInterpolation.NearestNeighbor,
            ImageInterpolation.Linear => CanvasImageInterpolation.Linear,
            ImageInterpolation.HighQualityCubic => isAnimating
                ? CanvasImageInterpolation.Linear
                : CanvasImageInterpolation.HighQualityCubic,
            _ => CanvasImageInterpolation.Linear
        };
}
