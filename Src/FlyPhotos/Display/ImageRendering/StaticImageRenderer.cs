using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;
using FlyPhotos.Core;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Size = FlyPhotos.Core.Model.Size;

namespace FlyPhotos.Display.ImageRendering;

// TODO - 
// 1. Now antialiasing is disabled when drawing checkerboard. Find another way
internal partial class StaticImageRenderer : IRenderer
{
    private readonly CanvasBitmap _sourceBitmap;
    private readonly Action _invalidateCanvas;

    private readonly CanvasViewState _canvasViewState;
    private CanvasImageBrush _checkeredBrush;
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }
    private readonly bool _supportsTransparency;
    private readonly CanvasAnimatedControl _canvas;

    // Off-screen render target for the scaled image.
    // Created on demand when zoomed in beyond a certain threshold,
    // to avoid expensive real-time scaling of large images on the W2D thread.
    // Updated asynchronously after a delay when view state changes,
    // to avoid unnecessary repeated creation while user is actively zooming/panning.
    private CanvasRenderTarget _offscreen;
    private readonly bool _createOffScreen;
    private readonly Lock _timerLock = new();
    private CancellationTokenSource _offscreenDrawCts;

    private Rect SourceBounds => _sourceBitmap.Bounds;

    public StaticImageRenderer(CanvasAnimatedControl canvas, CanvasViewState canvasViewState, CanvasBitmap sourceBitmap,
        bool supportsTransparency, Action invalidateCanvas, bool createOffScreen = true)
    {
        _sourceBitmap = sourceBitmap;
        _supportsTransparency = supportsTransparency;
        _invalidateCanvas = invalidateCanvas;
        _createOffScreen = createOffScreen;
        _canvasViewState = canvasViewState;
        _canvas = canvas;
    }


    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        session.Units = CanvasUnits.Pixels;
        var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
        session.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (drawCheckeredBackground)
        {
            var brushScale = viewState.MatInv.M11;
            _checkeredBrush.Transform = Matrix3x2.CreateScale(brushScale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }


        lock (_timerLock)
        {
            if (_offscreen != null && !isAnimating)
                session.DrawImage(_offscreen, viewState.ImageRect, _offscreen.Bounds, 1f, quality);
            else
                session.DrawImage(_sourceBitmap, viewState.ImageRect, _sourceBitmap.Bounds, 1f, quality);
        }

        // DrawDebugInfo(session, viewState);
    }

    private void DrawDebugInfo(CanvasDrawingSession session, CanvasViewState viewState)
    {
        // Save the current transform
        var originalTransform = session.Transform;

        // Reset transform to identity to draw the debug text at a fixed position
        session.Transform = Matrix3x2.Identity;

        // Build the debug text including canvas properties
        string debugText = $"--Canvas Properties--\n" +
                           $"Width = {_canvas.ActualWidth:0.00}, Height = {_canvas.ActualHeight:0.00}\n" +
                           $"Dpi = {_canvas.Dpi}, DpiScale = {_canvas.DpiScale:0.00}\n\n" +
                           $"--Display Source Properties--\n" +
                           $"Width = {SourceBounds.Width:0.00}, Height = {SourceBounds.Height:0.00}, Dpi = {_sourceBitmap.Dpi}\n\n" +
                           viewState.GetAsString();

        var textFormat = new CanvasTextFormat()
        {
            FontSize = 14,
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        var textBrush = new CanvasSolidColorBrush(session, Colors.White);

        // Measure and layout the text
        using (var layout = new CanvasTextLayout(session, debugText, textFormat, 0.0f, 0.0f))
        {
            var textPadding = 4f;
            var backgroundRect = new Rect(
                10 - textPadding,
                10 - textPadding,
                layout.DrawBounds.Width + 2 * textPadding,
                layout.DrawBounds.Height + 2 * textPadding);

            session.FillRectangle(backgroundRect, Color.FromArgb(128, 0, 0, 0)); // semi-transparent black
            session.DrawTextLayout(layout, 10, 10, textBrush);
        }

        // Restore the original transform
        session.Transform = originalTransform;
    }

    public void CancelOffScreenTimer()
    {
        lock (_timerLock)
        {
            _offscreenDrawCts?.Cancel();
        }
    }

    // Invoked on the W2D thread. Snapshots the W2D-owned view state up front so the deferred
    // background task never reads it cross-thread.
    public void RestartOffScreenDrawTimer()
    {
        if (!_createOffScreen) return;
        var scale = _canvasViewState.Scale;
        var imageRect = _canvasViewState.ImageRect;
        var canvasSize = _canvas.GetSize();
        lock (_timerLock)
        {
            _offscreenDrawCts?.Cancel();
            _offscreenDrawCts?.Dispose();
            _offscreenDrawCts = new CancellationTokenSource();
            var token = _offscreenDrawCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Constants.OffScreenDrawDelayMs), token);
                    if (token.IsCancellationRequested) return;

                    lock (_timerLock)
                    {
                        if (token.IsCancellationRequested) return;
                        CreateOffscreen(false, scale, imageRect, canvasSize);
                    }
                    _invalidateCanvas();
                }
                catch (TaskCanceledException) { }
            }, token);
        }
    }

    // Invoked on the W2D thread. Snapshots view state and creates the off-screen synchronously.
    public void TryRedrawOffScreen(bool forceCreate)
    {
        if (!_createOffScreen) return;
        var scale = _canvasViewState.Scale;
        var imageRect = _canvasViewState.ImageRect;
        var canvasSize = _canvas.GetSize();
        lock (_timerLock)
        {
            _offscreenDrawCts?.Cancel();
            CreateOffscreen(forceCreate, scale, imageRect, canvasSize);
        }
        _invalidateCanvas();
    }

    private void CreateOffscreen(bool forceCreate, float scale, Rect imageRect, Size canvasSize)
    {
        var scaledImageWidth = imageRect.Width * scale;
        var scaledImageHeight = imageRect.Height * scale;

        // Always destroy stale offscreen first, before any early-return guard.
        // Guards below only control creation, not cleanup.
        var offScreenDimensionsChanged =
            _offscreen != null &&
            (!(_offscreen.SizeInPixels.Width == (int)scaledImageWidth &&
               _offscreen.SizeInPixels.Height == (int)scaledImageHeight));

        if (forceCreate || offScreenDimensionsChanged)
            DestroyOffscreen();

        // offscreen already exists and matches current scale — reuse it
        if (_offscreen != null)
            return;

        // image is small enough to draw directly from source with no offscreen
        if (imageRect.Width < canvasSize.Width * 1.5 &&
            imageRect.Height < canvasSize.Height * 1.5)
            return;

        // invalid size
        if (scaledImageWidth <= 0 || scaledImageHeight <= 0)
            return;

        // too zoomed-in — offscreen would exceed canvas, draw from source instead
        if (scaledImageWidth > canvasSize.Width * 1.5 &&
            scaledImageHeight > canvasSize.Height * 1.5)
            return;

        var drawingQuality = AppConfig.Settings.HighQualityInterpolation
            ? CanvasImageInterpolation.HighQualityCubic
            : CanvasImageInterpolation.NearestNeighbor;

        var tempOffScreen = new CanvasRenderTarget(_canvas.Device, (float)scaledImageWidth, (float)scaledImageHeight, 96);
        using (var ds = tempOffScreen.CreateDrawingSession())
        {
            var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
            ds.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
            if (drawCheckeredBackground)
            {
                _checkeredBrush.Transform = Matrix3x2.Identity;
                ds.FillRectangle(new Rect(0, 0, scaledImageWidth, scaledImageHeight), _checkeredBrush);
            }
            else
            {
                ds.Clear(Colors.Transparent);
            }

            ds.DrawImage(_sourceBitmap, new Rect(0, 0, scaledImageWidth, scaledImageHeight),
                _sourceBitmap.Bounds, 1, drawingQuality);
        }
        _offscreen = tempOffScreen;
    }

    private void DestroyOffscreen()
    {
        _offscreen?.Dispose();
        _offscreen = null;
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            _offscreenDrawCts?.Cancel();
            _offscreenDrawCts?.Dispose();
            DestroyOffscreen();
        }
    }
}