#include "pch.h"
#include "AnimatedAvifReader.h"
#include "BGRAEncoder.h"
#include "DllGlobals.h"

/**
 * @brief Default constructor for AnimatedAvifReader.
 */
AnimatedAvifReader::AnimatedAvifReader() {
}

/**
 * @brief Destructor. Releases the active heif_track and heif_image resources safely.
 */
AnimatedAvifReader::~AnimatedAvifReader() {
    if (track) heif_track_release(track);
    if (current_image) heif_image_release(current_image);
}

/**
 * @brief Opens the AVIF container from a zero-allocation memory buffer.
 * @param data The raw file bytes.
 * @param size The size of the file.
 * @return True if a valid image or animation track is discovered, otherwise false.
 */
bool AnimatedAvifReader::Open(const uint8_t* data, size_t size) {
    if (!data || size == 0) return false;

    // Allocate a heif_context with a custom deleter to ensure proper cleanup
    context = std::shared_ptr<heif_context>(heif_context_alloc(), [](heif_context* c) { heif_context_free(c); });
    
    // Read directly from the provided memory without making any copies to maintain zero-allocation
    heif_error err = heif_context_read_from_memory_without_copy(context.get(), data, size, nullptr);
    if (err.code != 0) {
        return false;
    }

    // Cache the data for Reset()
    cached_data = data;
    cached_size = size;

    // Attempt to locate an actual animation sequence track
    int num_seqs = heif_context_number_of_sequence_tracks(context.get());
    if (num_seqs > 0) {
        std::vector<uint32_t> track_ids(num_seqs);
        heif_context_get_track_ids(context.get(), track_ids.data());
        track_id = track_ids[0]; // Store the original track ID so we can reset correctly on a loop
        track = heif_context_get_track(context.get(), track_id);
    }
    
    if (track) {
        // We found an animation track, extract dimensions
        uint16_t w, h;
        err = heif_track_get_image_resolution(track, &w, &h);
        if (err.code == 0) {
            width = w;
            height = h;
        }
    } else {
        // Fallback for non-animated AVIF/HEIC files (reads the top-level master image bounding box)
        int num_images = heif_context_get_number_of_top_level_images(context.get());
        if (num_images > 0) {
            heif_item_id head_id = 0;
            heif_context_get_list_of_top_level_image_IDs(context.get(), &head_id, 1);
            heif_image_handle* handle = nullptr;
            heif_context_get_image_handle(context.get(), head_id, &handle);
            if (handle) {
                width = heif_image_handle_get_width(handle);
                height = heif_image_handle_get_height(handle);
                heif_image_handle_release(handle);
            }
        }
    }

    if (width == 0 || height == 0) {
        if (track) {
            heif_track_release(track);
            track = nullptr;
        }
        return false;
    }

    return true;
}

/**
 * @brief Determines if the loaded context possesses an actual animation sequence track.
 * @return True if the file contains an animated track, false if it is a static image.
 */
bool AnimatedAvifReader::IsAnimated() const {
    if (!context) return false;
    return heif_context_has_sequence(context.get()) || heif_context_number_of_sequence_tracks(context.get()) > 0;
}

/**
 * @brief Retrieves the parsed width of the sequence.
 * @return The sequence width in pixels.
 */
int AnimatedAvifReader::GetWidth() const {
    return width;
}

/**
 * @brief Retrieves the parsed height of the sequence.
 * @return The sequence height in pixels.
 */
int AnimatedAvifReader::GetHeight() const {
    return height;
}

/**
 * @brief Extracts, decodes, and interleaves the next sequence frame into the provided BGRA buffer.
 * @param out_bgra_buffer Pointer to the pre-allocated byte array of size (width * height * 4).
 * @return The duration of the decoded frame in milliseconds.
 */
int AnimatedAvifReader::DecodeNextFrame(uint8_t* out_bgra_buffer) {
    if (!out_bgra_buffer || !track) return 0;

    // Release the previous frame's memory if it exists
    if (current_image) {
        heif_image_release(current_image);
        current_image = nullptr;
    }

    // Decode the next interleaved RGB frame into memory
    heif_error err = heif_track_decode_next_image(track, &current_image, heif_colorspace_RGB, heif_chroma_interleaved_RGBA, nullptr);
    if (err.code != 0 || !current_image) {
        return 0; // EOF or decoding error
    }
    
    // Copy/encode the frame's pixels to the out buffer for C#
    BGRAEncoder encoder;
    encoder.Encode(current_image, width, height, out_bgra_buffer);

    // Calculate the frame's exact display duration using the track timescale
    uint32_t timescale = heif_track_get_timescale(track);
    uint32_t duration_ticks = heif_image_get_duration(current_image);
    int duration_ms = 100;
    
    if (timescale > 0) {
        duration_ms = static_cast<int>((static_cast<double>(duration_ticks) / timescale) * 1000.0);
    }
    current_frame_duration_ms = duration_ms > 0 ? duration_ms : 100;

    return current_frame_duration_ms;
}

/**
 * @brief Resets the animation track to the beginning to loop the playback continuously.
 */
void AnimatedAvifReader::Reset() {
    if (track) {
        heif_track_release(track);
        track = nullptr;
    }

    if (current_image) {
        heif_image_release(current_image);
        current_image = nullptr;
    }

    // Since libheif's decode cursor is tied to the context, we recreate the context
    // from memory. This is very fast and guarantees a clean start.
    if (cached_data && cached_size > 0) {
        context = std::shared_ptr<heif_context>(heif_context_alloc(), [](heif_context* c) { heif_context_free(c); });
        heif_error err = heif_context_read_from_memory_without_copy(context.get(), cached_data, cached_size, nullptr);
        if (err.code == 0) {
            track = heif_context_get_track(context.get(), track_id);
        }
    }
}
