#include "pch.h"

#include <cerrno>
#include <png.h>
#include <cstring>
#include <cstdlib>
#include <vector>
#include <cstdio> // Required for FILE, fprintf, etc.

#include "PngEncoder.h"

PngEncoder::PngEncoder() = default;

/**
 * @brief Encodes a decoded heif_image into a PNG file.
 * This function uses libpng to perform the encoding and write the file to disk.
 */
bool PngEncoder::Encode(const struct heif_image* image,
    int width, int height, // Use these parameters for the output dimensions
    const std::string& filename)
{
    // Initialize the main libpng structures for writing.
    png_structp png_ptr = png_create_write_struct(PNG_LIBPNG_VER_STRING, nullptr,
        nullptr, nullptr);
    if (!png_ptr) {
        fprintf(stderr, "libpng initialization failed (1)\n");
        return false;
    }

    png_infop info_ptr = png_create_info_struct(png_ptr);
    if (!info_ptr) {
        png_destroy_write_struct(&png_ptr, nullptr);
        fprintf(stderr, "libpng initialization failed (2)\n");
        return false;
    }

    // Set a custom ZLIB compression level if one was specified.
    if (m_compression_level != -1) {
        png_set_compression_level(png_ptr, m_compression_level);
    }

    // --- Use fopen_s for secure file opening in binary write mode. ---
    FILE* fp;
    errno_t err = fopen_s(&fp, filename.c_str(), "wb");
    if (err != 0) {
        // --- Use strerror_s to get a thread-safe error message. ---
        char error_buffer[256];
        strerror_s(error_buffer, sizeof(error_buffer), err);
        fprintf(stderr, "Can't open %s: %s\n", filename.c_str(), error_buffer);
        png_destroy_write_struct(&png_ptr, &info_ptr);
        return false;
    }

    // Set up libpng's error handling. If an error occurs in a libpng function,
    // it will longjmp to this point, allowing for proper cleanup.
    if (setjmp(png_jmpbuf(png_ptr))) {
        png_destroy_write_struct(&png_ptr, &info_ptr);
        fclose(fp);
        fprintf(stderr, "Error while encoding image\n");
        return false;
    }

    // Associate the file pointer with the png write structure.
    png_init_io(png_ptr, fp);

    // Determine if the source image has an alpha channel.
    bool withAlpha = (heif_image_get_chroma_format(image) == heif_chroma_interleaved_RGBA ||
        heif_image_get_chroma_format(image) == heif_chroma_interleaved_RRGGBBAA_BE);

    // --- MODIFIED: These two lines are REMOVED ---
    // The width and height are now passed as parameters instead of being read from the image.
    // int width = heif_image_get_width(image, heif_channel_interleaved);
    // int height = heif_image_get_height(image, heif_channel_interleaved);

    // Determine the bit depth for the output PNG (8 or 16).
    int bitDepth;
    int input_bpp = heif_image_get_bits_per_pixel_range(image, heif_channel_interleaved);
    if (input_bpp > 8) {
        bitDepth = 16;
    }
    else {
        bitDepth = 8;
    }

    // Set the color type based on whether an alpha channel is present.
    const int colorType = withAlpha ? PNG_COLOR_TYPE_RGBA : PNG_COLOR_TYPE_RGB;

    // We now use the passed-in width and height to set the PNG header (IHDR chunk).
    png_set_IHDR(png_ptr, info_ptr, width, height, bitDepth, colorType,
        PNG_INTERLACE_NONE, PNG_COMPRESSION_TYPE_BASE, PNG_FILTER_TYPE_BASE);

    // Write the PNG header information to the file.
    png_write_info(png_ptr, info_ptr);

    // Use a std::vector for automatic memory management of the row pointers array.
    // libpng requires an array of pointers, where each pointer points to the start of a row.
    std::vector<png_bytep> row_pointers(height);

    // Get a pointer to the raw pixel data and its stride (bytes per row).
    int stride;
    const uint8_t* data = heif_image_get_plane_readonly(image, heif_channel_interleaved, &stride);

    // This loop populates the row_pointers vector with the correct starting address for each row.
    // The stride is used to correctly calculate the offset for each row.
    for (int y = 0; y < height; ++y) {
        row_pointers[y] = const_cast<png_bytep>(data + y * stride);
    }

    // Write the entire image's pixel data to the file.
    png_write_image(png_ptr, row_pointers.data());

    // Write the end of the PNG file and perform cleanup.
    png_write_end(png_ptr, nullptr);
    png_destroy_write_struct(&png_ptr, &info_ptr);
    fclose(fp);
    return true;
}