#nullable enable
using System;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// High-level wrapper around the Rust rawler_bridge DLL.
/// P/Invoke declarations live in <see cref="Infra.Interop.RawlerBridge"/>; this class contains
/// only managed orchestration logic.
/// </summary>
internal static unsafe class RawlerWrapper
{
    /// <summary>
    /// Safely copies an unmanaged Rust pixel buffer into a Win2D <see cref="CanvasBitmap"/>
    /// and unconditionally frees the buffer afterwards.
    /// </summary>
    private static CanvasBitmap? CreateBitmapAndFree(CanvasControl ctrl, nint ptr, int width, int height)
    {
        if (ptr == nint.Zero)
            return null;

        int totalBytes = width * height * 4;
        try
        {
            // AllocateUninitializedArray avoids zeroing potentially 100 MB+ of memory.
            byte[] bgraPixels = GC.AllocateUninitializedArray<byte>(totalBytes);
            // SIMD-optimised memmove via Span.
            new ReadOnlySpan<byte>((void*)ptr, totalBytes).CopyTo(bgraPixels);
            return CanvasBitmap.CreateFromBytes(ctrl, bgraPixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            // (uint) cast preserves the exact usize Rust used, even when width*height*4 overflows int.
            RawlerBridge.free_rust_buffer(ptr, (nuint)(uint)totalBytes);
        }
    }

    /// <summary>
    /// Shared internal logic to extract the embedded JPEG preview and related metadata from a RAW file.
    /// </summary>
    private static (bool success, CanvasBitmap? bitmap, int rotation, int primaryWidth, int primaryHeight)
        LoadEmbeddedPreview(CanvasControl ctrl, string inputPath)
    {
        nint ptr = RawlerBridge.get_embedded_preview(
            inputPath, out var width, out var height,
            out var rotation, out var primaryWidth, out var primaryHeight);

        var bitmap = CreateBitmapAndFree(ctrl, ptr, width, height);

        return bitmap != null
            ? (true, bitmap, rotation, primaryWidth, primaryHeight)
            : (false, null, 0, 0, 0);
    }

    /// <summary>
    /// Extracts the embedded JPEG preview from a RAW file if available.
    /// </summary>
    public static (bool, PreviewDisplayItem) GetEmbeddedPreview(CanvasControl ctrl, string inputPath)
    {
        var (success, bitmap, rotation, pWidth, pHeight) = LoadEmbeddedPreview(ctrl, inputPath);

        if (!success || bitmap == null)
            return (false, PreviewDisplayItem.Empty());

        var metadata = new ImageMetadata(pWidth, pHeight);
        return (true, new PreviewDisplayItem(bitmap, Origin.Disk, metadata, rotation));
    }

    /// <summary>
    /// Fully decodes a RAW file into a high-quality render.
    /// Falls back to the embedded preview if high-quality decoding is disabled in settings.
    /// </summary>
    public static (bool, HqDisplayItem) GetHq(CanvasControl ctrl, string inputPath)
    {
        // Skip full decode when the user has opted for preview-only mode.
        if (!AppConfig.Settings.DecodeRawData)
        {
            var (success, bitmap, rotation1, _, _) = LoadEmbeddedPreview(ctrl, inputPath);
            if (success && bitmap != null)
                return (true, new StaticHqDisplayItem(bitmap, Origin.Disk, rotation1));

            // Embedded preview unavailable — fall through to full RAW decode.
        }

        nint ptr = RawlerBridge.get_hq_image(inputPath, out var width, out var height, out var rotation2);
        var canvasBitmap = CreateBitmapAndFree(ctrl, ptr, width, height);

        return canvasBitmap != null
            ? (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation2))
            : (false, HqDisplayItem.Empty());
    }
}