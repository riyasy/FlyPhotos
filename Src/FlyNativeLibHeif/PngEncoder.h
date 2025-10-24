/**
 * @file example_encoder_png.h
 * @brief Defines a PngEncoder class for saving heif_image data as a PNG file.
 */

#pragma once
#ifndef EXAMPLE_ENCODER_PNG_H
#define EXAMPLE_ENCODER_PNG_H

#include <string>
#include <libheif/heif.h>

 /// @brief A helper class to encode a `heif_image` into a PNG file.
class PngEncoder
{
public:
    /// @brief Constructs a PngEncoder with default settings.
    PngEncoder();

    /// @brief Sets the ZLIB compression level for the PNG output.
    /// @param level Compression level: 0 (fastest) to 9 (best). Use -1 for the default.
    void set_compression_level(int level) {
        m_compression_level = level;
    }

    /// @brief Gets the colorspace for PNG encoding (always RGB).
    /// @param has_alpha Indicates if the source image has an alpha channel.
    /// @return The `heif_colorspace` for PNG output.
    heif_colorspace colorspace(bool has_alpha) const
    {
        return heif_colorspace_RGB;
    }

    /// @brief Gets the recommended chroma format based on bit depth and alpha.
    /// @param has_alpha True if the source image has an alpha channel.
    /// @param bit_depth The bit depth of the source image (e.g., 8 or 16).
    /// @return The appropriate interleaved `heif_chroma` format.
    heif_chroma chroma(bool has_alpha, int bit_depth) const
    {
        if (bit_depth <= 8) {
            if (has_alpha)
                return heif_chroma_interleaved_RGBA;
            else
                return heif_chroma_interleaved_RGB;
        }
        else {
            if (has_alpha)
                return heif_chroma_interleaved_RRGGBBAA_BE;
            else
                return heif_chroma_interleaved_RRGGBB_BE;
        }
    }

    /// @brief Encodes the given image data and saves it to a PNG file.
    /// @param image The decoded heif_image containing the pixel data to encode.
    /// @param width The width of the image.
    /// @param height The height of the image.
    /// @param filename The path to the output PNG file.
    /// @return True on success, false on failure.
    bool Encode(const heif_image* image, int width, int height, const std::string& filename);

private:
    /// @brief The ZLIB compression level for PNG encoding.
    int m_compression_level = -1;
};

#endif  // EXAMPLE_ENCODER_PNG_H