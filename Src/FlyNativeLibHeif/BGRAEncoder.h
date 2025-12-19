/**
 * @file BGRAEncoder.h
 * @brief Defines the BGRAEncoder class for converting heif_image data to a BGRA buffer.
 */

#pragma once
#ifndef BGRA_ENCODER_H
#define BGRA_ENCODER_H

#include <vector>
#include <cstdint> // For uint8_t
#include <libheif/heif.h>

#pragma comment(lib, "heif.lib")

 /// @brief Converts a decoded `heif_image` into a raw 32-bit BGRA pixel buffer.
 /// @note This class is optimized for interoperability with Windows graphics APIs.
class BGRAEncoder {
public:
    /// @brief Constructs a new BGRAEncoder.
    BGRAEncoder() = default;

    /// @brief Fills a user-provided buffer with BGRA pixel data from a `heif_image`.
    /// @param image The decoded heif_image containing the source pixels.
    /// @param width The width of the image.
    /// @param height The height of the image.
    /// @param out_buffer Pointer to a pre-allocated buffer to receive the BGRA data.
    ///                   This buffer must be at least `width * height * 4` bytes in size.
    void Encode(const heif_image* image, int width, int height, uint8_t* out_buffer);
};

#endif // BGRA_ENCODER_H