using LiteDB;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using PhotoSauce.MagicScaler;
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

namespace FlyPhotos.Utils;
public sealed class PhotoDiskCacher : IDisposable
{
    private static readonly Lazy<PhotoDiskCacher> instance = new(() => new PhotoDiskCacher());

    private readonly string _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyPhotosCache.db");
    private readonly LiteDatabase _db;
    private bool _disposed;

    private const int MaxItemCount = 20000; // Maximum number of items in the cache

    private PhotoDiskCacher()
    {
        _db = new LiteDatabase(_dbPath);
        var col = _db.GetCollection<CachedImage>("images");
        col.EnsureIndex(x => x.FilePath);
        col.EnsureIndex(x => x.LastAccessed);
    }

    public static PhotoDiskCacher Instance => instance.Value;

    public async Task<CanvasBitmap> ReturnFromCache(CanvasControl canvasControl, string filePath)
    {
        var col = _db.GetCollection<CachedImage>("images");
        var cachedImage = col.FindOne(x => x.FilePath == filePath);

        if (cachedImage == null) return null; // Not found or outdated in cache
        // Check if the last modified date of the file has changed
        var lastModified = File.GetLastWriteTimeUtc(filePath).ToString("yyyyMMddHHmmss");
        if (cachedImage.LastModified == lastModified)
        {
            cachedImage.LastAccessed = DateTime.UtcNow;
            col.Update(cachedImage);

            using var ms = new MemoryStream(cachedImage.ImageData);
            return await CanvasBitmap.LoadAsync(canvasControl, ms.AsRandomAccessStream());
        }

        // File has been modified, remove the outdated cache entry
        col.Delete(cachedImage.Id);
        return null; // Not found or outdated in cache
    }

    public async Task PutInCache(string filePath, CanvasBitmap bitmap)
    {
        var col = _db.GetCollection<CachedImage>("images");

        // Get the last modified time of the file
        var lastModified = File.GetLastWriteTimeUtc(filePath).ToString("yyyyMMddHHmmss");

        // Check if the image is already cached and if the last modified time is the same
        var cachedImage = col.FindOne(x => x.FilePath == filePath);
        if (cachedImage != null)
        {
            if (cachedImage.LastModified == lastModified)
            {
                return; // Image is already cached and up-to-date
            }
            else
            {
                // File has been modified, remove the outdated cache entry
                col.Delete(cachedImage.Id);
            }
        }

        // Check item count and clean up if necessary
        if (col.Count() >= MaxItemCount)
        {
            Console.WriteLine("Cache item limit reached. Removing rarely used files.");
            RemoveRarelyUsed();
        }

        // Resize and convert image to JPEG using PhotoSauce
        var resizedImage = await ResizeImageWithPhotoSauce(bitmap, 800);

        // Create a new cache entry
        var newCachedImage = new CachedImage
        {
            FilePath = filePath,
            ImageData = resizedImage,
            LastAccessed = DateTime.UtcNow,
            LastModified = lastModified
        };

        col.Insert(newCachedImage);
    }

    public void RemoveRarelyUsed()
    {
        var col = _db.GetCollection<CachedImage>("images");

        // Get the total number of entries in the collection
        var totalEntries = col.Count();
        if (totalEntries == 0) return;

        // Calculate 25% of the total entries
        var entriesToRemove = (int)(totalEntries * 0.25);

        // Fetch the least used entries, ordered by LastAccessed, limited to 25% of total entries
        var rarelyUsedImages = col.Query()
                                  .OrderBy(x => x.LastAccessed)
                                  .Limit(entriesToRemove)
                                  .ToList();

        foreach (var image in rarelyUsedImages)
        {
            col.Delete(image.Id);
        }

        Console.WriteLine($"Removed {rarelyUsedImages.Count} rarely used cached images.");
    }

    private async Task<byte[]> ResizeImageWithPhotoSauce(CanvasBitmap bitmap, int maxSize)
    {
        using var ms = new MemoryStream();
        using var msOutput = new MemoryStream();
        // Save CanvasBitmap as PNG to memory stream
        await bitmap.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg);
        ms.Seek(0, SeekOrigin.Begin);

        var settings = new ProcessImageSettings
        {
            Width = maxSize,
            Height = maxSize,
            ResizeMode = CropScaleMode.Max,
            HybridMode = HybridScaleMode.Turbo
        };

        // Process the image using MagicScaler and save as JPEG
        MagicImageProcessor.ProcessImage(ms, msOutput, settings);

        // Convert the processed image to JPEG with quality setting
        return msOutput.ToArray();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _db?.Dispose();
            }
            _disposed = true;
        }
    }

    ~PhotoDiskCacher()
    {
        Dispose(false);
    }

    private class CachedImage
    {
        public int Id { get; set; }
        public string FilePath { get; set; }
        public byte[] ImageData { get; set; }
        public DateTime LastAccessed { get; set; }
        public string LastModified { get; set; } // New field for last modified date
    }
}
