using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
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

            // Check if path is UNC (starts with double backslash)
            if (path.StartsWith(@"\\"))
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
    }
}
