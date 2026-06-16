using System;
using FlyPhotos.Display.State;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;

namespace FlyPhotos.Display.ImageRendering;

internal interface IRenderer : IDisposable
{
    CanvasImageBrush CheckeredBrush { set; }

    /// <summary>
    /// Draws the content to the canvas.
    /// </summary>
    void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating);

    /// <summary>
    /// Called when the image scaling quality setting changes. Renderers that cache scaled
    /// GPU resources (e.g. mip chains) should rebuild them with the new quality.
    /// </summary>
    void HandleScalingMethodChange();
}