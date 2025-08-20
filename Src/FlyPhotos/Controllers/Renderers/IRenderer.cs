using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using System;
using Windows.Foundation;

namespace FlyPhotos.Controllers.Renderers
{
    internal interface IRenderer : IDisposable
    {
        Rect SourceBounds { get; }

        /// <summary>
        /// Draws the content to the canvas.
        /// </summary>
        void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, CanvasImageBrush checkeredBrush);

        /// <summary>
        /// Signals the renderer to begin creating its high-quality representation, if applicable.
        /// </summary>
        void RestartOffScreenDrawTimer();
    }
}