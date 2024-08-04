//using Aspose.PSD;
//using Aspose.PSD.ImageOptions;
//using FlyPhotos.Data;
//using Microsoft.Graphics.Canvas;
//using Microsoft.Graphics.Canvas.UI.Xaml;
//using System;
//using System.IO;
//using System.Threading.Tasks;
//using Windows.Storage.Streams;

//namespace FlyPhotos.Readers
//{
//    internal class PsdReader
//    {
//        public static async Task<(bool, Photo)> GetPreview(CanvasControl ctrl, string inputPath)
//        {
//            try
//            {
//                // Load the PSD file using Aspose.PSD
//                using (var psdImage = Image.Load(inputPath))
//                {
//                    // Create a memory stream to hold the PNG data
//                    using (var memoryStream = new MemoryStream())
//                    {
//                        // Define the save options for the PNG format
//                        var options = new JpegOptions();
//                        options.Quality = 80;
//                        options.JpegLsAllowedLossyError = 10;

//                        // Save the PSD as a PNG to the memory stream
//                        psdImage.Save(memoryStream, options);

//                        // Rewind the memory stream to the beginning
//                        memoryStream.Seek(0, System.IO.SeekOrigin.Begin);

//                        // Create a CanvasBitmap from the memory stream
//                        using (var randomAccessStream = memoryStream.AsRandomAccessStream())
//                        {
//                            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, randomAccessStream);
//                            return (true, new Photo(canvasBitmap, Photo.PreviewSource.FromDisk));
//                        }
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                // Handle exceptions (e.g., file not found, invalid PSD file, etc.)
//                System.Diagnostics.Debug.WriteLine($"Error loading PSD file: {ex.Message}");
//                return (false, null);
//            }
//        }

//        public static async Task<(bool, Photo)> GetHq(CanvasControl ctrl, string inputPath)
//        {
//            try
//            {
//                // Load the PSD file using Aspose.PSD
//                using (var psdImage = Image.Load(inputPath))
//                {
//                    // Create a memory stream to hold the PNG data
//                    using (var memoryStream = new MemoryStream())
//                    {
//                        // Define the save options for the PNG format
//                        var options = new JpegOptions();
//                        options.Quality = 80;
//                        options.JpegLsAllowedLossyError = 10;

//                        // Save the PSD as a PNG to the memory stream
//                        psdImage.Save(memoryStream, options);

//                        // Rewind the memory stream to the beginning
//                        memoryStream.Seek(0, System.IO.SeekOrigin.Begin);

//                        // Create a CanvasBitmap from the memory stream
//                        using (var randomAccessStream = memoryStream.AsRandomAccessStream())
//                        {
//                            var canvasBitmap = await CanvasBitmap.LoadAsync(ctrl, randomAccessStream);
//                            return (true, new Photo(canvasBitmap, Photo.PreviewSource.FromDisk));
//                        }
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                // Handle exceptions (e.g., file not found, invalid PSD file, etc.)
//                System.Diagnostics.Debug.WriteLine($"Error loading PSD file: {ex.Message}");
//                return (false, null);
//            }
//        }
//    }
//}
