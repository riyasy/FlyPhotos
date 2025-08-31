#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using PhotoSauce.MagicScaler;

namespace FlyPhotos.Utils;

public sealed class DiskCacherWithSqlite : IDisposable
{
    private const int MaxItemCount = 20_000;
    private const int ThumbMaxSize = 800;
    private const double EvictionFactor = 0.25; // Evict 25% of items when full
    private static readonly Lazy<DiskCacherWithSqlite> _instance = new(() => new DiskCacherWithSqlite());
    private readonly SqliteCommand _cmdCount;
    private readonly SqliteCommand _cmdDeleteByPath;
    private readonly SqliteCommand _cmdEvictBatch;

    // Prepared commands
    private readonly SqliteCommand _cmdSelectByPath;
    private readonly SqliteCommand _cmdTouch;
    private readonly SqliteCommand _cmdUpsert;
    private readonly SqliteConnection _conn;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private DiskCacherWithSqlite()
    {
        var dbPath = Path.Combine(PathResolver.GetDbFolderPath(), "FlyPhotosCache_sqlite.db");

        // Ensure folder exists
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        _conn = new SqliteConnection(csb.ToString());
        _conn.Open();

        InitializeDatabase();

        // Prepare all statements once for performance
        _cmdSelectByPath = _conn.CreateCommand();
        _cmdSelectByPath.CommandText = "SELECT imageData, lastModified FROM images WHERE filePath = $p";
        _cmdSelectByPath.Parameters.Add("$p", SqliteType.Text);

        _cmdTouch = _conn.CreateCommand();
        _cmdTouch.CommandText = "UPDATE images SET lastAccessed = $a WHERE filePath = $p";
        _cmdTouch.Parameters.Add("$a", SqliteType.Integer);
        _cmdTouch.Parameters.Add("$p", SqliteType.Text);

        _cmdDeleteByPath = _conn.CreateCommand();
        _cmdDeleteByPath.CommandText = "DELETE FROM images WHERE filePath = $p";
        _cmdDeleteByPath.Parameters.Add("$p", SqliteType.Text);

        _cmdUpsert = _conn.CreateCommand();
        _cmdUpsert.CommandText = @"
            INSERT INTO images (filePath, imageData, lastAccessed, lastModified)
            VALUES ($p, $d, $a, $m)
            ON CONFLICT(filePath) DO UPDATE SET
                imageData    = excluded.imageData,
                lastAccessed = excluded.lastAccessed,
                lastModified = excluded.lastModified;";
        _cmdUpsert.Parameters.Add("$p", SqliteType.Text);
        _cmdUpsert.Parameters.Add("$d", SqliteType.Blob);
        _cmdUpsert.Parameters.Add("$a", SqliteType.Integer);
        _cmdUpsert.Parameters.Add("$m", SqliteType.Text);

        _cmdCount = _conn.CreateCommand();
        _cmdCount.CommandText = "SELECT COUNT(*) FROM images";

        _cmdEvictBatch = _conn.CreateCommand();
        _cmdEvictBatch.CommandText = @"
            WITH c AS (SELECT filePath FROM images ORDER BY lastAccessed ASC LIMIT $limit)
            DELETE FROM images WHERE filePath IN (SELECT filePath FROM c);";
        _cmdEvictBatch.Parameters.Add("$limit", SqliteType.Integer);
    }

    public static DiskCacherWithSqlite Instance => _instance.Value;

    /// <summary>
    /// Safely disposes of the singleton instance ONLY if it has been created.
    /// This should be called on application shutdown.
    /// If we call Dispose directly on the Instance property, it will create the instance if it doesn't exist yet.
    /// </summary>
    public static void Shutdown()
    {
        if (_instance.IsValueCreated)
        {
            ((IDisposable)_instance.Value).Dispose();
        }
    }

    void IDisposable.Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _gate.Wait(); // Wait for any ongoing operation to complete
        try
        {
            // Dispose all managed resources
            _cmdSelectByPath?.Dispose();
            _cmdUpsert?.Dispose();
            _cmdCount?.Dispose();
            _cmdTouch?.Dispose();
            _cmdDeleteByPath?.Dispose();
            _cmdEvictBatch?.Dispose();
            _conn?.Close(); // Close explicitly before disposing
            _conn?.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static long NowUnix()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string FileMtimeString(string path)
    {
        return File.GetLastWriteTimeUtc(path).ToString("yyyyMMddHHmmss");
    }

    private void InitializeDatabase()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            CREATE TABLE IF NOT EXISTS images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                filePath     TEXT    NOT NULL UNIQUE,
                imageData    BLOB    NOT NULL,
                lastAccessed INTEGER NOT NULL,
                lastModified TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_lastAccessed ON images(lastAccessed);";
        cmd.ExecuteNonQuery();
    }

    public async Task<CanvasBitmap?> ReturnFromCache(CanvasControl canvasControl, string filePath)
    {
        return null;
        try
        {
            // Perform file I/O outside the database lock to improve concurrency
            var currentMtime = FileMtimeString(filePath);
            byte[]? imageData = null;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _cmdSelectByPath.Parameters["$p"].Value = filePath;
                await using var reader = await _cmdSelectByPath.ExecuteReaderAsync().ConfigureAwait(false);

                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var cachedLastModified = reader.GetString(1);
                    if (cachedLastModified == currentMtime)
                    {
                        // Fresh item: read data and update access time
                        var blob = new byte[reader.GetBytes(0, 0, null, 0, int.MaxValue)];
                        reader.GetBytes(0, 0, blob, 0, blob.Length);
                        imageData = blob;

                        _cmdTouch.Parameters["$a"].Value = NowUnix();
                        _cmdTouch.Parameters["$p"].Value = filePath;
                        await _cmdTouch.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        // Stale item: delete it
                        _cmdDeleteByPath.Parameters["$p"].Value = filePath;
                        await _cmdDeleteByPath.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            // Decode the image outside the lock
            if (imageData != null)
            {
                using var ms = new MemoryStream(imageData, false);
                return await CanvasBitmap.LoadAsync(canvasControl, ms.AsRandomAccessStream());
            }

            return null;
        }
        catch (Exception ex)
        {
            // Cache failures should not crash the app. Log and return null.
            System.Diagnostics.Debug.WriteLine($"[CACHE-ERROR] Failed to read '{filePath}' from cache: {ex.Message}");
            return null;
        }
    }

    public async Task PutInCache(string filePath, CanvasBitmap bitmap)
    {
        try
        {
            // Perform CPU/IO-intensive work outside the database lock
            var resizedImageData = await ResizeImageWithPhotoSauce(bitmap, ThumbMaxSize).ConfigureAwait(false);
            var lastMod = FileMtimeString(filePath);
            var now = NowUnix();

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Check if eviction is needed
                var total = Convert.ToInt32(await _cmdCount.ExecuteScalarAsync().ConfigureAwait(false));
                if (total >= MaxItemCount)
                {
                    var toRemove = (int)Math.Max(1, total * EvictionFactor);
                    _cmdEvictBatch.Parameters["$limit"].Value = toRemove;
                    await _cmdEvictBatch.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // Upsert the new/updated item
                _cmdUpsert.Parameters["$p"].Value = filePath;
                _cmdUpsert.Parameters["$d"].Value = resizedImageData;
                _cmdUpsert.Parameters["$a"].Value = now;
                _cmdUpsert.Parameters["$m"].Value = lastMod;
                await _cmdUpsert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Cache failures should not crash the app. Log and continue.
            System.Diagnostics.Debug.WriteLine($"[CACHE-ERROR] Failed to put '{filePath}' in cache: {ex.Message}");
        }
    }

    // TODO - Check if copying to double encoding is necessary to convert canvas bitmap pixels.
    // currently we are saving canvas to memory stream as jpeg and again scaling to another memory stream
    // check if we can use canvas pixels directly to be given to magic scaler.
    private static async Task<byte[]> ResizeImageWithPhotoSauce(CanvasBitmap bitmap, int maxSize)
    {
        using var input = new MemoryStream();
        // Save as PNG to preserve quality during the intermediate step, or JPEG if speed is critical
        await bitmap.SaveAsync(input.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, 0.9f);
        input.Position = 0;

        using var output = new MemoryStream();
        var settings = new ProcessImageSettings
        {
            Width = maxSize,
            Height = maxSize,
            ResizeMode = CropScaleMode.Max,
            HybridMode = HybridScaleMode.Turbo,
            EncoderOptions = new JpegEncoderOptions { Quality = 85, Subsample = ChromaSubsampleMode.Subsample420 }
        };

        MagicImageProcessor.ProcessImage(input, output, settings);
        return output.ToArray();
    }
}