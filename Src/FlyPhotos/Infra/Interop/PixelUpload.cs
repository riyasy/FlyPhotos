#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using WinRT;

namespace FlyPhotos.Infra.Interop;

/// <summary>
/// Makes a <see cref="CanvasBitmap"/> from pixels where they are, managed or native, with no copy of our own:
/// Win2D reads them through an IBuffer during the call.
/// </summary>
internal static unsafe partial class PixelUpload
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid IID_IBuffer = new("905A0FE0-BC53-11DF-8C49-001E4FC686DA");

    public static CanvasBitmap CreateBitmap(ICanvasResourceCreator ctrl, ReadOnlySpan<byte> pixels, int width, int height,
        DirectXPixelFormat format)
    {
        fixed (byte* data = pixels)
        {
            var source = new SpanBuffer(data, (uint)pixels.Length);
            try
            {
                return CanvasBitmap.CreateFromBytes(ctrl, source.Wrap(), width, height, format);
            }
            finally
            {
                // Win2D has copied the pixels; a late read now fails instead of reading freed memory.
                source.Close();
            }
        }
    }

    /// <summary>The IBuffer, valid for one call: the WinRT wrapper lives until a GC, the pointer until Close.</summary>
    [GeneratedComClass]
    private sealed partial class SpanBuffer(byte* data, uint length) : IBufferAbi, IBufferByteAccess, ClosableStream.IAgileObject
    {
        private const int S_OK = 0, E_FAIL = unchecked((int)0x80004005), E_ACCESSDENIED = unchecked((int)0x80070005);
        private byte* _data = data;

        public IBuffer Wrap()
        {
            nint unknown = Wrappers.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_IBuffer, out nint buffer));
                try
                {
                    return MarshalInterface<IBuffer>.FromAbi(buffer);
                }
                finally
                {
                    Marshal.Release(buffer);
                }
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public void Close() => _data = null;

        int IInspectable.GetIids(uint* count, Guid** iids)
        {
            *count = 0;
            *iids = null;
            return S_OK;
        }

        int IInspectable.GetRuntimeClassName(nint* name)
        {
            *name = 0;
            return S_OK;
        }

        int IInspectable.GetTrustLevel(int* level)
        {
            *level = 0;
            return S_OK;
        }

        int IBufferAbi.GetCapacity(uint* value)
        {
            *value = length;
            return S_OK;
        }

        int IBufferAbi.GetLength(uint* value)
        {
            *value = length;
            return S_OK;
        }

        int IBufferAbi.SetLength(uint value) => E_ACCESSDENIED;

        int IBufferByteAccess.Buffer(byte** value)
        {
            *value = _data;
            return _data != null ? S_OK : E_FAIL;
        }
    }

    /// <summary>CsWinRT asks for it before it wraps a pointer as <see cref="IBuffer"/>.</summary>
    [GeneratedComInterface]
    [Guid("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90")]
    internal partial interface IInspectable
    {
        [PreserveSig] int GetIids(uint* count, Guid** iids);
        [PreserveSig] int GetRuntimeClassName(nint* name);
        [PreserveSig] int GetTrustLevel(int* level);
    }

    /// <summary>Windows.Storage.Streams.IBuffer's vtable.</summary>
    [GeneratedComInterface]
    [Guid("905A0FE0-BC53-11DF-8C49-001E4FC686DA")]
    internal partial interface IBufferAbi : IInspectable
    {
        [PreserveSig] int GetCapacity(uint* value);
        [PreserveSig] int GetLength(uint* value);
        [PreserveSig] int SetLength(uint value);
    }

    /// <summary>How Win2D reaches the bytes behind an IBuffer (robuffer.h).</summary>
    [GeneratedComInterface]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    internal partial interface IBufferByteAccess
    {
        [PreserveSig] int Buffer(byte** value);
    }
}
