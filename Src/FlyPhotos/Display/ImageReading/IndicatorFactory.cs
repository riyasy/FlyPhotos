using System;
using System.IO;
using Windows.Foundation;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

namespace FlyPhotos.Display.ImageReading;

internal sealed class IndicatorFactory(CanvasControl canvas)
{
    private const float IndicatorSize = 600f;

    private CanvasTextFormat _iconFormat;
    private CanvasTextFormat _textFormat;

    //private static CanvasBitmap FileNotFound;
    //private static CanvasBitmap PreviewFailed;
    //private static CanvasBitmap HqFailed;
    //private static CanvasBitmap Loading;

    public CanvasBitmap FileNotFound => field ??= Create("\uE783", "Status_FileNotFound");
    public CanvasBitmap PreviewFailed => field ??= Create("\uE91B", "Status_PreviewFailed");
    public CanvasBitmap HqFailed => field ??= Create("\uE91B", "Status_InvalidFile");
    public CanvasBitmap Loading => field ??= Create("\uF16A", "Status_Loading");

    private CanvasBitmap Create(string glyph, string resourceKey)
    {
        EnsureFormats();

        string text = L.Get(resourceKey);

        var target = new CanvasRenderTarget(canvas, IndicatorSize, IndicatorSize, 96);

        using var ds = target.CreateDrawingSession();
        ds.Clear(Colors.White);

        ds.DrawText(
            glyph,
            new Rect(0, 0, IndicatorSize, IndicatorSize * 0.8f),
            Colors.Gray,
            _iconFormat);

        ds.DrawText(
            text,
            new Rect(0, IndicatorSize * 0.7f, IndicatorSize, IndicatorSize * 0.3f),
            Colors.Black,
            _textFormat);

        return target;
    }

    private void EnsureFormats()
    {
        if (_iconFormat == null)
        {
            var fontPath = PathResolver.IsPackagedApp
                ? "ms-appx:///Assets/Fonts/Segoe Fluent Icons.ttf#Segoe Fluent Icons"
                : Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Segoe Fluent Icons.ttf") + "#Segoe Fluent Icons";

            _iconFormat = new CanvasTextFormat
            {
                FontFamily = fontPath,
                FontSize = IndicatorSize * 0.5f,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
        }

        if (_textFormat == null)
        {
            _textFormat = new CanvasTextFormat
            {
                FontSize = IndicatorSize * 0.06f,
                WordWrapping = CanvasWordWrapping.Wrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
        }
    }

    //public static async Task Initialize_Old(CanvasControl d2dCanvas)
    //{
    //    FileNotFound = await LoadIndicatorAsync(d2dCanvas, "FileNotFound.png");
    //    PreviewFailed = await LoadIndicatorAsync(d2dCanvas, "PreviewFailed.png");
    //    HqFailed = await LoadIndicatorAsync(d2dCanvas, "HQImageFailed.png");
    //    Loading = await LoadIndicatorAsync(d2dCanvas, "Loading.png");
    //}

    //private static async Task<CanvasBitmap> LoadIndicatorAsync(CanvasControl d2dCanvas, string fileName)
    //{
    //    var path = PathResolver.IsPackagedApp
    //        ? $"ms-appx:///Assets/Images/{fileName}"
    //        : Path.Combine(AppContext.BaseDirectory, "Assets", "Images", fileName);

    //    return PathResolver.IsPackagedApp
    //        ? await CanvasBitmap.LoadAsync(d2dCanvas, new Uri(path))
    //        : await CanvasBitmap.LoadAsync(d2dCanvas, path);
    //}
}
