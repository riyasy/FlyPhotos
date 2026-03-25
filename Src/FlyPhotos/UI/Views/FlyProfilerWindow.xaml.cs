using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using FlyPhotos.Display.ImageReading;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FlyPhotos.UI.Views;

public sealed partial class FlyProfilerWindow : Window
{
    public FlyProfilerWindow()
    {
        InitializeComponent();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TestButton.IsEnabled = false;
            StatusText.Text = "Selecting file...";

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add(".csv");

            // Required for WinUI 3
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                StatusText.Text = "Canceled.";
                TestButton.IsEnabled = true;
                return;
            }

            StatusText.Text = "Processing...";
            
            // Read CSV
            var inputPath = file.Path;
            var lines = await File.ReadAllLinesAsync(inputPath);
            if (lines.Length < 2)
            {
                StatusText.Text = "CSV has no data rows.";
                TestButton.IsEnabled = true;
                return;
            }

            // Prepare output
            var outDir = Path.GetDirectoryName(inputPath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_");
            var outName = timestamp + Path.GetFileNameWithoutExtension(inputPath) + "_Result.csv";
            var outPath = Path.Combine(outDir, outName);

            var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();

            var outLines = new List<string> { lines[0] }; // Copy headers

            // Execute sequentially in background task
            await Task.Run(async () =>
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(',').Select(p => p.Trim()).ToArray();
                    if (parts.Length < 1) continue;

                    var imagePath = parts[0];
                    // Map function names to their execution logic
                    var functionMap = new Dictionary<string, Func<Task<bool>>>()
                    {
                        { "WicReader.GetEmbedded", async () => { var (ok, item) = await WicReader.GetEmbedded(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "WicReader.GetHq", async () => { var (ok, item) = await WicReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "WicReader.GetResized", async () => { var (ok, item) = await WicReader.GetResized(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "MagickNetWrap.GetResized", async () => { var (ok, item) = await MagickNetWrap.GetResized(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "MagickNetWrap.GetEmbeddedForRawFile", async () => { var (ok, item) = await MagickNetWrap.GetEmbeddedForRawFile(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "MagickNetWrap.GetHq", async () => { var (ok, item) = await MagickNetWrap.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "MagicScalerWrap.GetResized", async () => { var (ok, item) = await MagicScalerWrap.GetResized(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "GifReader.GetFirstFrameFullSize", async () => { var (ok, item) = await GifReader.GetFirstFrameFullSize(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "GifReader.GetHq", async () => { var (ok, item) = await GifReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "PngReader.GetFirstFrameFullSize", async () => { var (ok, item) = await PngReader.GetFirstFrameFullSize(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "PngReader.GetHq", async () => { var (ok, item) = await PngReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "PsdReader.GetEmbedded", async () => { var (ok, item) = await PsdReader.GetEmbedded(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "SvgReader.GetResized", async () => { var (ok, item) = SvgReader.GetResized(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "SvgReader.GetHq", async () => { var (ok, item) = SvgReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "TiffReader.GetFirstFrameFullSize", async () => { var (ok, item) = await TiffReader.GetFirstFrameFullSize(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "TiffReader.GetHq", async () => { var (ok, item) = await TiffReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "IcoReader.GetPreview", async () => { var (ok, item) = await IcoReader.GetPreview(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "IcoReader.GetHq", async () => { var (ok, item) = await IcoReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "NativeHeifReader.GetEmbedded", async () => { var (ok, item) = NativeHeifReader.GetEmbedded(TestCanvas, imagePath); if (ok) item?.Dispose(); await Task.CompletedTask; return ok; } },
                        { "NativeHeifReader.GetHq", async () => { var (ok, item) = NativeHeifReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); await Task.CompletedTask; return ok; } },
                    };

                    var outRow = new List<string>(new string[headers.Length]) { [0] = imagePath };

                    // Iterate dynamically over the headers, starting from index 1
                    for (int c = 1; c < headers.Length; c++)
                    {
                        var header = headers[c];
                        var cellValue = c < parts.Length ? parts[c] : "";
                        
                        if (functionMap.TryGetValue(header, out var action))
                        {
                            outRow[c] = await MeasureAsync(cellValue, action);
                        }
                        else
                        {
                            // Output blank if header is unrecognized
                            outRow[c] = "";
                        }
                    }

                    outLines.Add(string.Join(",", outRow));

                    // Optional: Update UI progress
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        StatusText.Text = $"Processed {i} / {lines.Length - 1}";
                    });
                }

                await File.WriteAllLinesAsync(outPath, outLines);
            });

            StatusText.Text = $"Done! Output: {outPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static async Task<string> MeasureAsync(string callFlag, Func<Task<bool>> action)
    {
        if (!string.Equals(callFlag, "Yes", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var sw = Stopwatch.StartNew();
        bool success = false;
        try
        {
            success = await action();
        }
        catch
        {
            // Ignore failure for timing purposes, or could log it
        }
        sw.Stop();
        return success ? sw.ElapsedMilliseconds.ToString() : "Failed";
    }
}
