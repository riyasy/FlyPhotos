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

    // --- AVIF Animation Exports ---

    /// @brief Opens an AVIF/HEIF animation file from memory and caches its frame metadata.
    /// @param data Pointer to the file data in memory.
    /// @param size Size of the file data in bytes.
    /// @return An opaque handle to the animation context `AnimatedAvifReader`, or nullptr on failure. This handle must be passed to subsequent Avif export functions.
    __declspec(dllexport) void* OpenAvifAnimation(const uint8_t* data, size_t size);

    /// @brief Checks quickly if the given context handle holds an animated sequence.
    /// @param handle Opaque handle to the `AnimatedAvifReader` context.
    /// @return True if it contains a sequence track, false otherwise.
    __declspec(dllexport) bool IsAvifAnimated(void* handle);

    /// @brief Retrieves the width of the animated AVIF canvas.
    /// @param handle Opaque handle to the `AnimatedAvifReader` context.
    /// @return The width in pixels, or 0 if invalid.
    __declspec(dllexport) int GetAvifCanvasWidth(void* handle);

    /// @brief Retrieves the height of the animated AVIF canvas.
    /// @param handle Opaque handle to the `AnimatedAvifReader` context.
    /// @return The height in pixels, or 0 if invalid.
    __declspec(dllexport) int GetAvifCanvasHeight(void* handle);
    
    /// @brief Decodes the next sequence frame into a pre-allocated BGRA buffer and returns its duration in ms.
    /// @param handle Opaque handle to the `AnimatedAvifReader` context.
    /// @param out_bgra_buffer A pre-allocated buffer of size Width * Height * 4. This ensures a 0-allocation decode path.
    /// @return The duration of the decoded frame in ms, or 0 if EOF or an error occurred.
    __declspec(dllexport) int DecodeNextAvifFrame(void* handle, uint8_t* out_bgra_buffer);

    /// @brief Resets the animation track to the beginning to loop the animation continuously.
    /// @param handle Opaque handle to the `AnimatedAvifReader` context.
    __declspec(dllexport) void ResetAvifAnimation(void* handle);

    /// @brief Closes the animation and releases all libheif associated memory for the given context.
    /// @param handle Opaque handle to the `AnimatedAvifReader` context.
    __declspec(dllexport) void CloseAvifAnimation(void* handle);

#ifdef __cplusplus
}
#endif

#endif // NATIVE_EXPORTS_H