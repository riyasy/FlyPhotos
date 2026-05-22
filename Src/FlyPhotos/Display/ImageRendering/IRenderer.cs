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
    void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality);

    void RestartOffScreenDrawTimer();

    void TryRedrawOffScreen(bool forceCreate);
}