#nullable enable
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlyPhotos.Display.ImageReading;

namespace FlyPhotos.Infra.Interop;

/// <summary>
/// Low-level P/Invoke declarations for the Rust rawler_bridge DLL.
/// All interop entry-points are collected here; higher-level logic lives in <see cref="RawlerWrapper"/>.
/// </summary>
internal static partial class RawlerBridge
{
    private const string DllName = "fly_rust_bridge.dll";

    // ── Image decoding ────────────────────────────────────────────────────────

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint get_hq_image(string path, out int width, out int height, out int rotation);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint get_embedded_preview(string path, out int width, out int height, out int rotation, out int primaryWidth, out int primaryHeight);

    // ── Memory management ─────────────────────────────────────────────────────

    /// <summary>
    /// Frees a pixel buffer previously returned by <c>get_hq_image</c> or
    /// <c>get_embedded_preview</c>.  Do NOT use this for format-list buffers.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void free_rust_buffer(nint ptr, nuint size);

    // ── Format discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns a heap-allocated array of null-terminated C strings listing every
    /// RAW file extension that rawler can decode (e.g. "ARW", "CR2", …).
    /// The number of entries is written to <paramref name="count"/>.
    /// </summary>
    /// <remarks>
    /// The returned strings are UPPER-CASE and do NOT include a leading dot.
    /// The caller MUST free the pointer with <see cref="free_formats_buffer"/>.
    /// </remarks>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint get_supported_formats(out int size);

    /// <summary>
    /// Frees a buffer previously returned by <see cref="get_supported_formats"/>.
    /// <paramref name="size"/> MUST be the same value that was written to the
    /// <c>size</c> out-param of <c>get_supported_formats</c>.
    /// </summary>
    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void free_formats_buffer(nint ptr, int size);
}