using System;
using Windows.Foundation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;

namespace FlyPhotos.Controllers.Renderers
{
    internal interface IRenderer : IDisposable
    {
        Rect SourceBounds { get; }

        /// <summary>
        /// Draws the content to the canvas.
        /// </summary>
        void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality);

        /// <summary>
        /// Signals the renderer to begin creating its high-quality representation, if applicable.
        /// </summary>
        void RestartOffScreenDrawTimer();
    }
}