using System;
using System.Numerics;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Renderer for SVGs kept as a retained vector command list. At or below the raster's resolution (and while a
/// heavy SVG is being panned/zoomed) it draws the 2000 px bitmap through a <see cref="StaticImageRenderer"/>
/// (mip chain included); zoomed in past that it replays the command list, which stays sharp at any scale.
/// The command list and the bitmap belong to the <see cref="SvgVectorHqDisplayItem"/>, not to this renderer.
/// </summary>
internal partial class SvgVectorRenderer : IRenderer
{
    /// <summary>The image being drawn: its command list and its fallback bitmap.</summary>
    private readonly SvgVectorHqDisplayItem _item;
    /// <summary>Whether the image may have alpha, so the checkerboard is drawn under it.</summary>
    private readonly bool _supportsTransparency;
    /// <summary>Draws the bitmap when the vector path isn't used; owns the mip chain.</summary>
    private readonly StaticImageRenderer _bitmapRenderer;

    /// <summary>Checkerboard brush drawn under transparent images.</summary>
    private CanvasImageBrush _checkeredBrush;
    /// <summary>Set by CanvasController on the W2D thread when the renderer is installed.</summary>
    public CanvasImageBrush CheckeredBrush
    {
        set
        {
            _checkeredBrush = value;
            _bitmapRenderer.CheckeredBrush = value;
        }
    }

    public SvgVectorRenderer(CanvasAnimatedControl canvas, SvgVectorHqDisplayItem item,
        bool supportsTransparency, Action invalidateCanvas)
    {
        _item = item;
        _supportsTransparency = supportsTransparency;
        _bitmapRenderer = new StaticImageRenderer(canvas, item.Bitmap, supportsTransparency, invalidateCanvas);
    }

    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        // Below 1:1 the raster (with mips) is as sharp as it gets and cheaper than replaying the vectors.
        if (viewState.Scale <= 1f || (_item.IsHeavy && isAnimating))
        {
            _bitmapRenderer.Draw(session, viewState, quality, isAnimating);
            return;
        }

        session.Units = CanvasUnits.Pixels;
        if (AppConfig.Settings.CheckeredBackground && _supportsTransparency)
        {
            session.Antialiasing = CanvasAntialiasing.Aliased;
            _checkeredBrush.Transform = Matrix3x2.CreateScale(1f / viewState.Scale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }
        session.Antialiasing = CanvasAntialiasing.Antialiased;

        // The command list is in SVG user units; viewState.Mat maps image (bitmap) pixels to the screen.
        var toImage = Matrix3x2.CreateScale(
            (float)(viewState.ImageRect.Width / _item.SvgWidth),
            (float)(viewState.ImageRect.Height / _item.SvgHeight));
        session.Transform = toImage * viewState.Mat;
        session.DrawImage(_item.Vector);
        session.Transform = viewState.Mat;
    }

    public void HandleScalingMethodChange() => _bitmapRenderer.HandleScalingMethodChange();

    public void Dispose() => _bitmapRenderer.Dispose();
}
