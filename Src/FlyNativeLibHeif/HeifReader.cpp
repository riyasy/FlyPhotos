#include "pch.h"

#include "HeifReader.h"
#include <iostream>
#include <memory>
#include <cassert>
#include "BGRAEncoder.h"


HeifReader::HeifReader() {
    // The heif_initializer_ member handles initialization automatically.
}

HeifReader::~HeifReader() {
    // The heif_initializer_ member handles deinitialization automatically.
}

/**
 * @brief Extracts the thumbnail and decodes it into a raw BGRA buffer.
 * If no embedded thumbnail is found, it generates one by scaling the primary image.
 */
HeifError HeifReader::ExtractThumbnailBGRA(const std::string& input_filename, PixelBuffer& out_buffer) {
    // 1. Open the file and create the context.
    std::shared_ptr<heif_context> context(heif_context_alloc(),
        [](heif_context* c) { heif_context_free(c); });

    heif_error err = heif_context_read_from_file(context.get(), input_filename.c_str(), nullptr);
    if (err.code != 0) {
        return HeifError::FileReadError;
    }

    // 2. Get the primary image handle, needed for dimensions and thumbnail lookup.
    heif_image_handle* primary_image_handle = nullptr;
    err = heif_context_get_primary_image_handle(context.get(), &primary_image_handle);
    if (err.code) {
        return HeifError::NoPrimaryImage;
    }
    // Manage its lifetime since we'll need it throughout this function.
    std::shared_ptr<heif_image_handle> primary_handle_guard(primary_image_handle, heif_image_handle_release);

    // 3. Set the primary image dimensions in the output buffer for the caller.
    out_buffer.primaryImageWidth = heif_image_handle_get_width(primary_image_handle);
    out_buffer.primaryImageHeight = heif_image_handle_get_height(primary_image_handle);

    // 4. Try to find and retrieve an embedded thumbnail handle.
    heif_item_id thumbnail_id;
    if (heif_image_handle_get_list_of_thumbnail_IDs(primary_image_handle, &thumbnail_id, 1) > 0) {
        heif_image_handle* thumbnail_handle = nullptr;
        err = heif_image_handle_get_thumbnail(primary_image_handle, thumbnail_id, &thumbnail_handle);
        if (err.code == 0) {
            // Success: an embedded thumbnail was found and retrieved.
            // Delegate to the helper, which will take ownership of thumbnail_handle.
            HeifError thumb_decode_result = ExtractImageToBGRA(thumbnail_handle, out_buffer);
            if (thumb_decode_result == HeifError::Ok) {
                // SUCCESS: The embedded thumbnail was found, retrieved, AND decoded successfully.
                // We are done, so we can return immediately.
                return HeifError::Ok;
            }
            else {
                // FAILURE: The embedded thumbnail was corrupt or could not be decoded.
                // We log this specific issue and then allow the code to "fall through"
                std::cerr << "Note: Embedded thumbnail found but failed to decode. Generating a new one." << std::endl;
            }
        }
        // If getting the thumbnail failed, we fall through to the generation logic below.
    }

    // --- FALLBACK LOGIC: No embedded thumbnail found, so we generate one. ---

    // 5. First, we must decode the full primary image.
    heif_image* primary_image = nullptr;
    err = heif_decode_image(primary_image_handle, &primary_image, heif_colorspace_RGB, heif_chroma_interleaved_RGBA, nullptr);
    if (err.code) {
        return HeifError::ImageDecodeError;
    }
    // Ensure the decoded primary image is released when we're done with it.
    std::shared_ptr<heif_image> primary_image_guard(primary_image, heif_image_release);

    // 6. Check if the primary image needs to be scaled.
    const int primary_w = out_buffer.primaryImageWidth;
    const int primary_h = out_buffer.primaryImageHeight;
    const int longest_side = std::max(primary_w, primary_h);

    if (longest_side <= 800) {
        // The primary image is small enough, use it directly as the thumbnail.
        FillPixelBufferFromImage(primary_image, primary_w, primary_h, out_buffer);
        return HeifError::Ok;
    }
    else {
        // The primary image is large, so we scale it down.
        int thumb_w, thumb_h;
        if (primary_w > primary_h) {
            thumb_w = 800;
            thumb_h = static_cast<int>(primary_h * (800.0 / primary_w));
        }
        else {
            thumb_h = 800;
            thumb_w = static_cast<int>(primary_w * (800.0 / primary_h));
        }

        heif_image* scaled_image = nullptr;
        err = heif_image_scale_image(primary_image, &scaled_image, thumb_w, thumb_h, nullptr);
        if (err.code) {
            return HeifError::ImageDecodeError; // Scaling failed
        }
        // Ensure the newly created scaled image is released.
        std::shared_ptr<heif_image> scaled_image_guard(scaled_image, heif_image_release);

        // Fill the output buffer using the scaled image data.
        FillPixelBufferFromImage(scaled_image, thumb_w, thumb_h, out_buffer);
        return HeifError::Ok;
    }
}

/**
 * @brief Extracts the primary image and decodes it into a raw BGRA buffer.
 */
HeifError HeifReader::ExtractPrimaryImageBGRA(const std::string& input_filename, PixelBuffer& out_buffer) {
    // 1. Open the file and create the context.
    std::shared_ptr<heif_context> context(heif_context_alloc(),
        [](heif_context* c) { heif_context_free(c); });

    heif_error err = heif_context_read_from_file(context.get(), input_filename.c_str(), nullptr);
    if (err.code != 0) {
        return HeifError::FileReadError;
    }

    // 2. Get the primary image handle.
    heif_image_handle* primary_image_handle = nullptr;
    err = heif_context_get_primary_image_handle(context.get(), &primary_image_handle);
    if (err.code) {
        return HeifError::NoPrimaryImage;
    }

    // 3. Set the primary image dimensions. For the primary image, these are the same as its own dimensions.
    out_buffer.primaryImageWidth = heif_image_handle_get_width(primary_image_handle);
    out_buffer.primaryImageHeight = heif_image_handle_get_height(primary_image_handle);

    // 4. Delegate the decoding and buffer allocation to the shared helper function.
    //    The helper will take ownership of the primary_image_handle.
    return ExtractImageToBGRA(primary_image_handle, out_buffer);
}

/**
 * @brief Private helper to decode any image handle into a BGRA buffer.
 * This function contains the common logic for decoding, buffer allocation, and BGRA conversion.
 */
HeifError HeifReader::ExtractImageToBGRA(heif_image_handle* image_handle, PixelBuffer& out_buffer) {
    // Take ownership of the incoming handle and ensure it's released upon exit.
    std::shared_ptr<heif_image_handle> handle_guard(image_handle, heif_image_handle_release);

    // Decode the image handle into a raw heif_image object.
    heif_image* image = nullptr;
    heif_error err = heif_decode_image(image_handle, &image, heif_colorspace_RGB, heif_chroma_interleaved_RGBA, nullptr);

    if (err.code) {
        return HeifError::ImageDecodeError;
    }
    // Manage the decoded image's lifetime.
    std::shared_ptr<heif_image> image_guard(image, heif_image_release);

    // Get dimensions and delegate to the new buffer-filling helper.
    const int width = heif_image_handle_get_width(image_handle);
    const int height = heif_image_handle_get_height(image_handle);
    FillPixelBufferFromImage(image, width, height, out_buffer);

    return HeifError::Ok;
}

/**
 * @brief [NEW HELPER] Fills a PixelBuffer from a decoded heif_image.
 * This function contains the common logic for allocating the buffer and
 * using BGRAEncoder to fill it with pixel data.
 */
void HeifReader::FillPixelBufferFromImage(const heif_image* image, int width, int height, PixelBuffer& out_buffer) {
    // Set the dimensions for the output buffer.
    out_buffer.width = width;
    out_buffer.height = height;

    // Allocate the final raw buffer on the heap. Ownership is transferred to the caller.
    out_buffer.dataSize = width * height * 4;
    if (out_buffer.dataSize == 0) {
        out_buffer.data = nullptr; // Ensure data is null for zero-pixel images
        return;
    }
    out_buffer.data = new uint8_t[out_buffer.dataSize];

    // Use the BGRAEncoder to fill the allocated buffer.
    BGRAEncoder encoder;
    encoder.Encode(image, width, height, out_buffer.data);
}