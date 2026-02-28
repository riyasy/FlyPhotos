// =============================================================================
// StorageOps — Shell-Level File Access Strategy
// =============================================================================
//
// WHY GetWin2DPerformantStream WAS REWRITTEN (2026-02)
// -----------------------------------------------------
// The original implementation used StorageFile.GetFileFromPathAsync() to open
// all files. This routes through the Windows Storage Broker (an RPC layer),
// which enforces access policy and rejects hidden and system files (e.g. files
// with FILE_ATTRIBUTE_HIDDEN or FILE_ATTRIBUTE_SYSTEM set), causing crashes for
// a subset of users browsing system-managed folders.
//
// The replacement calls CreateRandomAccessStreamOnFile() from shcore.dll
// directly (bypassing the Storage Broker) and returns a native
// IRandomAccessStream. Win2D and WIC receive the same native stream type as
// before, so decoding performance is identical.
//
// WHY System.IO.FileStream WAS REJECTED FOR LOCAL FILES
// ------------------------------------------------------
// The obvious fix — open files with System.IO.FileStream — was benchmarked and
// caused a severe regression for large images (e.g. 30 MB / 6000×4000 PNG):
//
//   StorageFile  (WinRT native stream) : ~30 ms – 400 ms
//   FileStream   (.NET managed stream) : ~2000 ms+
//
// Root cause: Win2D delegates decoding to WIC (Windows Imaging Component), a
// native C++ library that performs thousands of small Seek/Read operations.
// Passing a .NET FileStream requires the .AsRandomAccessStream() adapter, which
// marshals every one of those tiny reads across the managed/native boundary.
// The accumulated context-switch cost is enormous for large, complex formats.
//
// HYBRID STRATEGY: GetWin2DPerformantStream
// -----------------------------------------
//   Local files   — shcore.dll CreateRandomAccessStreamOnFile()
//                   Returns a native IRandomAccessStream. Zero-copy, zero
//                   adapter overhead. Hidden/system file access works.
//
//   Network / UNC — System.IO.FileStream → InMemoryRandomAccessStream
//                   The file is fully buffered into native RAM and the file
//                   handle is closed immediately. This prevents WIC from
//                   holding the network file lock and blocking deletion.
//                   The adapter cost only applies during the one-time copy,
//                   not during decoding (WIC decodes from the in-memory stream).
//
// CLIPBOARD COPY STRATEGY: GetStorageFileOrVirtualFile
// -----------------------------------------------------
// The WinRT clipboard API (DataPackage.SetStorageItems) requires a StorageFile
// object, which normally demands GetFileFromPathAsync — hitting the Broker.
//
//   Normal files  — GetFileFromPathAsync succeeds → real StorageFile in clipboard.
//
//   Hidden/System — GetFileFromPathAsync throws → fallback to
//                   StorageFile.CreateStreamedFileAsync. This creates a *virtual*
//                   StorageFile backed by a lazy callback. The callback only fires
//                   when the user pastes in Explorer, at which point a plain
//                   FileStream reads the file, bypassing the broker entirely.
//
// KEY INSIGHT (bitmap clipboard): RandomAccessStreamReference.CreateFromStream()
//   does NOT copy the bitmap eagerly at SetContent time. The clipboard reads
//   the stream lazily on paste. The InMemoryRandomAccessStream backing the bitmap
//   must therefore NOT be disposed until the clipboard is replaced or cleared.
//
// DELETE STRATEGY: DeleteFileFromDisk
// ------------------------------------
// Sending a file to the Recycle Bin via StorageFile.DeleteAsync(Default) also
// requires GetFileFromPathAsync first, which fails for hidden/system files.
//
//   Normal files  — GetFileFromPathAsync + DeleteAsync(StorageDeleteOption.Default)
//                   → sends to Recycle Bin via WinRT.
//
//   Hidden/System — Falls back to SHFileOperation (shell32.dll) with FOF_ALLOWUNDO,
//                   which sends the file to the Recycle Bin at the Win32 Shell
//                   layer, bypassing the broker. Offloaded via Task.Run since
//                   SHFileOperation is synchronous.
//
// =============================================================================

#nullable enable
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using NLog;

namespace FlyPhotos.Services;

internal class StorageOps
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Reads the entire contents of a WinRT <see cref="IRandomAccessStream"/> into a managed
    /// byte array. Seeks the stream back to position 0 before reading.
    /// </summary>
    /// <param name="memStream">The stream to read. Must support seeking and report its size.</param>
    /// <returns>A byte array containing the full stream contents.</returns>
    public static async Task<byte[]> GetInMemByteArray(IRandomAccessStream memStream)
    {
        memStream.Seek(0);
        var bytes = new byte[memStream.Size];
        await memStream.ReadAsync(bytes.AsBuffer(), (uint)memStream.Size, InputStreamOptions.None);
        return bytes;
    }

    /// <summary>
    /// Opens a file and returns an <see cref="IRandomAccessStream"/> optimised for Win2D / WIC
    /// decoding. For local files, bypasses the Windows Storage Broker via
    /// <c>CreateRandomAccessStreamOnFile</c> (shcore.dll), which also succeeds for hidden and
    /// system files. For network and UNC paths the file is fully buffered into an
    /// <see cref="InMemoryRandomAccessStream"/> first, releasing the network file handle
    /// immediately — see the file header for the full performance rationale.
    /// </summary>
    /// <param name="path">Full path to the image file to open.</param>
    /// <returns>
    /// A native <see cref="IRandomAccessStream"/> ready for Win2D / WIC decoding.
    /// The caller is responsible for disposing this stream.
    /// </returns>
    public static async Task<IRandomAccessStream> GetWin2DPerformantStream(string path)
    {
        if (ShouldBufferFile(path))
        {
            var memStream = new InMemoryRandomAccessStream();
            try
            {
                await using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await fileStream.CopyToAsync(memStream.AsStreamForWrite());
                memStream.Seek(0);
                return memStream;
            }
            catch
            {
                memStream.Dispose();
                throw;
            }
        }
        else
        {
            return await Task.Run(() =>
            {
                IntPtr nativePtr = IntPtr.Zero;
                try
                {
                    int hr = Win32Methods.CreateRandomAccessStreamOnFile(
                        path, 0, ref Win32Methods.IID_IRandomAccessStream, out nativePtr);
                    Marshal.ThrowExceptionForHR(hr);

                    return WinRT.MarshalInterface<IRandomAccessStream>.FromAbi(nativePtr);
                }
                finally
                {
                    if (nativePtr != IntPtr.Zero)
                        Marshal.Release(nativePtr);
                }
            });
        }
    }

    /// <summary>
    /// [KEPT FOR REFERENCE — DO NOT USE IN PRODUCTION]
    /// The original stream-acquisition implementation. Uses
    /// <c>StorageFile.GetFileFromPathAsync()</c> which routes through the Windows Storage Broker
    /// and fails for hidden and system files. Replaced by
    /// <see cref="GetWin2DPerformantStream"/> which calls <c>shcore.dll</c> directly.
    /// </summary>
    /// <param name="path">Full path to the image file to open.</param>
    /// <returns>An <see cref="IRandomAccessStream"/> for Win2D / WIC decoding.</returns>
    public static async Task<IRandomAccessStream> GetWin2DPerformantStream_Old(string path)
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
    /// Returns a <see cref="StorageFile"/> suitable for use with
    /// <c>DataPackage.SetStorageItems</c> on the WinRT clipboard.
    /// For normal files the real <see cref="StorageFile"/> is returned directly. For hidden and
    /// system files — where <c>GetFileFromPathAsync</c> would throw — a virtual
    /// <see cref="StorageFile"/> is created via <c>CreateStreamedFileAsync</c>. Its content
    /// callback fires lazily when the user pastes in Explorer, streaming the file through a
    /// plain <see cref="System.IO.FileStream"/> that bypasses the Storage Broker entirely.
    /// </summary>
    /// <param name="filePath">Full path to the file to expose on the clipboard.</param>
    /// <returns>
    /// A <see cref="StorageFile"/> (real or virtual) that represents the file on the clipboard.
    /// </returns>
    public static async Task<StorageFile> GetStorageFileOrVirtualFile(string filePath)
    {
        StorageFile sourceFile;
        try
        {
            // Works for normal files via the Windows Storage Broker.
            sourceFile = await StorageFile.GetFileFromPathAsync(filePath);
        }
        catch
        {
            // FALLBACK: File is likely Hidden or System — Storage Broker rejects it.
            // CreateStreamedFileAsync creates a virtual StorageFile satisfying
            // DataPackage.SetStorageItems. The callback only runs when the user
            // actually pastes into Explorer, at which point FileStream bypasses
            // the broker entirely.
            sourceFile = await StorageFile.CreateStreamedFileAsync(
                Path.GetFileName(filePath),
                async void (request) =>
                {
                    try
                    {
                        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using var outStream = request.AsStreamForWrite();
                        await fs.CopyToAsync(outStream);
                        request.Dispose();
                    }
                    catch
                    {
                        request.FailAndClose(StreamedFileFailureMode.Failed);
                    }
                },
                null);
        }

        return sourceFile;
    }

    /// <summary>
    /// Deletes the specified file, sending it to the Recycle Bin where possible.
    /// First attempts the WinRT path (<c>StorageFile.DeleteAsync</c> with
    /// <see cref="StorageDeleteOption.Default"/>). If that fails — typically because the file
    /// is hidden or a system file and the Storage Broker rejects the open — falls back to
    /// <c>SHFileOperation</c> (shell32.dll) with <c>FOF_ALLOWUNDO</c>, which also sends the
    /// file to the Recycle Bin at the Win32 Shell layer without involving the broker.
    /// </summary>
    /// <param name="filePath">Full path to the file to delete.</param>
    /// <returns>
    /// A <see cref="DeleteResult"/> indicating success or failure. On failure,
    /// <see cref="DeleteResult.FailMessage"/> contains the error details.
    /// </returns>
    public static async Task<DeleteResult> DeleteFileFromDisk(string filePath)
    {
        try
        {
            // Normal files: WinRT path → Recycle Bin
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            await file.DeleteAsync(StorageDeleteOption.Default);
            Logger.Info($"Successfully deleted file: {filePath}");
            return new DeleteResult(true, false);
        }
        catch
        {
            // FALLBACK: File is likely Hidden or System — Storage Broker rejects it.
            // SHFileOperation with FOF_ALLOWUNDO sends the file to the Recycle Bin
            // without involving the broker.
            try
            {
                int result = await Task.Run(() =>
                {
                    var op = new Win32Methods.SHFILEOPSTRUCT
                    {
                        wFunc  = Win32Methods.FO_DELETE,
                        pFrom  = filePath + '\0' + '\0', // must be double-null terminated
                        fFlags = Win32Methods.FOF_ALLOWUNDO | Win32Methods.FOF_NOCONFIRMATION | Win32Methods.FOF_SILENT
                    };
                    return Win32Methods.SHFileOperation(ref op);
                });

                if (result != 0)
                {
                    Logger.Error($"SHFileOperation failed for '{filePath}' with code {result}.");
                    return new DeleteResult(false, false, $"Shell delete failed (code {result})");
                }

                Logger.Info($"Successfully deleted hidden/system file via SHFileOperation: {filePath}");
                return new DeleteResult(true, false);
            }
            catch (Exception ex)
            {
                return new DeleteResult(false, false, $"Exception: {ex.Message}");
            }
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