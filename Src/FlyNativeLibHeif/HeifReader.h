/**
 * @file heif_reader.h
 * @brief Defines the HeifReader class for reading and decoding HEIC/HEIF images.
 */

#ifndef HEIF_READER_H
#define HEIF_READER_H

#include <string>
#include <libheif/heif.h>
#include <vector>

 /// @brief A RAII wrapper to manage libheif's global initialization and deinitialization.
class LibHeifInitializer {
public:
    LibHeifInitializer() { heif_init(nullptr); }
    ~LibHeifInitializer() { heif_deinit(); }
};

/// @brief Error codes for HEIF reading operations.
enum class HeifError {
    Ok = 0,               ///< Operation was successful.
    FileNotFound,         ///< The input file was not found.
    FileReadError,        ///< Could not read the input file.
    NoPrimaryImage,       ///< The file has no primary image.
    NoThumbnailFound,     ///< The file has no thumbnail.
    ThumbnailReadError,   ///< Failed to read the thumbnail.
    ImageDecodeError,     ///< Failed to decode the image.
    PngEncodeError,       ///< Failed to encode the output PNG.
    InvalidInput          ///< An input parameter was invalid.
};

/// @brief A C-style struct to pass raw image data to C#.
/// @note The `data` buffer is allocated in C++ and must be freed by the caller.
struct PixelBuffer {
    uint8_t* data;            ///< Pointer to the raw BGRA pixel data.
    int dataSize;             ///< Total size of the data buffer in bytes.
    int width;                ///< Width of the decoded image in pixels.
    int height;               ///< Height of the decoded image in pixels.
    int primaryImageWidth;    ///< Width of the original primary image (useful for thumbnails).
    int primaryImageHeight;   ///< Height of the original primary image (useful for thumbnails).
};

/// @brief A class to read and decode HEIC/HEIF image files.
class HeifReader {
public:
    HeifReader();
    ~HeifReader();

	/// @brief Extracts the thumbnail into a raw BGRA pixel buffer. If no thumbnail exists, generates one from primary image.
    HeifError ExtractThumbnailBGRA(const std::string& input_filename, PixelBuffer& out_buffer);

    /// @brief Extracts the primary image into a raw BGRA pixel buffer.
    HeifError ExtractPrimaryImageBGRA(const std::string& input_filename, PixelBuffer& out_buffer);

private:
    ///@brief Internal helper to decode any image handle into a BGRA buffer.
    HeifError ExtractImageToBGRA(heif_image_handle* image_handle, PixelBuffer& out_buffer);

    ///@brief [NEW HELPER] Fills a PixelBuffer from a decoded heif_image.
    void FillPixelBufferFromImage(const heif_image* image, int width, int height, PixelBuffer& out_buffer);

    /// @brief Manages libheif's lifecycle.
    LibHeifInitializer heif_initializer_;
};

#endif // HEIF_READER_H