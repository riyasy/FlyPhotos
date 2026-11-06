#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using FlyPhotos.Display.ImageReading;
using Microsoft.UI;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FlyPhotos.Services;

/// <summary>
/// A persistent, LRU-evicting disk cache for photo thumbnails, backed by SQLite.
/// Thumbnails are stored as resized JPEG blobs alongside their source file's
/// last-modified timestamp so stale entries are detected and replaced automatically.
/// <para>
/// This class is a thread-safe singleton. All database operations are serialised
/// through a <see cref="SemaphoreSlim" /> because every read also issues a Touch
/// update (<c>lastAccessed</c>), making true read-only concurrency impossible
/// without a batched-touch redesign.
/// </para>
/// <para>
/// Callers are responsible for disposing any <see cref="CanvasBitmap" /> returned
/// by <see cref="ReturnFromCache" />. Failure to do so will leak GPU memory.
/// </para>
/// </summary>
public sealed partial class DiskCacherWithSqlite : IDisposable
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    /// <summary>
    /// Maximum number of thumbnail entries kept in the database before LRU
    /// eviction is triggered on the next write.
    /// </summary>
    private const int MaxItemCount = 20_000;

    /// <summary>
    /// The longest edge (in pixels) that a stored thumbnail may have.
    /// Images smaller than this on both axes are stored at their original size.
    /// </summary>
    private const int ThumbMaxSize = 800;

    /// <summary>
    /// Fraction of <see cref="MaxItemCount" /> to evict in a single batch when
    /// the cache is full. 0.25 removes the 5,000 least-recently-accessed entries.
    /// </summary>
    private const double EvictionFactor = 0.25;

    // -------------------------------------------------------------------------
    // Singleton
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lazily initialised singleton holder. <see cref="Lazy{T}" /> guarantees
    /// thread-safe construction without an explicit lock.
    /// </summary>
    private static readonly Lazy<DiskCacherWithSqlite> _instance =
        new(() => new DiskCacherWithSqlite());

    /// <summary>
    /// Gets the singleton instance of <see cref="DiskCacherWithSqlite" />.
    /// The instance is created on first access.
    /// </summary>
    public static DiskCacherWithSqlite Instance => _instance.Value;

    // -------------------------------------------------------------------------
    // Prepared SQL commands (reused across calls for performance)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Retrieves <c>imageData</c>, <c>lastModified</c>, <c>actualWidth</c>, and
    /// <c>actualHeight</c> for a given <c>filePath</c>.
    /// Parameter: <c>$p</c> = filePath.
    /// </summary>
    private readonly SqliteCommand _cmdSelectByPath;

    /// <summary>
    /// Updates <c>lastAccessed</c> for a given <c>filePath</c>, keeping LRU
    /// ordering accurate after a cache hit.
    /// Parameters: <c>$a</c> = Unix timestamp, <c>$p</c> = filePath.
    /// </summary>
    private readonly SqliteCommand _cmdTouch;

    /// <summary>
    /// Deletes a single entry by <c>filePath</c>. Used to remove stale entries
    /// whose source file has been modified since the thumbnail was cached.
    /// Parameter: <c>$p</c> = filePath.
    /// </summary>
    private readonly SqliteCommand _cmdDeleteByPath;

    /// <summary>
    /// Inserts a new thumbnail or replaces an existing one (upsert).
    /// Parameters: <c>$p</c> filePath, <c>$d</c> imageData, <c>$a</c> lastAccessed,
    /// <c>$m</c> lastModified, <c>$w</c> actualWidth, <c>$h</c> actualHeight.
    /// The <c>$d</c> parameter uses <see cref="Microsoft.Data.Sqlite.SqliteParameter.Size" />
    /// to specify how many bytes of the rented buffer are valid, avoiding an
    /// extra copy into a trimmed array.
    /// </summary>
    private readonly SqliteCommand _cmdUpsert;

    /// <summary>
    /// Counts the total number of rows in the <c>images</c> table.
    /// Used at startup to initialise <see cref="_rowCount" />. Prefer the cached
    /// counter for subsequent checks.
    /// </summary>
    private readonly SqliteCommand _cmdCount;

    /// <summary>
    /// Deletes the <c>$limit</c> least-recently-accessed rows from the
    /// <c>images</c> table in a single statement.
    /// Parameter: <c>$limit</c> = number of rows to remove.
    /// The return value of <c>ExecuteNonQueryAsync</c> gives the actual number of
    /// rows deleted (which may be less than <c>$limit</c> if the table is smaller),
    /// and is used to keep <see cref="_rowCount" /> accurate.
    /// </summary>
    private readonly SqliteCommand _cmdEvictBatch;

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    /// <summary>
    /// The open SQLite connection shared by all prepared commands.
    /// Opened in <c>ReadWriteCreate</c> mode with shared-cache enabled.
    /// </summary>
    private readonly SqliteConnection _conn;

    /// <summary>
    /// Serialises all database operations to a single concurrent caller at a time.
    /// A plain mutex (maxCount 1) is used because every read path also issues a
    /// <c>Touch</c> write, preventing safe concurrent reads.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// In-memory cache of the current row count, initialised from the database on
    /// first use and kept in sync via increment/decrement on every mutation.
    /// Avoids a <c>COUNT(*)</c> full-scan on every write once the cache is warm.
    /// -1 indicates the value has not yet been loaded from the database.
    /// </summary>
    private int _rowCount = -1;

    /// <summary>
    /// Disposal flag. Set atomically via <see cref="Interlocked.Exchange(ref int, int)" /> to
    /// prevent double-disposal races on the singleton.
    /// 0 = live, 1 = disposed.
    /// </summary>
    private int _disposedFlag;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initialises the SQLite connection, creates the schema if absent, and
    /// prepares all reusable SQL commands.
    /// Private — use <see cref="Instance" /> to obtain the singleton.
    /// </summary>
    private DiskCacherWithSqlite()
    {
        var dbPath = Path.Combine(PathResolver.GetDbFolderPath(), "FlyPhotosCache_sqlite_2.db");

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

        _cmdSelectByPath = _conn.CreateCommand();
        _cmdSelectByPath.CommandText =
            "SELECT imageData, lastModified, actualWidth, actualHeight FROM images WHERE filePath = $p";
        _cmdSelectByPath.Parameters.Add("$p", SqliteType.Text);

        _cmdTouch = _conn.CreateCommand();
        _cmdTouch.CommandText =
            "UPDATE images SET lastAccessed = $a WHERE filePath = $p";
        _cmdTouch.Parameters.Add("$a", SqliteType.Integer);
        _cmdTouch.Parameters.Add("$p", SqliteType.Text);

        _cmdDeleteByPath = _conn.CreateCommand();
        _cmdDeleteByPath.CommandText =
            "DELETE FROM images WHERE filePath = $p";
        _cmdDeleteByPath.Parameters.Add("$p", SqliteType.Text);

        _cmdUpsert = _conn.CreateCommand();
        _cmdUpsert.CommandText = @"
            INSERT INTO images (filePath, imageData, lastAccessed, lastModified, actualWidth, actualHeight)
            VALUES ($p, $d, $a, $m, $w, $h)
            ON CONFLICT(filePath) DO UPDATE SET
                imageData    = excluded.imageData,
                lastAccessed = excluded.lastAccessed,
                lastModified = excluded.lastModified,
                actualWidth  = excluded.actualWidth,
                actualHeight = excluded.actualHeight;";
        _cmdUpsert.Parameters.Add("$p", SqliteType.Text);
        _cmdUpsert.Parameters.Add("$d", SqliteType.Blob);
        _cmdUpsert.Parameters.Add("$a", SqliteType.Integer);
        _cmdUpsert.Parameters.Add("$m", SqliteType.Text);
        _cmdUpsert.Parameters.Add("$w", SqliteType.Integer);
        _cmdUpsert.Parameters.Add("$h", SqliteType.Integer);

        _cmdCount = _conn.CreateCommand();
        _cmdCount.CommandText = "SELECT COUNT(*) FROM images";

        _cmdEvictBatch = _conn.CreateCommand();
        _cmdEvictBatch.CommandText = @"
            WITH c AS (SELECT filePath FROM images ORDER BY lastAccessed ASC LIMIT $limit)
            DELETE FROM images WHERE filePath IN (SELECT filePath FROM c);";
        _cmdEvictBatch.Parameters.Add("$limit", SqliteType.Integer);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Safely disposes the singleton ONLY if it has already been instantiated.
    /// Call this on application shutdown. Accessing <see cref="Instance" /> directly
    /// to dispose would force creation of the singleton if it does not yet exist.
    /// </summary>
    public static void Shutdown()
    {
        if (_instance.IsValueCreated)
            ((IDisposable)_instance.Value).Dispose();
    }

    /// <summary>
    /// Attempts to retrieve a cached thumbnail for <paramref name="filePath" />.
    /// Returns the decoded bitmap and the original image dimensions on a cache hit,
    /// or <c>(null, 0, 0)</c> on a miss, a stale entry, or any internal error.
    /// <para>
    /// Freshness is determined by comparing the file's current last-write time
    /// against the value stored when the thumbnail was cached. A mismatch causes
    /// the stale entry to be deleted so it is regenerated on the next
    /// <see cref="PutInCache" /> call.
    /// </para>
    /// <para>
    /// <b>Important:</b> the caller owns the returned <see cref="CanvasBitmap" />
    /// and must dispose it when it is no longer needed to release GPU memory.
    /// </para>
    /// </summary>
    /// <param name="canvasControl">
    /// The <see cref="CanvasControl" /> used as the device context for decoding
    /// the stored JPEG into a GPU-resident bitmap.
    /// </param>
    /// <param name="filePath">Absolute path of the source photo file.</param>
    /// <returns>
    /// A tuple of <c>(bitmap, actualWidth, actualHeight)</c> where
    /// <c>actualWidth</c> and <c>actualHeight</c> are the dimensions of the
    /// original (un-resized) image. Returns <c>(null, 0, 0)</c> on miss or error.
    /// </returns>
    public async Task<(CanvasBitmap? bitmap, int actualWidth, int actualHeight)> ReturnFromCache(
        ICanvasResourceCreatorWithDpi canvasControl, string filePath)
    {
        try
        {
            var currentMtime = FileMtimeString(filePath);
            byte[]? imageData = null;
            int dataLength = 0;
            int actualWidth = 0, actualHeight = 0;

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
                        // Cache hit: read blob length, then rent a pooled buffer to
                        // avoid a large heap allocation (thumbnails often exceed the
                        // ~85 KB Large Object Heap threshold).
                        dataLength = (int)reader.GetBytes(0, 0, null, 0, int.MaxValue);
                        imageData = ArrayPool<byte>.Shared.Rent(dataLength);
                        reader.GetBytes(0, 0, imageData, 0, dataLength);

                        actualWidth = reader.GetInt32(2);
                        actualHeight = reader.GetInt32(3);

                        _cmdTouch.Parameters["$a"].Value = NowUnix();
                        _cmdTouch.Parameters["$p"].Value = filePath;
                        await _cmdTouch.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        // Stale entry: remove it so PutInCache regenerates it.
                        _cmdDeleteByPath.Parameters["$p"].Value = filePath;
                        await _cmdDeleteByPath.ExecuteNonQueryAsync().ConfigureAwait(false);

                        // Keep _rowCount consistent with the deletion.
                        if (_rowCount > 0) _rowCount--;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            if (imageData != null)
                try
                {
                    // Decode outside the lock: CanvasBitmap.LoadAsync is the
                    // dominant cost here and does not touch the database.
                    // Wrap the rented segment — MemoryStream over an existing
                    // buffer does not copy; it is a free view.
                    using var ms = new MemoryStream(imageData, 0, dataLength, false);
                    var bitmap = await CanvasBitmap.LoadAsync(canvasControl, ms.AsRandomAccessStream());
                    return (bitmap, actualWidth, actualHeight);
                }
                finally
                {
                    // Always return the rented buffer regardless of decode outcome.
                    ArrayPool<byte>.Shared.Return(imageData);
                }

            return (null, 0, 0);
        }
        catch (Exception ex)
        {
            // Cache failures must never crash the app — the caller will simply
            // regenerate the thumbnail from the source file.
            Debug.WriteLine(
                $"[CACHE-ERROR] Failed to read '{filePath}' from cache: {ex.Message}");
            return (null, 0, 0);
        }
    }

    /// <summary>
    /// Stores a resized JPEG thumbnail for <paramref name="filePath" /> in the
    /// cache. If an entry for the same path already exists it is replaced (upsert).
    /// <para>
    /// The image is resized on the GPU before writing so that stored blobs
    /// never exceed <see cref="ThumbMaxSize" /> pixels on their longest edge.
    /// Images already within that bound are stored at their original size.
    /// </para>
    /// <para>
    /// When the cache reaches <see cref="MaxItemCount" /> entries, the oldest
    /// <see cref="EvictionFactor" /> fraction is removed before the new entry is
    /// inserted.
    /// </para>
    /// </summary>
    /// <param name="filePath">Absolute path of the source photo file.</param>
    /// <param name="bitmap">
    /// The full-resolution (or already-decoded) bitmap to thumbnail and store.
    /// The bitmap is not disposed by this method — the caller retains ownership.
    /// </param>
    /// <param name="actualWidth">Width of the original image in pixels.</param>
    /// <param name="actualHeight">Height of the original image in pixels.</param>
    /// <param name="rotation">Clockwise rotation in degrees (0, 90, 180, 270) to apply to the JPEG before caching.
    /// Pass 0 (default) to store without rotation.</param>
    public async Task PutInCache(string filePath, CanvasBitmap bitmap, int actualWidth, int actualHeight, int rotation = 0)
    {
        try
        {
            // Resize and encode outside the lock — this is CPU/IO-intensive.
            using var encoded = await EncodeForCache(bitmap, ThumbMaxSize, rotation).ConfigureAwait(false);

            var lastMod = FileMtimeString(filePath);
            var now = NowUnix();

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureRowCountLoadedAsync().ConfigureAwait(false);

                if (_rowCount >= MaxItemCount)
                {
                    var toRemove = (int)Math.Max(1, _rowCount * EvictionFactor);
                    _cmdEvictBatch.Parameters["$limit"].Value = toRemove;
                    var rowsDeleted = await _cmdEvictBatch.ExecuteNonQueryAsync().ConfigureAwait(false);
                    _rowCount -= rowsDeleted;
                }

                // Value is the stream's whole buffer; Size tells Microsoft.Data.Sqlite
                // how many bytes are valid. ArraySegment<byte> is not in the official
                // type mapping table and does not bind as a BLOB — byte[] + Size is
                // the documented approach.
                _cmdUpsert.Parameters["$p"].Value = filePath;
                _cmdUpsert.Parameters["$d"].Value = encoded.GetBuffer();
                _cmdUpsert.Parameters["$d"].Size = (int)encoded.Length;
                _cmdUpsert.Parameters["$a"].Value = now;
                _cmdUpsert.Parameters["$m"].Value = lastMod;
                _cmdUpsert.Parameters["$w"].Value = actualWidth;
                _cmdUpsert.Parameters["$h"].Value = actualHeight;
                await _cmdUpsert.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Upsert may replace an existing row (count stays the same) or
                // add a new one (count increases). SQLite's changes() would tell
                // us which, but the simpler conservative approach is to clamp at
                // MaxItemCount rather than track exact deltas here.
                if (_rowCount < MaxItemCount) _rowCount++;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Cache write failures are non-fatal — the thumbnail will simply be
            // regenerated on the next session.
            Debug.WriteLine(
                $"[CACHE-ERROR] Failed to put '{filePath}' in cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes every entry from the disk cache and resets the in-memory row counter.
    /// Safe to call at any time; any error is swallowed and logged so the caller
    /// is never disrupted.
    /// </summary>
    public async Task ClearAllAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM images";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                _rowCount = 0;
            }
            finally
            {
                _gate.Release();
            }

            // VACUUM must run outside any transaction (and therefore outside the gate).
            // It rewrites the entire database file, reclaiming the space freed by DELETE.
            await using var vacuumCmd = _conn.CreateCommand();
            vacuumCmd.CommandText = "VACUUM";
            await vacuumCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CACHE-ERROR] Failed to clear all cache entries: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Releases all managed resources held by this instance: prepared SQL
    /// commands, the database connection, and the semaphore.
    /// <para>
    /// Use <see cref="Shutdown" /> rather than calling this directly on the
    /// singleton, to avoid accidentally instantiating it during teardown.
    /// </para>
    /// </summary>
    void IDisposable.Dispose()
    {
        // Interlocked.Exchange ensures only one thread wins the disposal race,
        // preventing double-free on the underlying unmanaged SQLite handles.
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        _gate.Wait();
        try
        {
            _cmdSelectByPath.Dispose();
            _cmdUpsert.Dispose();
            _cmdCount.Dispose();
            _cmdTouch.Dispose();
            _cmdDeleteByPath.Dispose();
            _cmdEvictBatch.Dispose();
            _conn.Close();
            _conn.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates the <c>images</c> table and its index if they do not already
    /// exist, and configures SQLite performance pragmas for the connection.
    /// <list type="bullet">
    /// <item><c>WAL</c> — reduces write-lock contention.</item>
    /// <item>
    /// <c>synchronous=OFF</c> — safe for a regenerable cache; removes
    /// the fsync overhead on every write. A power-loss can corrupt the DB,
    /// but the cache is fully disposable and will be recreated.
    /// </item>
    /// <item><c>temp_store=MEMORY</c> — keeps temp tables in RAM.</item>
    /// <item>
    /// <c>mmap_size</c> — maps up to 256 MB of the DB file into the
    /// process address space for faster sequential reads.
    /// </item>
    /// </list>
    /// </summary>
    private void InitializeDatabase()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=268435456;
            CREATE TABLE IF NOT EXISTS images (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                filePath     TEXT    NOT NULL UNIQUE,
                imageData    BLOB    NOT NULL,
                lastAccessed INTEGER NOT NULL,
                lastModified TEXT    NOT NULL,
                actualWidth  INTEGER NOT NULL,
                actualHeight INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_lastAccessed ON images(lastAccessed);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads the current row count from the database into <see cref="_rowCount" />
    /// if it has not yet been initialised (i.e. is still <c>-1</c>).
    /// Must be called inside the <see cref="_gate" /> lock.
    /// </summary>
    private async Task EnsureRowCountLoadedAsync()
    {
        if (_rowCount < 0)
            _rowCount = Convert.ToInt32(
                await _cmdCount.ExecuteScalarAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// Returns the current UTC time as a Unix epoch timestamp (seconds).
    /// Used to record <c>lastAccessed</c> values in the database.
    /// </summary>
    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Returns the last-write time of <paramref name="path" /> as a compact
    /// UTC string in the format <c>yyyyMMddHHmmss</c>.
    /// <para>
    /// This string is stored with each cached thumbnail and compared on every
    /// cache read to detect whether the source file has been modified since the
    /// thumbnail was generated.
    /// </para>
    /// </summary>
    /// <param name="path">Absolute path of the file to inspect.</param>
    private static string FileMtimeString(string path) => 
        File.GetLastWriteTimeUtc(path).ToString("yyyyMMddHHmmss");

    /// <summary>
    /// Encodes <paramref name="bitmap"/> as the cached JPEG: scaled to fit <paramref name="maxSize"/> and with
    /// <paramref name="rotation"/> baked into the pixels, so the stored image needs no orientation tag.
    /// </summary>
    /// <remarks>
    /// A bitmap that already fits and needs no rotation is saved as it is. Otherwise the GPU draws it scaled and
    /// rotated into a render target of the final size, which is saved once: the only GPU readback is the small
    /// result, and there is no intermediate JPEG. Measured 3× faster than the MagicScaler pipeline it replaced
    /// (save to JPEG, decode, resize and rotate, encode again), with the same output size and orientation.
    /// </remarks>
    /// <param name="maxSize">The longest edge allowed, in pixels.</param>
    /// <param name="rotation">Clockwise rotation in degrees: 0, 90, 180 or 270.</param>
    /// <returns>The JPEG; its valid bytes are <c>GetBuffer()[..Length]</c>.</returns>
    private static async Task<MemoryStream> EncodeForCache(CanvasBitmap bitmap, int maxSize, int rotation)
    {
        var output = new MemoryStream();
        try
        {
            var px = bitmap.SizeInPixels;
            if (px.Width <= maxSize && px.Height <= maxSize && rotation == 0)
            {
                await bitmap.SaveAsync(output.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, 0.9f);
                return output;
            }

            bool quarterTurn = rotation is 90 or 270;
            var (width, height) = CappedDecode.Fit(quarterTurn ? px.Height : px.Width,
                quarterTurn ? px.Width : px.Height, (uint)maxSize);

            // The bitmap draws at its size in DIPs; the 96-dpi target's DIPs are pixels.
            float w = (float)bitmap.Size.Width, h = (float)bitmap.Size.Height;
            var rotate = rotation switch
            {
                90 => Matrix3x2.CreateRotation(MathF.PI / 2) * Matrix3x2.CreateTranslation(h, 0),
                180 => Matrix3x2.CreateRotation(MathF.PI) * Matrix3x2.CreateTranslation(w, h),
                270 => Matrix3x2.CreateRotation(3 * MathF.PI / 2) * Matrix3x2.CreateTranslation(0, w),
                _ => Matrix3x2.Identity
            };

            using var target = new CanvasRenderTarget(bitmap.Device, width, height, 96);
            // Clamped edges: otherwise the filter samples transparency outside the bitmap and the outermost
            // rows and columns come out darker (black, once JPEG drops the alpha).
            using var source = new BorderEffect
            {
                Source = bitmap, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp
            };
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                // Each axis scaled to fill the target exactly: Fit rounds the short side, so one shared factor
                // can leave the last row or column only partly covered.
                ds.Transform = rotate * Matrix3x2.CreateScale(width / (quarterTurn ? h : w), height / (quarterTurn ? w : h));
                ds.DrawImage(source, 0, 0, bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
            }
            await target.SaveAsync(output.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg, 0.85f);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }
}