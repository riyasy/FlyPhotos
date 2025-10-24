using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlyPhotos.NativeWrappers;

/// <summary>
/// A managed C# wrapper for the native HeifDecoder DLL.
/// This class provides a safe and easy-to-use interface for decoding HEIC/HEIF images.
/// </summary>
public static class NativeHeifBridge
{
    /// <summary>
    /// Represents a decoded image with its pixel data and dimensions.
    /// </summary>
    public class HeifImage
    {
        /// <summary>
        /// The raw pixel data in 32-bit BGRA format.
        /// </summary>
        public byte[] Pixels { get; internal set; }

        /// <summary>
        /// The width of the decoded image.
        /// </summary>
        public int Width { get; internal set; }

        /// <summary>
        /// The height of the decoded image.
        /// </summary>
        public int Height { get; internal set; }

        /// <summary>
        /// The width of the primary image in the HEIC file.
        /// </summary>
        public int PrimaryImageWidth { get; internal set; }

        /// <summary>
        /// The height of the primary image in the HEIC file.
        /// </summary>
        public int PrimaryImageHeight { get; internal set; }
    }

    /// <summary>
    /// Decodes the primary image from a HEIC/HEIF file.
    /// </summary>
    /// <param name="filePath">The full path to the .heic or .hif file.</param>
    /// <returns>A HeifImage object containing the decoded BGRA pixel data and dimensions.</returns>
    /// <exception cref="Exception">Thrown if the native DLL fails to decode the image.</exception>
    public static HeifImage DecodePrimaryImage(string filePath)
    {
        // 1. Call the native function to get a pointer to the decoded data.
        HeifError result = NativeHeif.ExtractPrimaryImageBGRA(filePath, out PixelBuffer buffer);

        if (result != HeifError.Ok)
        {
            throw new Exception($"Native HEIF decoder failed to decode primary image. Error: {result}");
        }

        // The 'finally' block guarantees that the native memory is freed,
        // even if an exception occurs during the copy process.
        try
        {
            // If the image is empty, return null.
            if (buffer.data == IntPtr.Zero || buffer.dataSize == 0)
            {
                return null;
            }

            // 2. Create a managed byte array to hold the pixel data.
            byte[] managedPixels = new byte[buffer.dataSize];

            // 3. Copy the data from the unmanaged (C++) memory to the managed (C#) array.
            Marshal.Copy(buffer.data, managedPixels, 0, buffer.dataSize);

            // 4. Create the final result object.
            return new HeifImage
            {
                Pixels = managedPixels,
                Width = buffer.width,
                Height = buffer.height,
                PrimaryImageWidth = buffer.primaryImageWidth,
                PrimaryImageHeight = buffer.primaryImageHeight
            };
        }
        finally
        {
            // 5. CRITICAL: Always call the native function to free the memory.
            NativeHeif.FreePixelBuffer(ref buffer);
        }
    }

    /// <summary>
    /// Decodes the thumbnail image from a HEIC/HEIF file.
    /// </summary>
    /// <param name="filePath">The full path to the .heic or .hif file.</param>
    /// <returns>A HeifImage object containing the decoded BGRA pixel data and dimensions.</returns>
    /// <exception cref="Exception">Thrown if the native DLL fails to decode the image.</exception>
    public static HeifImage DecodeThumbnail(string filePath)
    {
        HeifError result = NativeHeif.ExtractThumbnailBGRA(filePath, out PixelBuffer buffer);

        if (result != HeifError.Ok)
        {
            throw new Exception($"Native HEIF decoder failed to decode thumbnail. Error: {result}");
        }

        try
        {
            if (buffer.data == IntPtr.Zero || buffer.dataSize == 0)
            {
                return null;
            }

            byte[] managedPixels = new byte[buffer.dataSize];
            Marshal.Copy(buffer.data, managedPixels, 0, buffer.dataSize);

            return new HeifImage
            {
                Pixels = managedPixels,
                Width = buffer.width,
                Height = buffer.height,
                PrimaryImageWidth = buffer.primaryImageWidth,
                PrimaryImageHeight = buffer.primaryImageHeight
            };
        }
        finally
        {
            NativeHeif.FreePixelBuffer(ref buffer);
        }
    }
}


#region P/Invoke Declarations

/// <summary>
/// C# equivalent of the C++ HeifError enum.
/// </summary>
public enum HeifError
{
    Ok = 0,
    FileNotFound,
    FileReadError,
    NoPrimaryImage,
    NoThumbnailFound,
    ThumbnailReadError,
    ImageDecodeError,
    PngEncodeError,
    InvalidInput
}

/// <summary>
/// C# equivalent of the C++ PixelBuffer struct.
/// This must have the exact same memory layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PixelBuffer
{
    public IntPtr data; // Maps to uint8_t*
    public int dataSize;
    public int width;
    public int height;
    public int primaryImageWidth;
    public int primaryImageHeight;
}


internal static partial class NativeHeif
{
    // IMPORTANT: Replace "YourDllName.dll" with the actual name of your compiled C++ DLL.
    private const string DllName = "FlyNativeLibHeif.dll";

    [LibraryImport(DllName, EntryPoint = "ExtractPrimaryImageBGRA", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractPrimaryImageBGRA(
        string heicPath, // The [MarshalAs] attribute is no longer needed.
        out PixelBuffer outBuffer);

    [LibraryImport(DllName, EntryPoint = "ExtractThumbnailBGRA", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractThumbnailBGRA(
        string heicPath,
        out PixelBuffer outBuffer);

    [LibraryImport(DllName, EntryPoint = "FreePixelBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FreePixelBuffer(ref PixelBuffer buffer);
}

#endregion