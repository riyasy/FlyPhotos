#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using FlyPhotos.Services;
using NLog;

namespace FlyPhotos.Display.ImageReading;

/// <summary>
/// One file being decoded. Every decoder in a chain shares its stream and bytes, so trying several decoders
/// opens the file once (native libraries that only take a path still open it themselves). The format starts
/// from the extension and is corrected from the header only after every decoder for it has failed.
/// Dispose when the decode is done; decoded items never keep a reference to the stream.
/// </summary>
internal sealed class ImageFile : IDisposable
{
    /// <summary>Bytes read by the header check; covers every signature <see cref="ImageFormats.Detect"/> knows.</summary>
    private const int HeaderSize = 64;

    /// <summary>Logger instance for ImageFile.</summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>The shared stream, opened by the first reader that asks; null when closed.</summary>
    private IRandomAccessStream? _stream;

    /// <summary>The open stream can serve a full read: opened buffered, or found to be a local file.</summary>
    private bool _streamReadyForFullRead;

    /// <summary>The whole file, read by the first reader that asks; null until then or after a close.</summary>
    private byte[]? _bytes;

    /// <summary>The header check has run; it reads at most once.</summary>
    private bool _headerChecked;

    /// <summary>Opens nothing: the file is opened by the first reader that asks for its stream or bytes.</summary>
    public ImageFile(string path)
    {
        Path = path;
        Extension = System.IO.Path.GetExtension(path);
        Format = ImageFormats.FromExtension(Extension);
    }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>The extension the codec lookups use: the file's own, or the real format's after a correction.</summary>
    public string Extension { get; private set; }

    /// <summary>The format that picks the decoder chain: the extension's, or the header's after a correction.</summary>
    public ImageFormat Format { get; private set; }

    /// <summary>
    /// The shared stream, rewound to the start. <paramref name="bufferNetworkFile"/> false (readers that only
    /// need the header or an embedded thumbnail) leaves a network file unbuffered, as
    /// <see cref="StorageOps.GetWin2DPerformantStream"/> does; a later full read re-opens it buffered, once.
    /// Readers must not dispose it.
    /// </summary>
    public async Task<IRandomAccessStream> GetStreamAsync(bool bufferNetworkFile = true)
    {
        // A stream opened for a header-only read, now wanted for a full read: a local file's stream already
        // serves it; only a network file is re-opened buffered. The network-path check runs at most once.
        if (_stream != null && bufferNetworkFile && !_streamReadyForFullRead)
        {
            if (StorageOps.ShouldBufferFile(Path)) CloseStreamOnly();
            else _streamReadyForFullRead = true;
        }

        if (_stream == null)
        {
            _stream = await StorageOps.GetWin2DPerformantStream(Path, bufferNetworkFile);
            _streamReadyForFullRead = bufferNetworkFile;
        }
        _stream.Seek(0);
        return _stream;
    }

    /// <summary>
    /// Releases the shared stream and the cached bytes. Called before a reader that opens the file itself, so
    /// neither our handle (which would block deleting the file) nor a whole-file copy (an oversized AVIF's, say)
    /// is held through a long native decode. A later reader opens or reads them again. Decoded items that use
    /// the bytes (animations) keep their own reference to them.
    /// </summary>
    public void CloseStream()
    {
        CloseStreamOnly();
        _bytes = null;
    }

    /// <summary>Releases the shared stream but keeps the cached bytes.</summary>
    private void CloseStreamOnly()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>
    /// The whole file in memory, for animators and decoders that take bytes. Read once: from the open stream
    /// when it already holds the whole file, otherwise straight from disk.
    /// </summary>
    public async Task<byte[]> GetBytesAsync() =>
        _bytes ??= _stream != null && _streamReadyForFullRead
            ? await StorageOps.GetInMemByteArray(await GetStreamAsync())
            : await File.ReadAllBytesAsync(Path);

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
            var stream = await GetStreamAsync(bufferNetworkFile: false);
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

    /// <summary>Closes the stream and drops the cached bytes.</summary>
    public void Dispose() => CloseStream();
}
