using System;
using FlyPhotos.Display.State;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageRendering;

internal interface IRenderer : IDisposable
{
    /// <summary>
    /// Draws the content to the canvas.
    /// </summary>
    void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality);

    /// <summary>
    /// Signals the renderer to begin creating its high-quality representation, if applicable.
    /// </summary>
    void RestartOffScreenDrawTimer();

    void TryRedrawOffScreen();
}