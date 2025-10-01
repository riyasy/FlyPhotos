using System;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Controllers.Renderers
{
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
}