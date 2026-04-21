#nullable enable
using System;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static unsafe partial class ResvgWrap
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Named constants so both call sites stay in sync. If the HQ dimension ever
    // changes, the metadata scaling in GetResized automatically follows.
    private const int PreviewMaxDimension = 800;
    private const int HqMaxDimension = 2000;

    public static (bool, PreviewDisplayItem) GetResized(CanvasControl ctrl, string inputPath)
    {
        var (bmp, width, height) = RenderSvg(ctrl, inputPath, PreviewMaxDimension);
        if (bmp == null) return (false, PreviewDisplayItem.Empty());

        // Scale the rendered preview dimensions up to what the HQ render would be,
        // so the metadata reflects the full-resolution SVG size.
        int maxCurrent = Math.Max(width, height);
        double scale = (double)HqMaxDimension / maxCurrent;
        int metaWidth = (int)Math.Round(width * scale);
        int metaHeight = (int)Math.Round(height * scale);
        var metadata = new ImageMetadata(metaWidth, metaHeight);

        return (true, new PreviewDisplayItem(bmp, Origin.Disk, metadata));
    }

    public static (bool, HqDisplayItem) GetHq(CanvasControl ctrl, string inputPath)
    {
        var (bmp, _, _) = RenderSvg(ctrl, inputPath, HqMaxDimension);
        if (bmp == null) return (false, HqDisplayItem.Empty());
        return (true, new StaticHqDisplayItem(bmp, Origin.Disk));
    }

    /// <summary>
    /// Calls the Rust resvg_render_svg entry point, copies the resulting RGBA8
    /// pixels into a <see cref="CanvasBitmap"/>, and unconditionally frees the
    /// native buffer — mirroring the contract in <see cref="RawlerWrapper"/>.
    /// </summary>
    private static (CanvasBitmap? Bitmap, int Width, int Height) RenderSvg(
        CanvasControl ctrl, string inputPath, int maxDimension)
    {
        nint pixelPtr = NativeRustBridge.resvg_render_svg(
            inputPath, maxDimension, out int renderWidth, out int renderHeight);

        if (pixelPtr == nint.Zero || renderWidth <= 0 || renderHeight <= 0)
        {
            Logger.Warn("Rust failed to render SVG or returned empty dimensions: {InputPath}", inputPath);
            // pixelPtr is either null or unusable — nothing to free.
            return (null, 0, 0);
        }

        // totalBytes is computed here, before any operation that could throw,
        // so the finally block in CreateBitmapAndFree always has the correct size.
        int totalBytes = renderWidth * renderHeight * 4;
        var bitmap = CreateBitmapAndFree(ctrl, pixelPtr, renderWidth, renderHeight, totalBytes, inputPath);
        return (bitmap, renderWidth, renderHeight);
    }

    /// <summary>
    /// Copies <paramref name="totalBytes"/> bytes from the unmanaged Rust buffer
    /// into a managed array, creates a <see cref="CanvasBitmap"/>, then frees the
    /// native buffer regardless of success or failure.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="GC.AllocateUninitializedArray{T}"/> to skip zero-filling
    /// (up to 16 MB for a 2000-px render) and <see cref="ReadOnlySpan{T}"/> for
    /// a SIMD-optimised memmove, matching the pattern in <see cref="RawlerWrapper"/>.
    /// </remarks>
    private static CanvasBitmap? CreateBitmapAndFree(
        CanvasControl ctrl, nint ptr, int width, int height, int totalBytes, string inputPath)
    {
        try
        {
            // Skip zero-initialisation — every byte is overwritten by CopyTo.
            byte[] pixels = GC.AllocateUninitializedArray<byte>(totalBytes);
            new ReadOnlySpan<byte>((void*)ptr, totalBytes).CopyTo(pixels);
            return CanvasBitmap.CreateFromBytes(ctrl, pixels, width, height, DirectXPixelFormat.R8G8B8A8UIntNormalized);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to create CanvasBitmap from SVG render: {InputPath}", inputPath);
            return null;
        }
        finally
        {
            // (uint) cast preserves the exact usize Rust used, even if width*height*4
            // were ever to overflow int on very large inputs.
            NativeRustBridge.free_rust_buffer(ptr, (nuint)(uint)totalBytes);
        }
    }
}