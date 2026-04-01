#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Wraps the Rust-based rawler_bridge DLL for high-performance reading of RAW image files.
/// This utilizes the rawler Rust crate to extract both embedded preview images and full high-quality renders.
/// </summary>
internal static unsafe partial class RawlerWrapper
{
    private const string DllName = "fly_rust_bridge.dll";

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr get_hq_image(string path, out int width, out int height, out int rotation);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr get_embedded_preview(string path, out int width, out int height, out int rotation, out int primaryWidth, out int primaryHeight);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void free_rust_buffer(IntPtr ptr, UIntPtr size);

    /// <summary>
    /// Safely handles copying the unmanaged Rust pointer into a Win2D CanvasBitmap 
    /// using modern, zero-initialization vectorized memory copying.
    /// Ensures the unmanaged pointer is ALWAYS freed.
    /// </summary>
    private static CanvasBitmap? CreateBitmapAndFree(CanvasControl ctrl, IntPtr ptr, int width, int height)
    {
        if (ptr == IntPtr.Zero)
            return null;

        int totalBytes = width * height * 4;
        try
        {
            // This prevents .NET from wasting CPU cycles zeroing out up to 100MB+ of memory.
            byte[] bgraPixels = GC.AllocateUninitializedArray<byte>(totalBytes);
            // Span.CopyTo uses heavily optimized SIMD memmove instructions under the hood.
            new ReadOnlySpan<byte>((void*)ptr, totalBytes).CopyTo(bgraPixels);
            return CanvasBitmap.CreateFromBytes(ctrl, bgraPixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            // (uint) cast ensures that even if width*height*4 overflows to a negative signed int, 
            // the bitwise memory size matches the exact usize Rust used to allocate it.
            free_rust_buffer(ptr, (UIntPtr)(uint)totalBytes);
        }
    }

    /// <summary>
    /// Shared internal logic to extract the embedded JPEG preview and related metadata from a RAW file.
    /// </summary>
    private static (bool success, CanvasBitmap? bitmap, int rotation, int primaryWidth, int primaryHeight) LoadEmbeddedPreview(CanvasControl ctrl, string inputPath)
    {
        IntPtr ptr = get_embedded_preview(inputPath, out var width, out var height, out var rotation, out var primaryWidth, out var primaryHeight);

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
        // Check if we should skip full decode and just use the embedded preview
        if (!AppConfig.Settings.DecodeRawData)
        {
            var (success, bitmap, rotation1, _, _) = LoadEmbeddedPreview(ctrl, inputPath);
            if (success && bitmap != null)
                return (true, new StaticHqDisplayItem(bitmap, Origin.Disk, rotation1));

            // If embedded preview failed, fall through to attempt full RAW decode
        }

        // Proceed with full High-Quality decode
        IntPtr ptr = get_hq_image(inputPath, out var width, out var height, out var rotation2);

        var canvasBitmap = CreateBitmapAndFree(ctrl, ptr, width, height);

        return canvasBitmap != null
            ? (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation2))
            : (false, HqDisplayItem.Empty());
    }
}