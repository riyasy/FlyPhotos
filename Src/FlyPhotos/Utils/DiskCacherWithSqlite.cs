//using Microsoft.Data.Sqlite;
//using Microsoft.Graphics.Canvas;
//using Microsoft.Graphics.Canvas.UI.Xaml;
//using PhotoSauce.MagicScaler;
//using System;
//using System.IO;
//using System.Reflection;
//using System.Threading.Tasks;

//namespace FlyPhotos.Utils
//{
//    public sealed class DiskCacherWithSqlite : IDisposable
//    {
//        private static readonly Lazy<DiskCacherWithSqlite> _instance = new(() => new DiskCacherWithSqlite());
//        private readonly SqliteConnection _connection;
//        private bool _disposed;

//        private const int MaxItemCount = 20000; // Maximum number of items in the cache

//        private DiskCacherWithSqlite()
//        {
//            var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "FlyPhotos";
//            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName, "FlyPhotosCache_sqlite.db");
//            _connection = new SqliteConnection($"Data Source={dbPath}");
//            _connection.Open();
//            InitializeDatabase();
//        }

//        public static DiskCacherWithSqlite Instance => _instance.Value;

//        private void InitializeDatabase()
//        {
//            using var cmd = _connection.CreateCommand();
//            cmd.CommandText = @"
//                CREATE TABLE IF NOT EXISTS images (
//                    id INTEGER PRIMARY KEY AUTOINCREMENT,
//                    filePath TEXT NOT NULL UNIQUE,
//                    imageData BLOB NOT NULL,
//                    lastAccessed TEXT NOT NULL,
//                    lastModified TEXT NOT NULL
//                );
//                CREATE INDEX IF NOT EXISTS idx_filePath ON images(filePath);
//                CREATE INDEX IF NOT EXISTS idx_lastAccessed ON images(lastAccessed);
//            ";
//            cmd.ExecuteNonQuery();
//        }

//        public async Task<CanvasBitmap> ReturnFromCache(CanvasControl canvasControl, string filePath)
//        {
//            using var cmd = _connection.CreateCommand();
//            cmd.CommandText = "SELECT imageData, lastModified FROM images WHERE filePath = @filePath";
//            cmd.Parameters.AddWithValue("@filePath", filePath);

//            using var reader = cmd.ExecuteReader();
//            if (!reader.Read())
//                return null;

//            var imageData = (byte[])reader["imageData"];
//            var cachedLastModified = (string)reader["lastModified"];

//            var lastModified = File.GetLastWriteTimeUtc(filePath).ToString("yyyyMMddHHmmss");

//            if (cachedLastModified == lastModified)
//            {
//                // Update lastAccessed
//                using var updateCmd = _connection.CreateCommand();
//                updateCmd.CommandText = "UPDATE images SET lastAccessed = @lastAccessed WHERE filePath = @filePath";
//                updateCmd.Parameters.AddWithValue("@lastAccessed", DateTime.UtcNow.ToString("o"));
//                updateCmd.Parameters.AddWithValue("@filePath", filePath);
//                updateCmd.ExecuteNonQuery();

//                using var ms = new MemoryStream(imageData);
//                return await CanvasBitmap.LoadAsync(canvasControl, ms.AsRandomAccessStream());
//            }
//            else
//            {
//                // Outdated entry
//                using var deleteCmd = _connection.CreateCommand();
//                deleteCmd.CommandText = "DELETE FROM images WHERE filePath = @filePath";
//                deleteCmd.Parameters.AddWithValue("@filePath", filePath);
//                deleteCmd.ExecuteNonQuery();

//                return null;
//            }
//        }

//        public async Task PutInCache(string filePath, CanvasBitmap bitmap)
//        {
//            var lastModified = File.GetLastWriteTimeUtc(filePath).ToString("yyyyMMddHHmmss");

//            // Check item count and cleanup if needed
//            using (var countCmd = _connection.CreateCommand())
//            {
//                countCmd.CommandText = "SELECT COUNT(*) FROM images";
//                var count = Convert.ToInt32(countCmd.ExecuteScalar());
//                if (count >= MaxItemCount)
//                {
//                    RemoveRarelyUsed();
//                }
//            }

//            var resizedImage = await ResizeImageWithPhotoSauce(bitmap, 800);

//            using var cmd = _connection.CreateCommand();
//            cmd.CommandText = @"
//                INSERT INTO images (filePath, imageData, lastAccessed, lastModified)
//                VALUES (@filePath, @imageData, @lastAccessed, @lastModified)
//                ON CONFLICT(filePath) DO UPDATE SET
//                    imageData = excluded.imageData,
//                    lastAccessed = excluded.lastAccessed,
//                    lastModified = excluded.lastModified;
//            ";
//            cmd.Parameters.AddWithValue("@filePath", filePath);
//            cmd.Parameters.AddWithValue("@imageData", resizedImage);
//            cmd.Parameters.AddWithValue("@lastAccessed", DateTime.UtcNow.ToString("o"));
//            cmd.Parameters.AddWithValue("@lastModified", lastModified);
//            cmd.ExecuteNonQuery();
//        }

//        public void RemoveRarelyUsed()
//        {
//            using var cmd = _connection.CreateCommand();
//            cmd.CommandText = "SELECT COUNT(*) FROM images";
//            var totalEntries = Convert.ToInt32(cmd.ExecuteScalar());
//            if (totalEntries == 0) return;

//            var entriesToRemove = (int)(totalEntries * 0.25);

//            using var selectCmd = _connection.CreateCommand();
//            selectCmd.CommandText = "SELECT filePath FROM images ORDER BY lastAccessed ASC LIMIT @limit";
//            selectCmd.Parameters.AddWithValue("@limit", entriesToRemove);

//            using var reader = selectCmd.ExecuteReader();
//            var toDelete = new System.Collections.Generic.List<string>();
//            while (reader.Read())
//                toDelete.Add(reader.GetString(0));

//            foreach (var filePath in toDelete)
//            {
//                using var deleteCmd = _connection.CreateCommand();
//                deleteCmd.CommandText = "DELETE FROM images WHERE filePath = @filePath";
//                deleteCmd.Parameters.AddWithValue("@filePath", filePath);
//                deleteCmd.ExecuteNonQuery();
//            }
//        }

//        private async Task<byte[]> ResizeImageWithPhotoSauce(CanvasBitmap bitmap, int maxSize)
//        {
//            using var ms = new MemoryStream();
//            using var msOutput = new MemoryStream();
//            await bitmap.SaveAsync(ms.AsRandomAccessStream(), CanvasBitmapFileFormat.Jpeg);
//            ms.Seek(0, SeekOrigin.Begin);

//            var settings = new ProcessImageSettings
//            {
//                Width = maxSize,
//                Height = maxSize,
//                ResizeMode = CropScaleMode.Max,
//                HybridMode = HybridScaleMode.Turbo
//            };

//            MagicImageProcessor.ProcessImage(ms, msOutput, settings);
//            return msOutput.ToArray();
//        }

//        public void Dispose()
//        {
//            Dispose(true);
//            GC.SuppressFinalize(this);
//        }

//        private void Dispose(bool disposing)
//        {
//            if (!_disposed)
//            {
//                if (disposing)
//                {
//                    _connection?.Dispose();
//                }
//                _disposed = true;
//            }
//        }

//        ~DiskCacherWithSqlite()
//        {
//            Dispose(false);
//        }
//    }
//}
