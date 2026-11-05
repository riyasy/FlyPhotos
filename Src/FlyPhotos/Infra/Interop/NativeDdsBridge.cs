#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlyPhotos.Infra.Interop;

/// <summary>Mirror of the C++ <c>DdsError</c> enum in FlyNativeLibDds (DdsReader.h).</summary>
public enum DdsError
{
    /// <summary>Decoded.</summary>
    Ok = 0,
    /// <summary>A null argument.</summary>
    InvalidInput,
    /// <summary>Missing file, or a header DirectXTex rejects (malformed or unsupported format).</summary>
    FileReadError,
    /// <summary>Decompress / convert / resize / premultiply failed.</summary>
    DecodeError,
    /// <summary>The output buffer could not be allocated, or would be over 2 GB.</summary>
    OutOfMemory
}

/// <summary>P/Invoke declarations for FlyNativeLibDds.dll, the DirectXTex-based DDS decoder.</summary>
internal static partial class NativeDdsBridge
{
    /// <summary>
    /// Mirror of the C++ <c>PixelBuffer</c> struct in FlyNativeLibDds (same layout as FlyNativeLibHeif's).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PixelBuffer
    {
        /// <summary>Premultiplied BGRA8 pixels, rows packed; owned by the native side until FreePixelBuffer.</summary>
        public IntPtr data;
        /// <summary>Size of <see cref="data"/> in bytes.</summary>
        public int dataSize;
        /// <summary>Width of the decoded image (a smaller mip level for previews).</summary>
        public int width;
        /// <summary>Height of the decoded image.</summary>
        public int height;
        /// <summary>Width of the texture's top mip level.</summary>
        public int primaryImageWidth;
        /// <summary>Height of the texture's top mip level.</summary>
        public int primaryImageHeight;
    }

    private const string DllName = "FlyNativeLibDds.dll";

    /// <summary>
    /// Decodes the first image of a DDS file into premultiplied BGRA8. <paramref name="maxDimension"/> 0
    /// gives the full image; otherwise the longer side is capped at it, using a stored mip level when one fits.
    /// </summary>
    [LibraryImport(DllName, EntryPoint = "ExtractDdsImage", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial DdsError ExtractDdsImage(string ddsPath, int maxDimension, out PixelBuffer outBuffer);

    /// <summary>Frees the pixel data allocated by <see cref="ExtractDdsImage"/>.</summary>
    [LibraryImport(DllName, EntryPoint = "FreePixelBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FreePixelBuffer(ref PixelBuffer buffer);
}

/// <summary>Managed wrapper over <see cref="NativeDdsBridge"/>: copies the pixels out and always frees the native buffer.</summary>
public static class NativeDdsWrapper
{
    /// <summary>A decoded DDS image: premultiplied BGRA8 pixels plus the texture's full size.</summary>
    public sealed record DdsImage(byte[] Pixels, int Width, int Height, int PrimaryImageWidth, int PrimaryImageHeight);

    /// <summary>Decodes <paramref name="filePath"/>; see <see cref="NativeDdsBridge.ExtractDdsImage"/>.</summary>
    /// <exception cref="Exception">The native decoder returned an error.</exception>
    public static DdsImage Decode(string filePath, int maxDimension)
    {
        DdsError result = NativeDdsBridge.ExtractDdsImage(filePath, maxDimension, out NativeDdsBridge.PixelBuffer buffer);
        if (result != DdsError.Ok)
            throw new Exception($"Native DDS decoder failed. Error: {result}");

        try
        {
            if (buffer.data == IntPtr.Zero || buffer.dataSize == 0)
                throw new Exception("Native DDS decoder returned no pixels.");

            // AllocateUninitializedArray skips zeroing a buffer that Marshal.Copy overwrites in full.
            byte[] pixels = GC.AllocateUninitializedArray<byte>(buffer.dataSize);
            Marshal.Copy(buffer.data, pixels, 0, buffer.dataSize);
            return new DdsImage(pixels, buffer.width, buffer.height, buffer.primaryImageWidth, buffer.primaryImageHeight);
        }
        finally
        {
            NativeDdsBridge.FreePixelBuffer(ref buffer);
        }
    }
}
