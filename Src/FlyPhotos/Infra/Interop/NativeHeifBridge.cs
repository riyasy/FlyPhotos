using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX;

namespace FlyPhotos.Infra.Interop;

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
    /// Imports the native `ExtractPrimaryImage` function from `FlyNativeLibHeif.dll`.
    /// This function decodes the primary image from a HEIF file into 32-bit RGBA pixel format.
    /// </summary>
    /// <param name="heicPath">The file path to the HEIC/HEIF image.</param>
    /// <param name="outBuffer">An output <see cref="PixelBuffer"/> struct containing the pointer to the decoded pixel data and image metadata.</param>
    /// <returns>A <see cref="HeifError"/> indicating the success or failure of the operation.</returns>
    [LibraryImport(DllName, EntryPoint = "ExtractPrimaryImage", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractPrimaryImage(string heicPath, out PixelBuffer outBuffer);

    /// <summary>
    /// Imports the native `ExtractPrimaryImageAndAnimationStatus` function from `FlyNativeLibHeif.dll`.
    /// This function decodes the primary image from a HEIF file into 32-bit RGBA pixel format,
    /// and reports whether the file contains an animated sequence track.
    /// </summary>
    /// <param name="heicPath">The file path to the HEIC/HEIF image.</param>
    /// <param name="outBuffer">An output <see cref="PixelBuffer"/> struct containing the pointer to the decoded pixel data and image metadata.</param>
    /// <param name="outIsAnimated">An output boolean that is true if the file contains an animation sequence.</param>
    /// <returns>A <see cref="HeifError"/> indicating the success or failure of the operation.</returns>
    [LibraryImport(DllName, EntryPoint = "ExtractPrimaryImageAndAnimationStatus", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractPrimaryImageAndAnimationStatus(string heicPath, out PixelBuffer outBuffer,
        out byte outIsAnimated);

    /// <summary>
    /// Imports the native `ExtractThumbnail` function from `FlyNativeLibHeif.dll`.
    /// This function decodes the thumbnail image from a HEIF file into 32-bit RGBA pixel format.
    /// </summary>
    /// <param name="heicPath">The file path to the HEIC/HEIF image.</param>
    /// <param name="outBuffer">An output <see cref="PixelBuffer"/> struct containing the pointer to the decoded pixel data and image metadata.</param>
    /// <returns>A <see cref="HeifError"/> indicating the success or failure of the operation.</returns>
    [LibraryImport(DllName, EntryPoint = "ExtractThumbnail", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractThumbnail(string heicPath, out PixelBuffer outBuffer);

    /// <summary>
    /// Like <see cref="ExtractThumbnail"/>, but returns <see cref="HeifError.NoThumbnailFound"/> instead of
    /// generating one from the primary image when the file has no embedded thumbnail.
    /// </summary>
    [LibraryImport(DllName, EntryPoint = "ExtractEmbeddedThumbnail", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractEmbeddedThumbnail(string heicPath, out PixelBuffer outBuffer);

    /// <summary>
    /// Imports the native `FreePixelBuffer` function from `FlyNativeLibHeif.dll`.
    /// This critical function is responsible for freeing the unmanaged memory allocated by
    /// `ExtractPrimaryImage` or `ExtractThumbnail` to prevent memory leaks.
    /// </summary>
    /// <param name="buffer">A reference to the <see cref="PixelBuffer"/> struct whose `data` pointer needs to be freed.</param>
    [LibraryImport(DllName, EntryPoint = "FreePixelBuffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FreePixelBuffer(ref PixelBuffer buffer);

    /// <summary>
    /// Imports the native `ExtractPrimaryImageAndAnimationStatusFromMemory` function from `FlyNativeLibHeif.dll`.
    /// This function decodes the primary image from an in-memory byte array into 32-bit RGBA pixel format,
    /// and simultaneously checks if the file contains an animated sequence track.
    /// </summary>
    /// <param name="data">A pointer to the unmanaged memory block containing the raw file bytes.</param>
    /// <param name="size">The size of the unmanaged memory block in bytes.</param>
    /// <param name="outBuffer">An output <see cref="PixelBuffer"/> struct containing the pointer to the decoded pixel data and image metadata.</param>
    /// <param name="outIsAnimated">An output boolean that is true if the file contains an animation sequence.</param>
    /// <returns>A <see cref="HeifError"/> indicating the success or failure of the operation.</returns>
    [LibraryImport(DllName, EntryPoint = "ExtractPrimaryImageAndAnimationStatusFromMemory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial HeifError ExtractPrimaryImageAndAnimationStatusFromMemory(
        IntPtr data,
        nuint size,
        out PixelBuffer outBuffer,
        out byte outIsAnimated);
}

#endregion

/// <summary>
/// A managed C# wrapper for the native HeifDecoder DLL.
/// This class provides a safe, exception-driven, and easy-to-use interface for decoding HEIC/HEIF images.
/// Each result holds the native RGBA8 pixels (straight alpha) until it is disposed.
/// </summary>
public static class NativeHeifWrapper
{
    /// <summary>
    /// Decodes the primary image from a HEIC/HEIF file.
    /// </summary>
    /// <param name="filePath">The full path to the .heic, .heif or .hif file.</param>
    /// <returns>The decoded RGBA pixels and dimensions, or null if the image data is empty.</returns>
    /// <exception cref="Exception">Thrown if the native DLL returns an error code during decoding,
    /// indicating issues like file not found, decoding errors, or no primary image.</exception>
    public static NativeImage DecodePrimaryImage(string filePath) => DecodePrimaryImage(filePath, out _);

    /// <summary>
    /// Decodes the primary image from a HEIC/HEIF file, like <see cref="DecodePrimaryImage(string)"/>, and reports
    /// whether the file is an image sequence (an animation). The check reads libheif's already-parsed track list,
    /// so it costs nothing extra.
    /// </summary>
    /// <param name="filePath">The full path to the .heic, .heif, .hif or .avif file.</param>
    /// <param name="isAnimated">Output parameter indicating whether the file has animation sequence tracks.</param>
    /// <returns>The decoded RGBA pixels and dimensions, or null if the image data is empty.</returns>
    /// <exception cref="Exception">Thrown if the native DLL returns an error code during decoding.</exception>
    public static NativeImage DecodePrimaryImage(string filePath, out bool isAnimated)
    {
        HeifError result = NativeHeifBridge.ExtractPrimaryImageAndAnimationStatus(filePath,
            out NativeHeifBridge.PixelBuffer buffer, out byte isAnimatedByte);
        isAnimated = isAnimatedByte != 0;

        if (result != HeifError.Ok)
            throw new Exception($"Native HEIF decoder failed to decode primary image. Error: {result}");
        return Own(ref buffer);
    }

    /// <summary>
    /// Decodes the thumbnail image from a HEIC/HEIF file.
    /// </summary>
    /// <param name="filePath">The full path to the .heic, heif or .hif file.</param>
    /// <param name="embeddedOnly">
    /// Only an embedded thumbnail: null when the file has none, instead of a preview scaled from a full decode.
    /// For a caller that decodes the full image next anyway.
    /// </param>
    /// <returns>The decoded RGBA pixels and dimensions, or null if no thumbnail data is found or is empty.</returns>
    /// <exception cref="Exception">Thrown if the native DLL returns an error code during decoding,
    /// indicating issues like file not found, decoding errors, or no thumbnail found.</exception>
    public static NativeImage DecodeThumbnail(string filePath, bool embeddedOnly = false)
    {
        HeifError result = embeddedOnly
            ? NativeHeifBridge.ExtractEmbeddedThumbnail(filePath, out NativeHeifBridge.PixelBuffer buffer)
            : NativeHeifBridge.ExtractThumbnail(filePath, out buffer);

        if (embeddedOnly && result == HeifError.NoThumbnailFound) return null;
        if (result != HeifError.Ok)
            throw new Exception($"Native HEIF decoder failed to decode thumbnail. Error: {result}");
        return Own(ref buffer);
    }

    /// <summary>
    /// Decodes the primary image from an in-memory byte array.
    /// This skips I/O file locks and performs a simultaneous check for animation sequence tracks.
    /// </summary>
    /// <param name="fileData">The raw byte array containing the entire file.</param>
    /// <param name="isAnimated">Output parameter indicating whether the sequence has animation tracks.</param>
    /// <returns>The decoded RGBA pixels and dimensions, or null if empty.</returns>
    public static NativeImage DecodePrimaryImageFromMemory(byte[] fileData, out bool isAnimated)
    {
        isAnimated = false;
        if (fileData == null || fileData.Length == 0) return null;

        // Briefly pin the byte array so we can pass its pointer to C++
        GCHandle pinnedData = GCHandle.Alloc(fileData, GCHandleType.Pinned);
        try
        {
            IntPtr memoryPtr = pinnedData.AddrOfPinnedObject();
            HeifError result = NativeHeifBridge.ExtractPrimaryImageAndAnimationStatusFromMemory(
                memoryPtr,
                (nuint)fileData.Length,
                out NativeHeifBridge.PixelBuffer buffer,
                out byte outIsAnimatedByte);

            isAnimated = outIsAnimatedByte != 0;

            if (result != HeifError.Ok)
            {
                // Soft fail if the file is just totally broke. Let the caller deal with null.
                return null;
            }
            return Own(ref buffer);
        }
        finally
        {
            if (pinnedData.IsAllocated)
                pinnedData.Free();
        }
    }

    /// <summary>The decoded buffer as a <see cref="NativeImage"/>, which frees it; an empty one is freed here.</summary>
    private static NativeImage Own(ref NativeHeifBridge.PixelBuffer buffer)
    {
        if (buffer.data == IntPtr.Zero || buffer.dataSize == 0)
        {
            NativeHeifBridge.FreePixelBuffer(ref buffer);
            return null;
        }
        return new NativeImage(NativeImage.Owner.Heif, DirectXPixelFormat.R8G8B8A8UIntNormalized, buffer.data,
            buffer.dataSize, buffer.width, buffer.height, buffer.primaryImageWidth, buffer.primaryImageHeight);
    }
}