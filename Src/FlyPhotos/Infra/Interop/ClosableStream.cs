#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32.SafeHandles;
using Windows.Storage.Streams;
using WinRT;

namespace FlyPhotos.Infra.Interop;

/// <summary>
/// How every decoder in the app reads bytes in memory or a file held open: a read-only COM stream for WIC, Win2D and
/// Windows.Data.Pdf. They call it natively, not through the .AsRandomAccessStream() adapter, which costs ~38–100 µs
/// per read (see StorageOps' header). Disposing drops the bytes or closes the file at once, though a decoder keeps its
/// stream until a GC; its later reads just fail. One decoder per stream: there is one position.
/// </summary>
/// <example><code>using var s = ClosableStream.Open(bytes); await CanvasBitmap.LoadAsync(ctrl, s.Stream);</code></example>
[GeneratedComClass]
internal sealed unsafe partial class ClosableStream : ClosableStream.IStream, ClosableStream.IAgileObject
{
    private const int S_OK = 0, S_FALSE = 1, E_FAIL = unchecked((int)0x80004005), E_NOTIMPL = unchecked((int)0x80004001),
        STG_E_INVALIDFUNCTION = unchecked((int)0x80030001), STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    private const uint STGTY_STREAM = 2;
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid IID_IStream = new("0000000c-0000-0000-C000-000000000046");
    private static Guid IID_IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");

    private byte[]? _bytes;
    private SafeFileHandle? _file;
    private nint _native;
    private readonly long _length;
    private long _position;

    private ClosableStream(byte[] bytes, int length)
    {
        _bytes = bytes;
        _length = length;
    }

    private ClosableStream(nint native, int length)
    {
        _native = native;
        _length = length;
    }

    private ClosableStream(SafeFileHandle file)
    {
        _file = file;
        _length = RandomAccess.GetLength(file);
    }

    /// <summary>
    /// Over the first <paramref name="length"/> bytes of <paramref name="bytes"/> (all by default), not copied. A pooled
    /// or reused array may be handed back once the result is disposed: later reads fail, never see it.
    /// </summary>
    public static Opened Open(byte[] bytes, int length = -1) => new ClosableStream(bytes, length < 0 ? bytes.Length : length).Wrap();

    /// <summary>Over what <paramref name="stream"/> holds, not copied.</summary>
    public static Opened Open(MemoryStream stream) => Open(stream.GetBuffer(), (int)stream.Length);

    /// <summary>
    /// Over <paramref name="length"/> bytes of native memory at <paramref name="data"/>, not copied. Free that memory
    /// only after disposing the result and awaiting the decode.
    /// </summary>
    public static Opened Open(byte* data, int length) => new ClosableStream((nint)data, length).Wrap();

    /// <summary>Over an open file, which the result owns from now on.</summary>
    public static Opened Open(SafeFileHandle file)
    {
        try
        {
            return new ClosableStream(file).Wrap();
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>A stream a decoder reads, and the way to cut it off: Dispose drops the bytes or closes the file.</summary>
    /// <remarks>Holds the WinRT stream outside <see cref="ClosableStream"/>: the WinRT stream holds that object through
    /// COM, so holding it back from there would be a cycle no GC frees.</remarks>
    internal sealed class Opened(IRandomAccessStream stream, ClosableStream source) : IDisposable
    {
        /// <summary>What WIC, Win2D and Windows.Data.Pdf take.</summary>
        public IRandomAccessStream Stream { get; } = stream;

        public void Dispose()
        {
            Stream.Dispose();
            source._bytes = null;
            source._native = 0;
            source._file?.Dispose();
            source._file = null;
        }
    }

    private Opened Wrap()
    {
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_IStream, out nint stream));
            try
            {
                Marshal.ThrowExceptionForHR(CreateRandomAccessStreamOverStream(stream, 0, ref IID_IRandomAccessStream, out nint ras));
                try
                {
                    return new Opened(MarshalInterface<IRandomAccessStream>.FromAbi(ras), this);
                }
                finally
                {
                    Marshal.Release(ras);
                }
            }
            finally
            {
                Marshal.Release(stream);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    int IStream.Read(byte* buffer, uint count, uint* read)
    {
        var bytes = _bytes;
        var native = _native;
        var file = _file;
        int done = 0;
        int want = (int)Math.Min(count, Math.Max(0, _length - _position));
        try
        {
            if (bytes != null)
            {
                new ReadOnlySpan<byte>(bytes, (int)_position, want).CopyTo(new Span<byte>(buffer, want));
                done = want;
            }
            else if (native != 0)
            {
                new ReadOnlySpan<byte>((byte*)native + _position, want).CopyTo(new Span<byte>(buffer, want));
                done = want;
            }
            else if (file != null)
            {
                for (int n; done < want && (n = RandomAccess.Read(file, new Span<byte>(buffer + done, want - done), _position + done)) > 0;)
                    done += n;
            }
            else
            {
                return Fail(read);
            }
        }
        catch (Exception) // closed under a read, or an I/O error: the decoder sees a failed read
        {
            return Fail(read);
        }
        _position += done;
        if (read != null) *read = (uint)done;
        return done == count ? S_OK : S_FALSE;
    }

    private static int Fail(uint* read)
    {
        if (read != null) *read = 0;
        return E_FAIL;
    }

    int IStream.Seek(long offset, uint origin, ulong* newPosition)
    {
        long from = origin switch { 0 => 0, 1 => _position, 2 => _length, _ => -1 };
        if (from < 0 || from + offset < 0) return STG_E_INVALIDFUNCTION;
        _position = from + offset;
        if (newPosition != null) *newPosition = (ulong)_position;
        return S_OK;
    }

    int IStream.Stat(STATSTG* stat, uint flags)
    {
        *stat = default;
        stat->type = STGTY_STREAM;
        stat->cbSize = (ulong)_length;
        return S_OK;
    }

    int IStream.Write(byte* buffer, uint count, uint* written) => STG_E_ACCESSDENIED;
    int IStream.SetSize(ulong size) => STG_E_ACCESSDENIED;
    int IStream.CopyTo(nint stream, ulong count, ulong* read, ulong* written) => E_NOTIMPL;
    int IStream.Commit(uint flags) => S_OK;
    int IStream.Revert() => S_OK;
    int IStream.LockRegion(ulong offset, ulong count, uint type) => STG_E_INVALIDFUNCTION;
    int IStream.UnlockRegion(ulong offset, ulong count, uint type) => STG_E_INVALIDFUNCTION;
    int IStream.Clone(nint* stream) => E_NOTIMPL;

    [LibraryImport("shcore.dll")]
    private static partial int CreateRandomAccessStreamOverStream(nint stream, uint options, ref Guid riid, out nint ras);

    /// <summary>COM's IStream (objidl.h), its ISequentialStream methods first, in vtable order.</summary>
    [GeneratedComInterface]
    [Guid("0000000c-0000-0000-C000-000000000046")]
    internal partial interface IStream
    {
        [PreserveSig] int Read(byte* buffer, uint count, uint* read);
        [PreserveSig] int Write(byte* buffer, uint count, uint* written);
        [PreserveSig] int Seek(long offset, uint origin, ulong* newPosition);
        [PreserveSig] int SetSize(ulong size);
        [PreserveSig] int CopyTo(nint stream, ulong count, ulong* read, ulong* written);
        [PreserveSig] int Commit(uint flags);
        [PreserveSig] int Revert();
        [PreserveSig] int LockRegion(ulong offset, ulong count, uint type);
        [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint type);
        [PreserveSig] int Stat(STATSTG* stat, uint flags);
        [PreserveSig] int Clone(nint* stream);
    }

    /// <summary>
    /// Says the stream needs no marshalling between threads, which is true: it has no thread affinity. Without it,
    /// CreateRandomAccessStreamOverStream sets up marshalling: 88 µs per open instead of 2 (measured).
    /// </summary>
    [GeneratedComInterface]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    internal partial interface IAgileObject;

    /// <summary>Mirror of <c>STATSTG</c> (objidl.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct STATSTG
    {
        public nint pwcsName;
        public uint type;
        public ulong cbSize;
        public long mtime, ctime, atime; // FILETIME
        public uint grfMode, grfLocksSupported;
        public Guid clsid;
        public uint grfStateBits, reserved;
    }
}
