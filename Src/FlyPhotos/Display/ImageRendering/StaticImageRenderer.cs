using System;
using System.Collections.Generic;
using NLog;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.State;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

namespace FlyPhotos.Display.ImageRendering;

// TODO -
// 1. Now antialiasing is disabled when drawing checkerboard. Find another way

/// <summary>
/// Draws one bitmap: a preview, a static HQ, or an animation's first frame. For an HQ it also builds a <b>mip
/// chain</b> (halved copies) on a background task, so a zoomed-out or animating view draws from a smaller copy
/// instead of resampling the full image every frame.
/// <para>
/// Threads: created on the UI thread; <see cref="Draw"/>, <see cref="HandleScalingMethodChange"/> and
/// <see cref="Dispose"/> run on the canvas's W2D thread; the mip chain is built on the thread pool.
/// </para>
/// <para>
/// Lifetime of the bitmap: the renderer does not own <see cref="_sourceBitmap"/>. The canvas holds a lease on the
/// item it belongs to for as long as this renderer is installed, and the mip task takes its own lease on that item
/// (<see cref="_mipChainOwner"/>), since it may still be reading the bitmap after the renderer is replaced.
/// </para>
/// </summary>
internal partial class StaticImageRenderer : IRenderer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // The bitmap drawn: the display item's, not this renderer's to dispose.
    private readonly CanvasBitmap _sourceBitmap;
    // Wakes the canvas's render loop (any thread): used when the mip chain is ready.
    private readonly Action _invalidateCanvas;

    // The checkered background for transparent images; set by the canvas on the W2D thread, owned by the canvas.
    private CanvasImageBrush _checkeredBrush;
    /// <summary>The checkered background brush, owned by the canvas.</summary>
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }
    // The format can carry alpha: draw the checkered background behind it (if that setting is on).
    private readonly bool _supportsTransparency;
    // The canvas, for its device when making mips.
    private readonly CanvasAnimatedControl _canvas;

    // Mipmap pyramid for smooth animation. Index 0 = 0.5× source, 1 = 0.25×, 2 = 0.125×, …
    // _sourceBitmap is the implicit level 0 and is never stored here.
    // _mipChain and _mipGenCts are swapped/cancelled under _mipChainLock; Draw also holds it while selecting a level.
    private CanvasRenderTarget[] _mipChain = [];
    // The item that owns _sourceBitmap; null = no mip chain. The mip task leases it, since the canvas may
    // release the item (freeing the bitmap) when this renderer is replaced, before the task ends.
    private readonly DisplayItem _mipChainOwner;
    // Guards _mipChain, _mipChainReady and _mipGenCts between the mip task and the W2D thread.
    private readonly Lock _mipChainLock = new();
    // Cancels the running mip task (a quality change restarts it; Dispose stops it).
    private CancellationTokenSource _mipGenCts;
    // _mipChain is complete and may be drawn from. Until then Draw uses the source bitmap.
    private volatile bool _mipChainReady;

    /// <param name="canvas">The main canvas.</param>
    /// <param name="sourceBitmap">The bitmap to draw; its item stays leased by the canvas while this renderer is installed.</param>
    /// <param name="supportsTransparency">Draw the checkered background behind it.</param>
    /// <param name="invalidateCanvas">Wakes the render loop.</param>
    /// <param name="mipChainOwner">The item that owns <paramref name="sourceBitmap"/>: given (an HQ), a mip chain is
    /// built; null (a preview, an animation's first frame), none is.</param>
    public StaticImageRenderer(CanvasAnimatedControl canvas, CanvasBitmap sourceBitmap,
        bool supportsTransparency, Action invalidateCanvas, DisplayItem mipChainOwner = null)
    {
        _sourceBitmap = sourceBitmap;
        _supportsTransparency = supportsTransparency;
        _invalidateCanvas = invalidateCanvas;
        _mipChainOwner = mipChainOwner;
        _canvas = canvas;

        if (_mipChainOwner != null)
            KickOffMipGeneration();
    }

    // Starts building the mip chain on the thread pool, under a lease on the item so its bitmap stays alive until the
    // task ends, even if the canvas replaces this renderer and releases the item first.
    private void KickOffMipGeneration()
    {
        _mipGenCts = new CancellationTokenSource();
        var token = _mipGenCts.Token;
        if (!_mipChainOwner.TryAcquire()) return; // already freed: no mips, Draw uses the source
        // No token on Task.Run: a task cancelled before it starts would skip the finally and leak the lease.
        // GenerateMipChain checks the token itself.
        _ = Task.Run(() =>
        {
            try { GenerateMipChain(token); }
            finally { _mipChainOwner.Release(); }
        });
    }

    // Thread pool. Halves the image level by level (up to 5 levels, none under 64 px), then publishes the whole chain
    // at once under the lock. A cancelled run disposes what it made and publishes nothing.
    private void GenerateMipChain(CancellationToken token)
    {
        const int MaxLevels = 5;
        const float MinDimension = 64f;

        try
        {
            // Snapshot the quality setting once so all levels use the same algorithm.
            // If the user changes quality mid-generation, HandleScalingMethodChange will cancel and restart.
            var genQuality = AppConfig.Settings.ImageScalingQuality.ToCanvasInterpolation(false);

            var mips = new List<CanvasRenderTarget>(MaxLevels);
            // Previous level source — starts as the full-res bitmap.
            CanvasBitmap prevLevel = _sourceBitmap;
            float prevWidth = (float)_sourceBitmap.Bounds.Width;
            float prevHeight = (float)_sourceBitmap.Bounds.Height;

            for (var i = 0; i < MaxLevels; i++)
            {
                if (token.IsCancellationRequested) break;

                var halfW = prevWidth / 2f;
                var halfH = prevHeight / 2f;
                if (halfW < MinDimension || halfH < MinDimension) break;

                var mip = new CanvasRenderTarget(_canvas.Device, halfW, halfH, 96);
                using (var ds = mip.CreateDrawingSession())
                {
                    ds.Clear(Colors.Transparent);
                    ds.DrawImage(prevLevel, new Rect(0, 0, halfW, halfH),
                        new Rect(0, 0, prevWidth, prevHeight), 1f,
                        genQuality);
                }

                if (token.IsCancellationRequested)
                {
                    mip.Dispose();
                    break;
                }

                mips.Add(mip);
                prevLevel = mip;
                prevWidth = halfW;
                prevHeight = halfH;
            }

            if (token.IsCancellationRequested)
            {
                foreach (var m in mips) m.Dispose();
                return;
            }

            lock (_mipChainLock)
            {
                if (token.IsCancellationRequested)
                {
                    foreach (var m in mips) m.Dispose();
                    return;
                }
                _mipChain = [.. mips];
                _mipChainReady = true;
            }
            _invalidateCanvas();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn(ex, "MipChain generation failed — falling back to source bitmap");
        }
    }


    /// <summary>W2D thread. Draws the checkered background if wanted, then the best-fitting mip level (or the source).</summary>
    public void Draw(CanvasDrawingSession session, CanvasViewState viewState, CanvasImageInterpolation quality, bool isAnimating)
    {
        session.Units = CanvasUnits.Pixels;
        var drawCheckeredBackground = AppConfig.Settings.CheckeredBackground && _supportsTransparency;
        session.Antialiasing = drawCheckeredBackground ? CanvasAntialiasing.Aliased : CanvasAntialiasing.Antialiased;
        if (drawCheckeredBackground)
        {
            var brushScale = 1f / viewState.Scale;
            _checkeredBrush.Transform = Matrix3x2.CreateScale(brushScale);
            session.FillRectangle(viewState.ImageRect, _checkeredBrush);
        }


        CanvasBitmap src;
        lock (_mipChainLock)
            src = SelectBitmapForScale(viewState.Scale);
        session.DrawImage(src, viewState.ImageRect, src.Bounds, 1f, quality);

    }

    // Selects the smallest mip level whose pixel dimensions are still >= the display size,
    // ensuring we always downscale (never upscale) from the chosen level.
    // CanvasRenderTarget inherits CanvasBitmap, so both source and mips share the return type.
    // Must be called inside _mipChainLock.
    private CanvasBitmap SelectBitmapForScale(float scale)
    {
        if (!_mipChainReady || _mipChain.Length == 0 || scale >= 1f)
            return _sourceBitmap;

        // k = floor(log2(1/scale)): 0 at scale≥1, 1 at scale<0.5, 2 at scale<0.25, …
        var k = (int)Math.Floor(Math.Log2(1.0 / scale));
        k = Math.Clamp(k, 0, _mipChain.Length);
        return k == 0 ? _sourceBitmap : _mipChain[k - 1];
    }

    /// <summary>
    /// W2D thread. The scaling quality changed (or the device was re-created): drops the mip chain and builds it
    /// again with the new quality. Nothing to do without a mip chain.
    /// </summary>
    public void HandleScalingMethodChange()
    {
        if (_mipChainOwner == null) return;
        lock (_mipChainLock)
        {
            _mipGenCts?.Cancel();
            _mipGenCts?.Dispose();
            DestroyMipChain();
        }
        KickOffMipGeneration();
        _invalidateCanvas(); // wake canvas immediately; Draw falls back to source while mips rebuild
    }

    // Inside _mipChainLock. Disposes the mips (they are this renderer's own) and goes back to drawing the source.
    private void DestroyMipChain()
    {
        foreach (var mip in _mipChain) mip?.Dispose();
        _mipChain = [];
        _mipChainReady = false;
    }

    /// <summary>
    /// W2D thread, when the canvas swaps this renderer out. Stops the mip task and frees the mips. The source bitmap
    /// is not disposed here: the canvas releases its item's lease right after.
    /// </summary>
    public void Dispose()
    {
        lock (_mipChainLock)
        {
            _mipGenCts?.Cancel();
            _mipGenCts?.Dispose();
            DestroyMipChain();
        }
    }
}