#include "pch.h"
#include "BGRAEncoder.h"

/**
 * @brief Converts a decoded heif_image into a 32-bit BGRA pixel buffer.
 *
 * This method reads the source interleaved RGBA/RGB data provided by libheif.
 * It then iterates through each pixel, re-ordering ("swizzling") the R and B
 * channels to produce the BGRA format. If the source image lacks an alpha
 * channel, it is set to fully opaque (255).
 *
 * @param image The source heif_image, already decoded.
 * @param width The width of the image in pixels.
 * @param height The height of the image in pixels.
 * @param out_buffer A pre-allocated buffer that will receive the BGRA data.
 */
void BGRAEncoder::Encode(const heif_image* image, int width, int height, uint8_t* out_buffer) {
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

    // Determine if the source has an alpha channel to know the pixel size (3 or 4 bytes).
    const bool has_alpha = (heif_image_get_chroma_format(image) == heif_chroma_interleaved_RGBA);
    const int src_bytes_per_pixel = has_alpha ? 4 : 3;

    // Get a pointer to the start of our destination buffer.
    uint8_t* dest_pixel = out_buffer;

    // Iterate over each row and pixel to perform the conversion.
    for (int y = 0; y < height; ++y) {
        // Get a pointer to the beginning of the current source row using the stride.
        const uint8_t* src_row = src_data + (y * stride);
        for (int x = 0; x < width; ++x) {
            // Get a pointer to the current source pixel.
            const uint8_t* src_pixel = src_row + (x * src_bytes_per_pixel);

            // Swizzle from RGBA/RGB to BGRA and write directly into the destination buffer.
            dest_pixel[0] = src_pixel[2]; // Blue
            dest_pixel[1] = src_pixel[1]; // Green
            dest_pixel[2] = src_pixel[0]; // Red
            dest_pixel[3] = has_alpha ? src_pixel[3] : 255; // Alpha (or opaque if none)

            // Advance the destination pointer by 4 bytes (one BGRA pixel).
            dest_pixel += 4;
        }
    }
}