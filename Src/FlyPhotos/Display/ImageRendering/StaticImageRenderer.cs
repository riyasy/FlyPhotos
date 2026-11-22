using System;
using System.Collections.Generic;
using NLog;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Display.Canvas;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>
/// Draws one bitmap: a preview, a static HQ, or an animation's first frame. For an HQ it builds a mip chain (halved
/// copies) on the thread pool, so a zoomed-out or animating view draws from a smaller copy.
/// <para>
/// The bitmap is the display item's, not this renderer's: the canvas holds the item's lease while this renderer is
/// installed, and the mip task takes its own, as it may still read the bitmap after the renderer is replaced.
/// </para>
/// </summary>
internal partial class StaticImageRenderer : IRenderer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasBitmap _sourceBitmap;
    private readonly Action _invalidateCanvas;
    private readonly CanvasAnimatedControl _canvas;

    // Level 0 is _sourceBitmap; _mipChain[0] is ½×, [1] ¼×, … Swapped under _mipChainLock, which Draw also holds.
    private CanvasRenderTarget[] _mipChain = [];
    // The item that owns _sourceBitmap, leased by the mip task; null = no mip chain.
    private readonly DisplayItem _mipChainOwner;
    private readonly Lock _mipChainLock = new();
    private CancellationTokenSource _mipGenCts;
    private volatile bool _mipChainReady;

    /// <param name="mipChainOwner">The item owning <paramref name="sourceBitmap"/>: given (an HQ), a mip chain is built.</param>
    public StaticImageRenderer(CanvasAnimatedControl canvas, CanvasBitmap sourceBitmap, Action invalidateCanvas,
        DisplayItem mipChainOwner = null)
    {
        _sourceBitmap = sourceBitmap;
        _invalidateCanvas = invalidateCanvas;
        _mipChainOwner = mipChainOwner;
        _canvas = canvas;

        if (_mipChainOwner != null)
            KickOffMipGeneration();
    }

    private void KickOffMipGeneration()
    {
        _mipGenCts = new CancellationTokenSource();
        var token = _mipGenCts.Token;
        if (!_mipChainOwner.TryAcquire()) return; // already freed: no mips, Draw uses the source
        // No token on Task.Run: a task cancelled before it starts would skip the finally and leak the lease.
        _ = Task.Run(() =>
        {
            try { GenerateMipChain(token); }
            finally { _mipChainOwner.Release(); }
        });
    }

    // Thread pool. Up to 5 levels, none under 64 px, published together; a cancelled run publishes nothing.
    private void GenerateMipChain(CancellationToken token)
    {
        const int MaxLevels = 5;
        const float MinDimension = 64f;

        try
        {
            // One quality for every level; a change of setting cancels and restarts the run.
            var genQuality = AppConfig.Settings.ImageScalingQuality.ToCanvasInterpolation(false);

            var mips = new List<CanvasRenderTarget>(MaxLevels);
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
                    ds.DrawImage(prevLevel, new Rect(0, 0, halfW, halfH), new Rect(0, 0, prevWidth, prevHeight), 1f,
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

    public void Draw(CanvasDrawingSession session, in ViewFrame frame, CanvasImageInterpolation quality, bool isAnimating)
    {
        CanvasBitmap src;
        lock (_mipChainLock)
            src = SelectBitmapForScale(frame.Scale);
        session.DrawImage(src, frame.ImageRect, src.Bounds, 1f, quality);
    }

    // The smallest level still at least the size on screen, so a level is only ever scaled down. Under the lock.
    private CanvasBitmap SelectBitmapForScale(float scale)
    {
        if (!_mipChainReady || _mipChain.Length == 0 || scale >= 1f)
            return _sourceBitmap;

        // k = floor(log2(1/scale)): 0 at scale ≥ 1, 1 below 0.5, 2 below 0.25, …
        var k = (int)Math.Floor(Math.Log2(1.0 / scale));
        k = Math.Clamp(k, 0, _mipChain.Length);
        return k == 0 ? _sourceBitmap : _mipChain[k - 1];
    }

    /// <summary>Rebuilds the mip chain with the new quality (or on the new device); the source is drawn meanwhile.</summary>
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
        _invalidateCanvas();
    }

    // Under the lock. The mips are this renderer's own.
    private void DestroyMipChain()
    {
        foreach (var mip in _mipChain) mip?.Dispose();
        _mipChain = [];
        _mipChainReady = false;
    }

    /// <summary>Stops the mip task and frees the mips. The source bitmap stays: the canvas releases its item next.</summary>
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
