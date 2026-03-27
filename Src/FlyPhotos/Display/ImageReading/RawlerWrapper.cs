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
internal static partial class RawlerWrapper
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
    /// Shared internal logic to extract the embedded JPEG preview and related metadata from a RAW file.
    /// </summary>
    private static (bool success, CanvasBitmap? bitmap, int rotation, int primaryWidth, int primaryHeight) LoadEmbeddedPreview(CanvasControl ctrl, string inputPath)
    {
        IntPtr ptr = get_embedded_preview(inputPath, out var width, out var height, out var rotation, out var primaryWidth, out var primaryHeight);

        if (ptr == IntPtr.Zero)
            return (false, null, 0, 0, 0);

        int totalBytes = width * height * 4;
        try
        {
            byte[] bgraPixels = new byte[totalBytes];
            Marshal.Copy(ptr, bgraPixels, 0, totalBytes);

            var canvasBitmap = CanvasBitmap.CreateFromBytes(ctrl, bgraPixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            return (true, canvasBitmap, rotation, primaryWidth, primaryHeight);
        }
        catch (Exception)
        {
            return (false, null, 0, 0, 0);
        }
        finally
        {
            free_rust_buffer(ptr, (UIntPtr)totalBytes);
        }
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

        if (ptr == IntPtr.Zero)
            return (false, HqDisplayItem.Empty());

        int totalBytes = width * height * 4;
        try
        {
            byte[] bgraPixels = new byte[totalBytes];
            Marshal.Copy(ptr, bgraPixels, 0, totalBytes);

            var canvasBitmap = CanvasBitmap.CreateFromBytes(ctrl, bgraPixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            return (true, new StaticHqDisplayItem(canvasBitmap, Origin.Disk, rotation2));
        }
        catch (Exception)
        {
            return (false, HqDisplayItem.Empty());
        }
        finally
        {
            free_rust_buffer(ptr, (UIntPtr)totalBytes);
        }
    }
}