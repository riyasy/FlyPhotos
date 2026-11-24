#nullable enable
using System;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Infra.Interop;

/// <summary>
/// Pixels a native decoder allocated, read where they are instead of copied into a managed array. Dispose frees
/// them: keep the decode and the upload inside one <c>using</c>.
/// </summary>
/// <remarks>No finalizer: a span from <see cref="Pixels"/> does not keep this object alive, so a finalizer could
/// free memory that is still being read.</remarks>
public sealed unsafe class NativeImage : IDisposable
{
    internal enum Owner { Heif, Dds, Rust }

    private readonly Owner _owner;
    private readonly int _length;
    private byte* _data;

    internal NativeImage(Owner owner, DirectXPixelFormat format, nint data, int length, int width, int height,
        int primaryWidth, int primaryHeight)
    {
        (_owner, _length, Format) = (owner, length, format);
        _data = (byte*)data;
        (Width, Height, PrimaryImageWidth, PrimaryImageHeight) = (width, height, primaryWidth, primaryHeight);
    }

    /// <summary>A Rust buffer of RGBA8 pixels (rawler, resvg); null for a null pointer.</summary>
    internal static NativeImage? FromRust(nint data, int width, int height) => data == 0 ? null :
        new NativeImage(Owner.Rust, DirectXPixelFormat.R8G8B8A8UIntNormalized, data, width * height * 4, width, height, width, height);

    /// <summary>The layout the decoder wrote, set where the image is made so no caller can mismatch it (#273).</summary>
    public DirectXPixelFormat Format { get; }

    /// <summary>Width of the decoded image (a thumbnail or smaller mip level for previews).</summary>
    public int Width { get; }
    /// <summary>Height of the decoded image.</summary>
    public int Height { get; }
    /// <summary>Width of the full image the file holds.</summary>
    public int PrimaryImageWidth { get; }
    /// <summary>Height of the full image the file holds.</summary>
    public int PrimaryImageHeight { get; }

    /// <summary>The pixels, rows packed. Valid until Dispose.</summary>
    public ReadOnlySpan<byte> Pixels =>
        _data != null ? new ReadOnlySpan<byte>(_data, _length) : throw new ObjectDisposedException(nameof(NativeImage));

    /// <summary>The pixels on the GPU; this image can be disposed as soon as it returns.</summary>
    public CanvasBitmap ToBitmap(ICanvasResourceCreator ctrl) => PixelUpload.CreateBitmap(ctrl, Pixels, Width, Height, Format);

    public void Dispose()
    {
        var data = (nint)_data;
        if (data == 0) return;
        _data = null;
        switch (_owner)
        {
            case Owner.Heif:
                var heif = new NativeHeifBridge.PixelBuffer { data = data, dataSize = _length };
                NativeHeifBridge.FreePixelBuffer(ref heif);
                break;
            case Owner.Dds:
                var dds = new NativeDdsBridge.PixelBuffer { data = data, dataSize = _length };
                NativeDdsBridge.FreePixelBuffer(ref dds);
                break;
            case Owner.Rust:
                // (uint) keeps the exact usize Rust allocated, even when width * height * 4 overflowed int.
                NativeRustBridge.free_rust_buffer(data, (uint)_length);
                break;
        }
    }
}
