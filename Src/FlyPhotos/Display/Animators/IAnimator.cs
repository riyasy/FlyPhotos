using System;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.Animators;

/// <summary>
/// Defines a common interface for on-demand, real-time animators
/// that can be updated based on elapsed time and provide a renderable surface.
/// </summary>
public interface IAnimator : IDisposable
{
    /// <summary>
    /// The width of the animation canvas in pixels.
    /// </summary>
    uint PixelWidth { get; }

    /// <summary>
    /// The height of the animation canvas in pixels.
    /// </summary>
    uint PixelHeight { get; }

    /// <summary>
    /// The composited output surface that can be drawn to a CanvasControl.
    /// </summary>
    ICanvasImage Surface { get; }

    /// <summary>
    /// Updates the animation state to the specified elapsed time.
    /// This method is responsible for rendering the correct frame to the Surface.
    /// </summary>
    /// <param name="totalElapsedTime">The total time elapsed since the animation began.</param>
    Task UpdateAsync(TimeSpan totalElapsedTime);
}