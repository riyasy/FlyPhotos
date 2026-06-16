using System;
using System.Collections.Generic;
using NLog;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
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
internal partial class StaticImageRenderer : IRenderer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly CanvasBitmap _sourceBitmap;
    private readonly Action _invalidateCanvas;

    private CanvasImageBrush _checkeredBrush;
    public CanvasImageBrush CheckeredBrush { set => _checkeredBrush = value; }
    private readonly bool _supportsTransparency;
    private readonly CanvasAnimatedControl _canvas;

    // Mipmap pyramid for smooth animation. Index 0 = 0.5× source, 1 = 0.25×, 2 = 0.125×, …
    // _sourceBitmap is the implicit level 0 and is never stored here.
    // _mipChain and _mipGenCts are swapped/cancelled under _mipChainLock; Draw also holds it while selecting a level.
    private CanvasRenderTarget[] _mipChain = [];
    private readonly bool _generateMipChain;
    private readonly Lock _mipChainLock = new();
    private CancellationTokenSource _mipGenCts;
    private volatile bool _mipChainReady;

    public StaticImageRenderer(CanvasAnimatedControl canvas, CanvasBitmap sourceBitmap,
        bool supportsTransparency, Action invalidateCanvas, bool generateMipChain = true)
    {
        _sourceBitmap = sourceBitmap;
        _supportsTransparency = supportsTransparency;
        _invalidateCanvas = invalidateCanvas;
        _generateMipChain = generateMipChain;
        _canvas = canvas;

        if (_generateMipChain)
            KickOffMipGeneration();
    }

    private void KickOffMipGeneration()
    {
        _mipGenCts = new CancellationTokenSource();
        var token = _mipGenCts.Token;
        _ = Task.Run(() => GenerateMipChain(token), token);
    }

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

    public void HandleScalingMethodChange()
    {
        if (!_generateMipChain) return;
        lock (_mipChainLock)
        {
            _mipGenCts?.Cancel();
            _mipGenCts?.Dispose();
            DestroyMipChain();
        }
        KickOffMipGeneration();
        _invalidateCanvas(); // wake canvas immediately; Draw falls back to source while mips rebuild
    }

    private void DestroyMipChain()
    {
        foreach (var mip in _mipChain) mip?.Dispose();
        _mipChain = [];
        _mipChainReady = false;
    }

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