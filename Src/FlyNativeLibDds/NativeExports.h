/**
 * @file NativeExports.h
 * @brief The C-style P/Invoke API of FlyNativeLibDds (see NativeDdsBridge.cs).
 */

#pragma once

#include "DdsReader.h" // DdsError and PixelBuffer

#ifdef __cplusplus
extern "C" {
#endif

    /// @brief Decodes a DDS file into premultiplied BGRA8.
    /// @param dds_path Path to the .dds file (UTF-16).
    /// @param max_dimension 0 for the full image; otherwise the longer side of a preview (see DecodeDds).
    /// @param out_buffer Receives the pixels; zeroed on failure.
    /// @note The caller MUST call FreePixelBuffer() on out_buffer after a successful call.
    __declspec(dllexport) DdsError ExtractDdsImage(const wchar_t* dds_path, int max_dimension, PixelBuffer* out_buffer);

    /// @brief Frees the pixel data allocated by ExtractDdsImage. Safe to call twice.
    __declspec(dllexport) void FreePixelBuffer(PixelBuffer* buffer);

#ifdef __cplusplus
}
#endif
