using FlyPhotos.Core.Model;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Infra.Utils;

internal static class ImageInterpolationMapper
{
    // Clamp HighQualityCubic to Anisotropic while animating;
    // the full-quality result lands once the view settles.
    public static CanvasImageInterpolation ToCanvasInterpolation(this ImageInterpolation quality, bool isAnimating)
        => quality switch
        {
            ImageInterpolation.NearestNeighbor => CanvasImageInterpolation.NearestNeighbor,
            ImageInterpolation.Linear => CanvasImageInterpolation.Linear,
            ImageInterpolation.Anisotropic => CanvasImageInterpolation.Anisotropic,
            ImageInterpolation.HighQualityCubic => isAnimating
                ? CanvasImageInterpolation.Anisotropic
                : CanvasImageInterpolation.HighQualityCubic,
            _ => CanvasImageInterpolation.Linear
        };
}
