using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FlyPhotos.Utils
{
    internal class ReaderUtil
    {
        public static async Task<byte[]> GetInMemByteArray(IRandomAccessStream memStream)
        {
            memStream.Seek(0);
            var bytes = new byte[memStream.Size];
            await memStream.ReadAsync(bytes.AsBuffer(), (uint)memStream.Size, InputStreamOptions.None);
            return bytes;
        }

        public static async Task<IRandomAccessStream> GetWin2DPerformantStream(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            
            if (ShouldBufferFile(path))
            {
                // UNC/Network: Buffer to native RAM to release file lock immediately
                var memStream = new InMemoryRandomAccessStream();
                using var fileStream = await file.OpenAsync(FileAccessMode.Read);
                await RandomAccessStream.CopyAsync(fileStream, memStream);
                memStream.Seek(0);
                return memStream;
            }
            else
            {
                // Local: Stream directly to save RAM. 
                // StorageFile.OpenAsync returns a native stream which is fast for Win2D.
                return await file.OpenAsync(FileAccessMode.Read);
            }
        }

        /// <summary>
        /// Determines if a file should be buffered into RAM.
        /// Returns true for UNC paths (\\server) and Mapped Network Drives (Z:\).
        /// </summary>
        private static bool ShouldBufferFile(string path)
        {
            // 1. Check for explicit UNC paths
            if (path.StartsWith(@"\\")) return true;

            try
            {
                // 2. Check for Mapped Network Drives
                var root = Path.GetPathRoot(path);

                // If root is null or empty, we can't check drive type, assume local.
                if (string.IsNullOrEmpty(root)) return false;

                // Check if it is a Network drive
                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Network) return true;

                // Optional: Buffer "Removable" drives (USB sticks) too?
                // WIC can be slow on USB 2.0, buffering helps performance there too.
                // if (drive.DriveType == DriveType.Removable) return true;
            }
            catch
            {
                // If DriveInfo fails (e.g. disconnected drive, weird path), 
                // strictly speaking, buffering is "Safe" but consumes RAM. 
                // Assuming "Local" ensures we don't crash, but might lock.
                return false;
            }
            return false;
        }
    }
}
