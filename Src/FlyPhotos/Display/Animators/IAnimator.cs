using System;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.Animators;

/// <summary>An animation decoded on demand: updated to an elapsed time, it draws that moment's frame to its surface.</summary>
public interface IAnimator : IDisposable
{
    uint PixelWidth { get; }
    uint PixelHeight { get; }

    /// <summary>The composited frame to draw.</summary>
    ICanvasImage Surface { get; }

    /// <summary>Draws the frame for <paramref name="totalElapsedTime"/> since the animation began onto <see cref="Surface"/>.</summary>
    Task UpdateAsync(TimeSpan totalElapsedTime);

    /// <summary>
    /// How long the frame drawn by the last <see cref="UpdateAsync"/> stays up, so the canvas can sleep until the next
    /// one. <see cref="TimeSpan.MaxValue"/> when it never changes.
    /// </summary>
    TimeSpan TimeToNextFrame { get; }

    /// <summary>How long one loop of the animation runs; <see cref="TimeSpan.Zero"/> when unknown.</summary>
    TimeSpan LoopDuration { get; }
}
