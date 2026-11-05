/**
 * @file DdsReader.h
 * @brief Decodes DDS textures to premultiplied BGRA8 with DirectXTex.
 */

#pragma once

#include <cstdint>

/// @brief Error codes for DDS decoding. Mirrored by DdsError in NativeDdsBridge.cs.
enum class DdsError {
    Ok = 0,          ///< Decoded.
    InvalidInput,    ///< A null argument.
    FileReadError,   ///< Missing file, or a header DirectXTex rejects (malformed or unsupported format).
    DecodeError,     ///< Decompress / convert / resize / premultiply failed.
    OutOfMemory      ///< Could not allocate the output buffer (or it is over 2 GB).
};

/// @brief A C-style struct to pass raw image data to C#. Same layout as FlyNativeLibHeif's PixelBuffer.
/// @note `data` is allocated with new[] here and must be released with FreePixelBuffer().
struct PixelBuffer {
    uint8_t* data;            ///< Premultiplied BGRA8 pixels, rows packed (stride = width * 4).
    int dataSize;             ///< Total size of the data buffer in bytes.
    int width;                ///< Width of the decoded image (a smaller mip level for previews).
    int height;               ///< Height of the decoded image.
    int primaryImageWidth;    ///< Width of the texture's top mip level.
    int primaryImageHeight;   ///< Height of the texture's top mip level.
};

/// @brief Decodes the first image of a DDS file (face 0 of a cubemap, slice 0 of a volume).
/// @param path UTF-16 file path.
/// @param maxDimension 0 for the full-size top mip. Otherwise the result's longer side is at most
///        this: the smallest stored mip level that is still at least this big is used, then scaled
///        down if it is larger (a texture without mips is scaled from the top level).
/// @param out Receives the pixels; zeroed on failure.
DdsError DecodeDds(const wchar_t* path, int maxDimension, PixelBuffer& out);
