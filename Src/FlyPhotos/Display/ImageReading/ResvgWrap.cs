#nullable enable
using System;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Infra.Utils;
using Microsoft.Graphics.Canvas;
using NLog;

namespace FlyPhotos.Display.ImageReading;

internal static partial class ResvgWrap
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Named constants so both call sites stay in sync. If the HQ dimension ever
    // changes, the metadata scaling in GetResized automatically follows.
    private const int PreviewMaxDimension = 800;
    private const int HqMaxDimension = 2000;

    /// <summary>
    /// The preview: the SVG rasterised to 800 px on the longer side, on the GPU when the scene can be built,
    /// otherwise by resvg on the CPU. Its metadata reports the 2000 px HQ size.
    /// </summary>
    public static async Task<(bool, PreviewDisplayItem)> GetResized(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        var (scene, bmp) = await LoadScene(ctrl, inputPath, PreviewMaxDimension);
        if (scene != null)
            using (scene) bmp = Rasterize(scene, PreviewMaxDimension) ?? RenderSvg(ctrl, inputPath, PreviewMaxDimension);
        if (bmp == null) return (false, PreviewDisplayItem.Empty());
        var (width, height) = ((int)bmp.SizeInPixels.Width, (int)bmp.SizeInPixels.Height);

        // Scale the rendered preview dimensions up to what the HQ render would be,
        // so the metadata reflects the full-resolution SVG size.
        int maxCurrent = Math.Max(width, height);
        double scale = (double)HqMaxDimension / maxCurrent;
        int metaWidth = (int)Math.Round(width * scale);
        int metaHeight = (int)Math.Round(height * scale);
        var metadata = new ImageMetadata(metaWidth, metaHeight);

        return (true, new PreviewDisplayItem(bmp, Origin.Disk, metadata));
    }

    /// <summary>
    /// The full-quality item: an <see cref="SvgHqDisplayItem"/> (GPU scene, redrawn as vectors at any zoom)
    /// when the scene can be built, otherwise a 2000 px resvg bitmap as a <see cref="StaticHqDisplayItem"/>.
    /// </summary>
    public static async Task<(bool, HqDisplayItem)> GetHq(ICanvasResourceCreatorWithDpi ctrl, string inputPath)
    {
        // GPU path: the scene is kept and drawn as vectors by the renderer; nothing is drawn here.
        var (scene, bmp) = await LoadScene(ctrl, inputPath, HqMaxDimension);
        if (scene != null)
        {
            var (w, h, _) = RasterSize(scene, HqMaxDimension);
            return (true, new SvgHqDisplayItem(Origin.Disk, scene, w, h));
        }
        if (bmp == null) return (false, HqDisplayItem.Empty());
        return (true, new StaticHqDisplayItem(bmp, Origin.Disk));
    }

    /// <summary>
    /// Reads and parses the SVG once. Returns a GPU scene, or, when the file uses something the GPU path can't
    /// draw yet, resvg's raster fitted to <paramref name="maxDimension"/> from the same parse. If building the
    /// scene throws, resvg renders the file instead (a second read, but rare). Both null if the file can't be
    /// read or parsed (resvg would fail on the same parse).
    /// </summary>
    private static async Task<(SvgScene? Scene, CanvasBitmap? Bitmap)> LoadScene(
        ICanvasResourceCreatorWithDpi ctrl, string inputPath, int maxDimension)
    {
        var (stream, raster) = BuildSceneStream(ctrl, inputPath, maxDimension);
        if (stream == null) return (null, raster);

        try
        {
            return (await SvgScene.LoadAsync(ctrl, stream), null);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SVG scene build failed, using the resvg bitmap: {InputPath}", inputPath);
            return (null, RenderSvg(ctrl, inputPath, maxDimension));
        }
    }

    /// <summary>
    /// Calls svg_build_scene: the draw stream without its status byte, or (status 1) resvg's raster as a
    /// bitmap. Both null on read/parse failure.
    /// </summary>
    private static unsafe (byte[]? Stream, CanvasBitmap? Raster) BuildSceneStream(
        ICanvasResourceCreatorWithDpi ctrl, string inputPath, int maxDimension)
    {
        nint ptr = NativeRustBridge.svg_build_scene(inputPath, maxDimension, out int len);
        if (ptr == nint.Zero || len <= 0)
        {
            Logger.Warn("SVG could not be read or parsed: {InputPath}", inputPath);
            return (null, null);
        }
        try
        {
            var data = new ReadOnlySpan<byte>((void*)ptr, len);
            if (data[0] == 0) return (data[1..].ToArray(), null);

            int w = BitConverter.ToInt32(data[1..]), h = BitConverter.ToInt32(data[5..]);
            var pixels = data.Slice(9, w * h * 4);
            Logger.Info("SVG GPU path not used for {InputPath}: {Reason}", inputPath, Encoding.UTF8.GetString(data[(9 + pixels.Length)..]));
            return (null, CreateBitmap(ctrl, pixels, w, h, inputPath));
        }
        finally
        {
            NativeRustBridge.free_rust_buffer(ptr, (nuint)(uint)len);
        }
    }

    /// <summary>
    /// The pixel size of <paramref name="scene"/> fitted to <paramref name="maxDimension"/> on its longer side,
    /// and the scale (pixels per user unit) that fits it.
    /// </summary>
    private static (int Width, int Height, float Scale) RasterSize(SvgScene scene, int maxDimension)
    {
        var scale = maxDimension / Math.Max(scene.Width, scene.Height);
        return (Math.Max(1, (int)Math.Round(scene.Width * scale)), Math.Max(1, (int)Math.Round(scene.Height * scale)), scale);
    }

    /// <summary>Draws <paramref name="scene"/> on the GPU into a bitmap whose longer side is <paramref name="maxDimension"/>.</summary>
    public static CanvasBitmap? Rasterize(SvgScene scene, int maxDimension)
    {
        CanvasRenderTarget? rt = null;
        try
        {
            var (w, h, scale) = RasterSize(scene, maxDimension);
            rt = new CanvasRenderTarget(scene.Device, w, h, 96f);
            using var painter = new SvgScene.Painter(scene, rt,Matrix3x2.CreateScale(scale),
                AppConfig.Settings.ImageScalingQuality.ToCanvasInterpolation(false));
            painter.Finish();
            return rt;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SVG GPU rasterize failed");
            rt?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Calls the Rust resvg_render_svg entry point, copies the resulting RGBA8 pixels into a
    /// <see cref="CanvasBitmap"/>, and unconditionally frees the native buffer — mirroring the contract in
    /// <see cref="RawlerWrapper"/>.
    /// </summary>
    private static unsafe CanvasBitmap? RenderSvg(ICanvasResourceCreatorWithDpi ctrl, string inputPath, int maxDimension)
    {
        nint pixelPtr = NativeRustBridge.resvg_render_svg(inputPath, maxDimension, out int width, out int height);
        if (pixelPtr == nint.Zero || width <= 0 || height <= 0)
        {
            Logger.Warn("Rust failed to render SVG or returned empty dimensions: {InputPath}", inputPath);
            // pixelPtr is either null or unusable — nothing to free.
            return null;
        }

        // Computed before anything that could throw, so the free below always has the exact size Rust used.
        int totalBytes = width * height * 4;
        try
        {
            return CreateBitmap(ctrl, new ReadOnlySpan<byte>((void*)pixelPtr, totalBytes), width, height, inputPath);
        }
        finally
        {
            // (uint) cast preserves the exact usize Rust used, even if width*height*4 were ever to overflow int.
            NativeRustBridge.free_rust_buffer(pixelPtr, (nuint)(uint)totalBytes);
        }
    }

    /// <summary>
    /// Copies premultiplied RGBA8 <paramref name="pixels"/> (from a Rust buffer the caller frees) into a
    /// <see cref="CanvasBitmap"/>; null on failure, logged.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="GC.AllocateUninitializedArray{T}"/> to skip zero-filling (up to 16 MB for a 2000-px render),
    /// matching the pattern in <see cref="RawlerWrapper"/>.
    /// </remarks>
    private static CanvasBitmap? CreateBitmap(ICanvasResourceCreatorWithDpi ctrl, ReadOnlySpan<byte> pixels, int width, int height, string inputPath)
    {
        try
        {
            // Skip zero-initialisation — every byte is overwritten by CopyTo.
            byte[] copy = GC.AllocateUninitializedArray<byte>(pixels.Length);
            pixels.CopyTo(copy);
            return CanvasBitmap.CreateFromBytes(ctrl, copy, width, height, DirectXPixelFormat.R8G8B8A8UIntNormalized);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to create CanvasBitmap from SVG render: {InputPath}", inputPath);
            return null;
        }
    }
}
