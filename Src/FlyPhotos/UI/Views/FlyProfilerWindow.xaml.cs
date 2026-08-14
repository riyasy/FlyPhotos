using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using FlyPhotos.Display.ImageReading;
using FlyPhotos.Services;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FlyPhotos.UI.Views;

public sealed partial class FlyProfilerWindow : Window
{
    private string _selectedFolder;

    public FlyProfilerWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 760));
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*"); // FolderPicker requires at least one filter entry

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _selectedFolder = folder.Path;
        ProfileAllButton.IsEnabled = true;
        FolderPathText.Text = _selectedFolder;
        StatusText.Text = "Ready to profile.";
    }

    private async void ProfileAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowseFolderButton.IsEnabled = false;
            ProfileAllButton.IsEnabled = false;
            TestButton.IsEnabled = false;

            var files = Directory
                .GetFiles(_selectedFolder, "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Where(f => CodecDiscovery.SupportedExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
            {
                StatusText.Text = "No supported image files in folder.";
                return;
            }

            ProfileProgress.Maximum = files.Length;
            ProfileProgress.Value = 0;
            ProfileProgress.Visibility = Visibility.Visible;

            // Read UI options on the UI thread before entering the background task.
            var mode = ModeSelector.SelectedIndex; // 0 = HQ, 1 = Preview, 2 = Both
            var runHq = mode == 0 || mode == 2;
            var runPreview = mode == 1 || mode == 2;
            var checkLock = CheckLockBox.IsChecked == true;

            var outLines = new List<string>
            {
                "fileName,gethq status,time taken gethq (ms),gethq function used,gethq fallback,FileLocked," +
                "getpreview status,getpreview (ms),getpreview function used,getpreview fallback"
            };

            await Task.Run(async () =>
            {
                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];

                    string hqStatus = "", hqMs = "", hqFunc = "", hqFallback = "", fileLocked = "";
                    if (runHq)
                    {
                        var hq = await ProfilingImageReader.ProbeHq(TestCanvas, file, checkLock);
                        hqStatus = hq.Success.ToString();
                        hqMs = hq.ElapsedMs.ToString();
                        hqFunc = hq.FunctionUsed;
                        hqFallback = hq.Fallback.ToString();
                        fileLocked = hq.Locked?.ToString() ?? "";
                    }

                    string pvStatus = "", pvMs = "", pvFunc = "", pvFallback = "";
                    if (runPreview)
                    {
                        var pv = await ProfilingImageReader.ProbePreview(TestCanvas, file);
                        pvStatus = pv.Success.ToString();
                        pvMs = pv.ElapsedMs.ToString();
                        pvFunc = pv.FunctionUsed;
                        pvFallback = pv.Fallback.ToString();
                    }

                    outLines.Add(string.Join(",",
                        CsvEscape(file),
                        hqStatus, hqMs, hqFunc, hqFallback, fileLocked,
                        pvStatus, pvMs, pvFunc, pvFallback));

                    var done = i + 1;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatusText.Text = $"Processing {done} / {files.Length}…";
                        ProfileProgress.Value = done;
                    });
                }
            });

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var iso = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss"); // ':' is illegal in Windows filenames
            var outPath = Path.Combine(desktop, iso + "_ProfileResult.csv");
            await File.WriteAllLinesAsync(outPath, outLines);

            StatusText.Text = $"Done! Output: {outPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ProfileProgress.Visibility = Visibility.Collapsed;
            BrowseFolderButton.IsEnabled = true;
            ProfileAllButton.IsEnabled = _selectedFolder != null;
            TestButton.IsEnabled = true;
        }
    }

    private static string CsvEscape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

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
                        //{ "SvgSkiaWrap.GetResized", async () => { var (ok, item) = SvgSkiaWrap.GetResized(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        //{ "SvgSkiaWrap.GetHq", async () => { var (ok, item) = SvgSkiaWrap.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "ResvgWrap.GetResized", async () => { var (ok, item) = ResvgWrap.GetResized(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "ResvgWrap.GetHq", async () => { var (ok, item) = ResvgWrap.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "TiffReader.GetFirstFrameFullSize", async () => { var (ok, item) = await TiffReader.GetFirstFrameFullSize(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "TiffReader.GetHq", async () => { var (ok, item) = await TiffReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "IcoReader.GetPreview", async () => { var (ok, item) = await IcoReader.GetPreview(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "IcoReader.GetHq", async () => { var (ok, item) = await IcoReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); return ok; } },
                        { "NativeHeifReader.GetEmbedded", async () => { var (ok, item) = NativeHeifReader.GetEmbedded(TestCanvas, imagePath); if (ok) item?.Dispose(); await Task.CompletedTask; return ok; } },
                        { "NativeHeifReader.GetHq", async () => { var (ok, item) = NativeHeifReader.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); await Task.CompletedTask; return ok; } },
                        { "RawlerWrapper.GetEmbeddedPreview", async () => { var (ok, item) = RawlerWrapper.GetEmbeddedPreview(TestCanvas, imagePath); if (ok) item?.Dispose(); await Task.CompletedTask; return ok; } },
                        { "RawlerWrapper.GetHq", async () => { var (ok, item) = RawlerWrapper.GetHq(TestCanvas, imagePath); if (ok) item?.Dispose(); await Task.CompletedTask; return ok; } },
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
