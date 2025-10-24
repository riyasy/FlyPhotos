/**
 * @file native_exports.h
 * @brief Defines the C-style P/Invoke API for the HeifReader library.
 *
 * This header declares the functions exported from the DLL for use by other
 * languages like C#. It uses extern "C" to prevent C++ name mangling.
 */

#pragma once

#ifndef NATIVE_EXPORTS_H
#define NATIVE_EXPORTS_H

#include <Windows.h>
#include <cstdint> // For uint8_t
#include "HeifReader.h" // Provides HeifError and PixelBuffer definitions

#ifdef __cplusplus
extern "C" {
#endif

    /// @brief Decodes the primary HEIC image into a raw BGRA pixel buffer.
    /// @param heic_path Path to the input .heic file (UTF-16).
    /// @param out_buffer Pointer to a struct to receive the decoded image data.
    /// @return A HeifError code indicating the result.
    /// @note The caller MUST call FreePixelBuffer() on the out_buffer to prevent a memory leak.
    __declspec(dllexport) HeifError ExtractPrimaryImageBGRA(const wchar_t* heic_path, PixelBuffer* out_buffer);

    /// @brief Decodes the thumbnail from a HEIC image into a raw BGRA pixel buffer.
    /// @param heic_path Path to the input .heic file (UTF-16).
    /// @param out_buffer Pointer to a struct to receive the decoded image data.
    /// @return A HeifError code indicating the result.
    /// @note The caller MUST call FreePixelBuffer() on the out_buffer to prevent a memory leak.
    __declspec(dllexport) HeifError ExtractThumbnailBGRA(const wchar_t* heic_path, PixelBuffer* out_buffer);

    /// @brief Frees the native memory allocated within a PixelBuffer struct.
    /// @param buffer Pointer to the PixelBuffer whose internal data buffer needs to be freed.
    __declspec(dllexport) void FreePixelBuffer(PixelBuffer* buffer);

#ifdef __cplusplus
}
#endif

#endif // NATIVE_EXPORTS_H