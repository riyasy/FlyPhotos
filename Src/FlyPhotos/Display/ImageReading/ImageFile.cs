#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using FlyPhotos.Infra.Interop;
using FlyPhotos.Services;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// One file being decoded, shared by every reader in a decoder chain. All access to the file goes through it, so
/// three rules live in one place:
/// <list type="bullet">
/// <item>Trying several readers opens the file once: they share one stream and one copy of the bytes.</item>
/// <item>For a full read or the bytes, a network file is read whole in one go and its handle closed at once (#87):
/// fewer round trips, and a decoder can't hold the file open and block deleting or renaming it.</item>
/// <item>A native library that opens the file by path gets it with our handle closed and our bytes dropped.</item>
/// </list>
/// A reader asks for one of: <see cref="GetStreamAsync"/> (decodes the whole file),
/// <see cref="GetPartialReadStreamAsync"/> (reads a part), <see cref="GetBytesAsync"/> (keeps or parses the bytes),
/// <see cref="ReleaseForPathReader"/> (a library opens the path), or <see cref="GetBytesIfNetworkAsync"/> (a library
/// that takes either). Which to pick, and who disposes what: "Writing a reader" in
/// docs/work/20261124-reader-file-access/stream-handling.md.
/// <para>
/// The format starts from the extension and is corrected from the header only after every reader for it failed.
/// Dispose when the decode is done. Not thread-safe: a chain runs its readers one at a time.
/// </para>
/// </summary>
internal sealed class ImageFile : IDisposable
{
    /// <summary>Bytes read by the header check; covers every signature <see cref="ImageFormats.Detect"/> knows.</summary>
    private const int HeaderSize = 64;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The shared stream, opened by the first reader that asks; null when closed. A local file's is shcore's
    /// stream on the file; a network file's reads <see cref="_bytes"/> once a full read was asked for.
    /// </summary>
    private IRandomAccessStream? _stream;

    /// <summary>Owns <see cref="_stream"/> when it reads <see cref="_bytes"/>; disposing it disposes the stream.</summary>
    private ClosableStream.Opened? _overBytes;

    /// <summary>
    /// <see cref="_stream"/> serves a full read. Matters for a local file, whose partial-read stream is promoted on
    /// the first full read; on a share it is true exactly when the stream reads <see cref="_bytes"/>.
    /// </summary>
    private bool _streamReadyForFullRead;

    /// <summary>The whole file, read by the first reader that needs it; null until then and after a release.</summary>
    private byte[]? _bytes;

    /// <summary>Whether the file is on a network share; asked once, on first use.</summary>
    private bool? _isNetwork;

    /// <summary>The header check has run; it reads at most once.</summary>
    private bool _headerChecked;

    /// <summary>Opens nothing: the file is opened by the first reader that asks for its stream or bytes.</summary>
    public ImageFile(string path)
    {
        Path = path;
        Extension = System.IO.Path.GetExtension(path);
        Format = ImageFormats.FromExtension(Extension);
    }

    /// <summary>
    /// The file's full path, for log messages and extension checks. A native library that decodes from the path
    /// takes it from <see cref="ReleaseForPathReader"/> instead. Opening it directly is only for a read that keeps
    /// the shared stream for later use (the Magick size ping, MagicScaler's local preview).
    /// </summary>
    public string Path { get; }

    /// <summary>The extension the codec lookups use: the file's own, or the real format's after a correction.</summary>
    public string Extension { get; private set; }

    /// <summary>The format that picks the decoder chain: the extension's, or the header's after a correction.</summary>
    public ImageFormat Format { get; private set; }

    /// <summary>Whether the image is over the GPU's maximum bitmap size; null until ImageReader has checked.</summary>
    public bool? Oversized { get; set; }

    /// <summary>
    /// The shared stream, rewound to the start, for a reader that decodes the whole file (a WIC full decode,
    /// <c>CanvasBitmap.LoadAsync</c>). Locally it is shcore's stream on the file, the fastest WIC source measured.
    /// On a share the file is read whole first and the stream reads those bytes, because WIC's many small reads
    /// would each be a round trip and the handle would stay open. Owned here: readers must not dispose it.
    /// </summary>
    public Task<IRandomAccessStream> GetStreamAsync() => OpenStreamAsync(fullRead: true);

    /// <summary>
    /// The shared stream, rewound to the start, for a reader that needs only part of the file (the header, an
    /// embedded thumbnail, a capped decode, a size probe). A network file is read in place, so a thumbnail
    /// doesn't cost a whole-file read; a later full read replaces it with the whole file, once.
    /// Owned here: readers must not dispose it.
    /// </summary>
    public Task<IRandomAccessStream> GetPartialReadStreamAsync() => OpenStreamAsync(fullRead: false);

    /// <summary>
    /// The whole file in memory, for a reader that keeps the bytes (animators, multi-page images) or a library
    /// that parses them (libheif for AVIF). Read once and shared: on a share the full-read stream reads the same
    /// array; locally it is copied from an open full-read stream, or read from disk.
    /// The array is never pooled or freed by us, so a decoded item may keep it; an item that needs a stream over it
    /// opens its own <see cref="ClosableStream"/> and disposes that.
    /// </summary>
    public async Task<byte[]> GetBytesAsync()
    {
        if (_bytes != null) return _bytes;
        if (IsNetwork)
        {
            // A stream open here reads the share in place (a full-read one would have set _bytes): close it, so
            // the handle isn't held while the reader decodes. A later full read wraps these bytes.
            CloseStreamOnly();
            return _bytes = await File.ReadAllBytesAsync(Path);
        }
        return _bytes = _stream != null && _streamReadyForFullRead
            ? await StorageOps.GetInMemByteArray(await GetStreamAsync())
            : await File.ReadAllBytesAsync(Path);
    }

    /// <summary>
    /// The bytes on a network share, else null; for a library that reads either a path or bytes (MagicScaler,
    /// libheif). On a share each open by path is a round trip (~10 ms), so the library gets the bytes, read once
    /// and shared with the chain. Locally the path is as fast and needs no whole-file array.
    /// On null, open the path: through <see cref="ReleaseForPathReader"/>, unless the reader still needs the shared
    /// stream (MagicScaler's small-image branch).
    /// </summary>
    public async Task<byte[]?> GetBytesIfNetworkAsync() => IsNetwork ? await GetBytesAsync() : null;

    /// <summary>
    /// Closes the shared stream, drops the cached bytes and returns <see cref="Path"/>, for a native library that
    /// opens the file itself (libheif, DirectXTex, ImageMagick, rawler, resvg, Windows.Data.Pdf, the PSD reader).
    /// Its decode can take seconds: our handle would block deleting or renaming the file, and the bytes (an
    /// oversized AVIF's, say) would stay in memory for nothing. A later reader in the chain opens or reads again.
    /// Decoded items that kept the bytes keep their own reference.
    /// </summary>
    public string ReleaseForPathReader()
    {
        CloseStream();
        return Path;
    }

    /// <summary>
    /// Reads the header and, when it shows a different format than <see cref="Format"/>, switches to it
    /// (#252: e.g. an AVIF saved as .webp). Returns whether it switched; only the first call reads.
    /// </summary>
    public async Task<bool> CorrectFormatFromHeaderAsync()
    {
        if (_headerChecked) return false;
        _headerChecked = true;
        try
        {
            var stream = await GetPartialReadStreamAsync();
            var header = (await stream.ReadAsync(new byte[HeaderSize].AsBuffer(), HeaderSize, InputStreamOptions.None)).ToArray();
            if (ImageFormats.Detect(header) is not { } actual || actual == Format) return false;

            Logger.Info("{0} holds {1} data; decoding it as {1}", Path, actual);
            Format = actual;
            Extension = ImageFormats.ExtensionOf(actual);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not read the header of {0}", Path);
            return false;
        }
    }

    /// <summary>Closes the stream and drops the cached bytes; decoded items never hold our stream.</summary>
    public void Dispose() => CloseStream();

    /// <summary>
    /// Whether the file is on a network share (UNC path or mapped drive). Cached for readability, not speed
    /// (the check costs ~1.5 µs). Private: readers ask for "the bytes, if cheaper", never "is this a share".
    /// </summary>
    private bool IsNetwork => _isNetwork ??= StorageOps.ShouldBufferFile(Path);

    /// <summary>Opens the shared stream on first use, or replaces a network partial-read one for a full read.</summary>
    private async Task<IRandomAccessStream> OpenStreamAsync(bool fullRead)
    {
        // A local stream opened for a partial read serves a full read as it is; a network one is replaced.
        if (_stream != null && fullRead && !_streamReadyForFullRead)
        {
            if (IsNetwork) CloseStreamOnly();
            else _streamReadyForFullRead = true;
        }

        if (_stream == null)
        {
            if (fullRead && IsNetwork)
            {
                _bytes ??= await File.ReadAllBytesAsync(Path);
                _overBytes = ClosableStream.Open(_bytes);
                _stream = _overBytes.Stream;
            }
            else
            {
                _stream = await StorageOps.GetWin2DPerformantStream(Path);
            }
            _streamReadyForFullRead = fullRead;
        }
        _stream.Seek(0);
        return _stream;
    }

    /// <summary>Closes the stream and drops the bytes.</summary>
    private void CloseStream()
    {
        CloseStreamOnly();
        _bytes = null;
    }

    /// <summary>Closes the stream but keeps the bytes, which a network full read is about to use.</summary>
    private void CloseStreamOnly()
    {
        if (_overBytes != null) _overBytes.Dispose(); // disposes _stream too
        else _stream?.Dispose();
        (_stream, _overBytes) = (null, null);
    }
}
