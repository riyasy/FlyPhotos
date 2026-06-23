/**
 * @file PixelBufferEncoder.h
 * @brief Defines the PixelBufferEncoder class for copying heif_image data into a packed pixel buffer.
 */

#pragma once
#ifndef PIXEL_BUFFER_ENCODER_H
#define PIXEL_BUFFER_ENCODER_H

#include <vector>
#include <cstdint> // For uint8_t
#include <libheif/heif.h>

#pragma comment(lib, "heif.lib")

 /// @brief Copies a decoded `heif_image` into a raw 32-bit RGBA pixel buffer.
 /// @note The source is decoded as interleaved RGBA and uploaded as R8G8B8A8 on the
 ///       managed side, so this is a straight copy (no channel swap). The name is
 ///       format-agnostic on purpose: the destination byte order is whatever the managed
 ///       CreateFromBytes call declares.
class PixelBufferEncoder {
public:
    /// @brief Constructs a new PixelBufferEncoder.
    PixelBufferEncoder() = default;

    /// @brief Fills a user-provided buffer with RGBA pixel data from a `heif_image`.
    /// @param image The decoded heif_image containing the source pixels.
    /// @param width The width of the image.
    /// @param height The height of the image.
    /// @param out_buffer Pointer to a pre-allocated buffer to receive the RGBA data.
    ///                   This buffer must be at least `width * height * 4` bytes in size.
    void Encode(const heif_image* image, int width, int height, uint8_t* out_buffer);
};

#endif // PIXEL_BUFFER_ENCODER_H
