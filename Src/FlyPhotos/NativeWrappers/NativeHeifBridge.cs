using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlyPhotos.NativeWrappers;

#region P/Invoke Declarations

/// <summary>
/// C# equivalent of the C++ HeifError enum.
/// Defines possible error codes returned by the native HEIF decoding library.
/// </summary>
public enum HeifError
{
    /// <summary>
    /// Operation completed successfully.
    /// </summary>
    Ok = 0,
    /// <summary>
    /// The specified HEIF file was not found.
    /// </summary>
    FileNotFound,
    /// <summary>
    /// An error occurred while reading the HEIF file.
    /// </summary>
    FileReadError,
    /// <summary>
    /// No primary image was found within the HEIF file.
    /// </summary>
    NoPrimaryImage,
    /// <summary>
    /// No thumbnail image was found within the HEIF file.
    /// </summary>
    NoThumbnailFound,
    /// <summary>
    /// An error occurred while reading the thumbnail data.
    /// </summary>
    ThumbnailReadError,
    /// <summary>
    /// An error occurred during the decoding process of the image data.
    /// </summary>
    ImageDecodeError,
    /// <summary>
    /// An error occurred during the PNG encoding process (if applicable).
    /// </summary>
    PngEncodeError,
    /// <summary>
    /// The input provided to the native function was invalid.
    /// </summary>
    InvalidInput
}

/// <summary>
/// Provides static methods for direct interoperability with the native HEIF decoding library (FlyNativeLibHeif.dll).
/// This class handles the P/Invoke declarations and ensures correct memory layout for interop structures.
/// </summary>
internal static partial class NativeHeifBridge
{
    /// <summary>
    /// C# equivalent of the C++ PixelBuffer struct.
    /// This structure must have the exact same memory layout as its native counterpart
    /// to ensure correct data transfer across the P/Invoke boundary.
    /// It holds information about the decoded pixel data and image dimensions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PixelBuffer
    {
        /// <summary>
        /// A pointer to the unmanaged memory location where the pixel data is stored.
        /// Corresponds to `uint8_t*` in C++.
        /// </summary>
        public IntPtr data; // Maps to uint8_t*
        /// <summary>
        /// The total size of the pixel data in bytes.
        /// </summary>
        public int dataSize;
        /// <summary>
        /// The width of the extracted image (either primary or thumbnail).
        /// </summary>
        public int width;
        /// <summary>
        /// The height of the extracted image (either primary or thumbnail).
        /// </summary>
        public int height;
        /// <summary>
        /// The width of the primary image as reported by the HEIF file.
        /// </summary>
        public int primaryImageWidth;
        /// <summary>
        /// The height of the primary image as reported by the HEIF file.
        /// </summary>
        public int primaryImageHeight;
    }

    private const string DllName = "FlyNativeLibHeif.dll";

    /// <summary>
    /// Imports the native `ExtractPrimaryImageBGRA` function from `FlyNativeLibHeif.dll`.
    /// This function decodes the primary image from a HEIF file into 32-bit BGRA pixel format.
    /// </summary>
    /// <param name="heicPath">The file path to the HEIC/HEIF image.</param>
    /// <param name="outBuffer">An output <see cref="PixelBuffer"/> struct containing the pointer to the decoded pixel data and image metadata.</param>
    /// <returns>A <see cref="HeifError"/> indicating the success or failure of the operation.</returns>
    [LibraryImport(DllName, EntryPoint = "ExtractPrimaryImageBGRA", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractPrimaryImageBGRA(string heicPath, out PixelBuffer outBuffer);

    /// <summary>
    /// Imports the native `ExtractThumbnailBGRA` function from `FlyNativeLibHeif.dll`.
    /// This function decodes the thumbnail image from a HEIF file into 32-bit BGRA pixel format.
    /// </summary>
    /// <param name="heicPath">The file path to the HEIC/HEIF image.</param>
    /// <param name="outBuffer">An output <see cref="PixelBuffer"/> struct containing the pointer to the decoded pixel data and image metadata.</param>
    /// <returns>A <see cref="HeifError"/> indicating the success or failure of the operation.</returns>
    [LibraryImport(DllName, EntryPoint = "ExtractThumbnailBGRA", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractThumbnailBGRA(string heicPath, out PixelBuffer outBuffer);

    /// <summary>
    /// Imports the native `FreePixelBuffer` function from `FlyNativeLibHeif.dll`.
    /// This critical function is responsible for freeing the unmanaged memory allocated by
    /// `ExtractPrimaryImageBGRA` or `ExtractThumbnailBGRA` to prevent memory leaks.
    /// </summary>
    /// <param name="buffer">A reference to the <see cref="PixelBuffer"/> struct whose `data` pointer needs to be freed.</param>
    [LibraryImport(DllName, EntryPoint = "FreePixelBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FreePixelBuffer(ref PixelBuffer buffer);
}

#endregion

/// <summary>
/// A managed C# wrapper for the native HeifDecoder DLL.
/// This class provides a safe, exception-driven, and easy-to-use interface for decoding HEIC/HEIF images,
/// handling memory management between managed and unmanaged code.
/// </summary>
public static class NativeHeifWrapper
{
    /// <summary>
    /// Represents a decoded HEIF image with its pixel data and dimensions.
    /// </summary>
    public class HeifImage
    {
        /// <summary>
        /// The raw pixel data of the decoded image. The data is in 32-bit BGRA format,
        /// meaning each pixel consists of 4 bytes: Blue, Green, Red, Alpha.
        /// </summary>
        public byte[] Pixels { get; internal set; }

        /// <summary>
        /// The width of the decoded image (either primary image or thumbnail).
        /// </summary>
        public int Width { get; internal set; }

        /// <summary>
        /// The height of the decoded image (either primary image or thumbnail).
        /// </summary>
        public int Height { get; internal set; }

        /// <summary>
        /// The width of the primary image as specified in the HEIC file metadata.
        /// This might differ from <see cref="Width"/> if a thumbnail was decoded.
        /// </summary>
        public int PrimaryImageWidth { get; internal set; }

        /// <summary>
        /// The height of the primary image as specified in the HEIC file metadata.
        /// This might differ from <see cref="Height"/> if a thumbnail was decoded.
        /// </summary>
        public int PrimaryImageHeight { get; internal set; }
    }

    /// <summary>
    /// Decodes the primary image from a HEIC/HEIF file into a managed <see cref="HeifImage"/> object.
    /// Handles calling the native DLL, copying data to managed memory, and freeing native resources.
    /// </summary>
    /// <param name="filePath">The full path to the .heic, .heif or .hif file.</param>
    /// <returns>A <see cref="HeifImage"/> object containing the decoded BGRA pixel data and dimensions, or null if the image data is empty.</returns>
    /// <exception cref="Exception">Thrown if the native DLL returns an error code during decoding,
    /// indicating issues like file not found, decoding errors, or no primary image.</exception>
    public static HeifImage DecodePrimaryImage(string filePath)
    {
        HeifError result = NativeHeifBridge.ExtractPrimaryImageBGRA(filePath, out NativeHeifBridge.PixelBuffer buffer);

        if (result != HeifError.Ok)
            throw new Exception($"Native HEIF decoder failed to decode primary image. Error: {result}");

        try
        {
            if (buffer.data == IntPtr.Zero || buffer.dataSize == 0)
                return null;

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
            // Ensure the unmanaged memory is always freed.
            NativeHeifBridge.FreePixelBuffer(ref buffer);
        }
    }

    /// <summary>
    /// Decodes the thumbnail image from a HEIC/HEIF file into a managed <see cref="HeifImage"/> object.
    /// Handles calling the native DLL, copying data to managed memory, and freeing native resources.
    /// </summary>
    /// <param name="filePath">The full path to the .heic, heif or .hif file.</param>
    /// <returns>A <see cref="HeifImage"/> object containing the decoded BGRA pixel data and dimensions, or null if no thumbnail data is found or is empty.</returns>
    /// <exception cref="Exception">Thrown if the native DLL returns an error code during decoding,
    /// indicating issues like file not found, decoding errors, or no thumbnail found.</exception>
    public static HeifImage DecodeThumbnail(string filePath)
    {
        HeifError result = NativeHeifBridge.ExtractThumbnailBGRA(filePath, out NativeHeifBridge.PixelBuffer buffer);

        if (result != HeifError.Ok)
            throw new Exception($"Native HEIF decoder failed to decode thumbnail. Error: {result}");

        try
        {
            if (buffer.data == IntPtr.Zero || buffer.dataSize == 0)
                return null;

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
            NativeHeifBridge.FreePixelBuffer(ref buffer);
        }
    }
}