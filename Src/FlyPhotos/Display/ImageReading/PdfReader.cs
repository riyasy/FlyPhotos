#nullable enable
using System;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Foundation;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using Microsoft.Graphics.Canvas;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// Reads PDFs for the decoder chain, through Windows.Data.Pdf (<see cref="NativePdfBridge"/>). A page's size at
/// 100% is its size at 96 DPI, so an A4 page is 794×1123.
/// </summary>
internal static class PdfReader
{
    /// <summary>The preview's longer side, as for SVG.</summary>
    private const int PreviewMaxDimension = 800;

    /// <summary>Page 1 at 800 px on the longer side; its metadata reports the page's size at 100%.</summary>
    public static async Task<PreviewDisplayItem> GetResized(ICanvasResourceCreatorWithDpi ctrl, string path)
    {
        using var pdf = await PdfFile.OpenAsync(path);
        using var page = pdf.Document.GetPage(0);
        var (width, height) = SizeAt100(page);
        var bitmap = RenderWhole(ctrl.Device, page, (double)PreviewMaxDimension / Math.Max(width, height));
        return new PreviewDisplayItem(bitmap, Origin.Disk, new ImageMetadata(width, height));
    }

    /// <summary>
    /// The full-quality item: the path, page 1's size and the page count. Draws nothing: the renderer opens the
    /// file itself and draws every view as vectors. Fails for a file Windows.Data.Pdf can't open (damaged, not a
    /// PDF, or AES-256 / password encrypted), which then shows the error screen.
    /// </summary>
    public static async Task<HqDisplayItem> GetHq(ICanvasResourceCreatorWithDpi ctrl, string path)
    {
        using var pdf = await PdfFile.OpenAsync(path);
        using var page = pdf.Document.GetPage(0);
        var (width, height) = SizeAt100(page);
        return new PdfHqDisplayItem(ctrl.Device, Origin.Disk, width, height, (int)pdf.Document.PageCount);
    }

    /// <summary>
    /// Page <paramref name="pageIndex"/> drawn whole at <paramref name="scale"/> times its size at 100%, at most
    /// <paramref name="maxDimension"/> on the longer side; owned by the caller. Runs on the thread pool, whatever
    /// thread awaits it. Throws on failure.
    /// </summary>
    public static Task<CanvasBitmap> RenderPage(CanvasDevice device, string path, int pageIndex, double scale,
        int maxDimension) =>
        Task.Run<CanvasBitmap>(async () =>
        {
            using var pdf = await PdfFile.OpenAsync(path);
            using var page = pdf.Document.GetPage((uint)pageIndex);
            var (width, height) = SizeAt100(page);
            return RenderWhole(device, page, Math.Min(scale, (double)maxDimension / Math.Max(width, height)));
        });

    /// <summary>
    /// The whole page drawn at <paramref name="scale"/> times its size at 100%, owned by the caller. The scale is
    /// lowered if needed so neither side passes the GPU's maximum bitmap size (a poster page keeps its shape).
    /// </summary>
    public static CanvasRenderTarget RenderWhole(CanvasDevice device, PdfPage page, double scale)
    {
        var (width, height) = SizeAt100(page);
        scale = Math.Min(scale, (double)device.MaximumBitmapSizeInPixels / Math.Max(width, height));
        return NativePdfBridge.Render(device, page, new Rect(0, 0, page.Size.Width, page.Size.Height),
            (int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>The page's size at 100% (96 DPI), in whole pixels: its size in DIPs, rounded.</summary>
    public static (int Width, int Height) SizeAt100(PdfPage page) =>
        (Math.Max(1, (int)Math.Round(page.Size.Width)), Math.Max(1, (int)Math.Round(page.Size.Height)));
}
