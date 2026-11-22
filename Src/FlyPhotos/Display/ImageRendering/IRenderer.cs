using System;
using FlyPhotos.Display.Canvas;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>Draws one kind of content. Made on the UI thread; drawn and disposed on the W2D thread.</summary>
internal interface IRenderer : IDisposable
{
    /// <summary>
    /// Draws the content in <paramref name="frame"/>'s layout pixels. The engine has set the session's transform to
    /// <c>frame.Mat</c>, its units to pixels and its antialiasing, and drawn the checkerboard.
    /// </summary>
    void Draw(CanvasDrawingSession session, in ViewFrame frame, CanvasImageInterpolation quality, bool isAnimating);

    /// <summary>The scaling quality changed or the device was re-created: rebuild any scaled copies.</summary>
    void HandleScalingMethodChange();
}
