#include "pch.h"
#include "PixelBufferEncoder.h"
#include <cstring> // For memcpy

/**
 * @brief Copies a decoded heif_image's interleaved pixels into a tightly-packed
 *        32-bit RGBA buffer.
 *
 * libheif is asked to decode into interleaved RGBA (see HeifReader /
 * AnimatedAvifReader), and the managed side now uploads the result as
 * R8G8B8A8UIntNormalized, so no channel swap is required: the common path is a
 * straight row-wise copy (a single contiguous copy when the source has no row
 * padding). The rare interleaved-RGB source is expanded to RGBA with an opaque
 * alpha, preserving channel order.
 *
 * @param image The source heif_image, already decoded.
 * @param width The width of the image in pixels.
 * @param height The height of the image in pixels.
 * @param out_buffer A pre-allocated buffer (>= width * height * 4 bytes) that
 *                   receives the RGBA data.
 */
/* static */ void PixelBufferEncoder::Encode(const heif_image* image, int width, int height, uint8_t* out_buffer) {
    // Ensure the output buffer is valid before proceeding.
    if (!out_buffer) {
        return;
    }

    // Get a read-only pointer to the source image's interleaved pixel data.
    // The 'stride' is the number of bytes per row, which may include padding.
    int stride = 0;
    const uint8_t* src_data = heif_image_get_plane_readonly(image, heif_channel_interleaved, &stride);
    if (!src_data) {
        return;
    }

    // Determine if the source has an alpha channel (4 bytes/pixel) or is plain RGB (3).
    const bool has_alpha = (heif_image_get_chroma_format(image) == heif_chroma_interleaved_RGBA);
    const int dst_row_bytes = width * 4;

    if (has_alpha) {
        // RGBA -> RGBA: no per-pixel work, just a copy. When the source row stride matches
        // the destination row size there is no padding, so the whole image is one block.
        if (stride == dst_row_bytes) {
            memcpy(out_buffer, src_data, static_cast<size_t>(dst_row_bytes) * height);
        }
        else {
            for (int y = 0; y < height; ++y) {
                memcpy(out_buffer + static_cast<size_t>(y) * dst_row_bytes,
                       src_data + static_cast<size_t>(y) * stride,
                       dst_row_bytes);
            }
        }
    }
    else {
        // RGB -> RGBA: expand each pixel, keeping channel order and forcing opaque alpha.
        for (int y = 0; y < height; ++y) {
            const uint8_t* src_row = src_data + static_cast<size_t>(y) * stride;
            uint8_t* dst_row = out_buffer + static_cast<size_t>(y) * dst_row_bytes;
            for (int x = 0; x < width; ++x) {
                dst_row[x * 4 + 0] = src_row[x * 3 + 0]; // Red
                dst_row[x * 4 + 1] = src_row[x * 3 + 1]; // Green
                dst_row[x * 4 + 2] = src_row[x * 3 + 2]; // Blue
                dst_row[x * 4 + 3] = 255;                // Alpha (opaque)
            }
        }
    }
}
