#include "pch.h"
#include "AnimatedAvifReader.h"
#include "PixelBufferEncoder.h"
#include "DllGlobals.h"
#include <algorithm>

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

namespace {
    // A minimal ISOBMFF box reader over the file bytes. An empty Box means "not found".
    struct Box { uint32_t type; const uint8_t* body; size_t size; };

    uint32_t Be32(const uint8_t* p) { return (uint32_t(p[0]) << 24) | (p[1] << 16) | (p[2] << 8) | p[3]; }
    uint64_t Be64(const uint8_t* p) { return (uint64_t(Be32(p)) << 32) | Be32(p + 4); }
    constexpr uint32_t Fourcc(const char (&s)[5]) { return (uint32_t(s[0]) << 24) | (s[1] << 16) | (s[2] << 8) | s[3]; }

    // Reads the box at p and moves p past it; false at end or on a malformed size.
    bool NextBox(const uint8_t*& p, const uint8_t* end, Box& box) {
        size_t n = size_t(end - p);
        if (n < 8) return false;
        uint64_t size = Be32(p);
        size_t header = 8;
        if (size == 1) {
            if (n < 16) return false;
            size = Be64(p + 8);
            header = 16;
        } else if (size == 0) {
            size = n;
        }
        if (size < header || size > n) return false;
        box = { Be32(p + 4), p + header, size_t(size) - header };
        p += size;
        return true;
    }

    Box FindBox(Box parent, uint32_t type) {
        const uint8_t* p = parent.body;
        Box box;
        while (NextBox(p, parent.body + parent.size, box))
            if (box.type == type) return box;
        return {};
    }

    // moov/trak[track_id]/mdia/mdhd: the media duration, one pass through the samples, in ms. The edit list
    // (which repeats a looping file) lives outside it. Timescale and duration both come from this box. 0 if unknown.
    double MediaDurationMs(const uint8_t* data, size_t size, uint32_t track_id) {
        Box moov = FindBox({ 0, data, size }, Fourcc("moov"));
        const uint8_t* p = moov.body;
        Box trak;
        while (NextBox(p, moov.body + moov.size, trak)) {
            if (trak.type != Fourcc("trak")) continue;
            Box tkhd = FindBox(trak, Fourcc("tkhd"));
            if (tkhd.size < 24 || Be32(tkhd.body + (tkhd.body[0] == 1 ? 20 : 12)) != track_id) continue;
            Box mdhd = FindBox(FindBox(trak, Fourcc("mdia")), Fourcc("mdhd"));
            // Version 0: 32-bit times; timescale at 12, duration at 16. Version 1: 64-bit; at 20 and 24.
            bool v1 = mdhd.size >= 32 && mdhd.body[0] == 1;
            if (!v1 && (mdhd.size < 20 || mdhd.body[0] != 0)) return 0;
            uint32_t timescale = Be32(mdhd.body + (v1 ? 20 : 12));
            uint64_t ticks = v1 ? Be64(mdhd.body + 24) : Be32(mdhd.body + 16);
            if (timescale == 0 || ticks == (v1 ? UINT64_MAX : UINT32_MAX)) return 0; // all 1s: unknown
            return double(ticks) * 1000 / timescale;
        }
        return 0;
    }
}

/**
 * @brief The length of one loop, from the track's media header: no decode cursor is touched.
 * @return One loop in ms, or 0 if unknown.
 */
int AnimatedAvifReader::GetLoopDurationMs() const {
    if (!track) return 0;
    return static_cast<int>(std::min(MediaDurationMs(cached_data, cached_size, track_id), double(INT32_MAX)));
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
 * @brief Extracts, decodes, and interleaves the next sequence frame into the provided RGBA buffer.
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
    PixelBufferEncoder::Encode(current_image, width, height, out_bgra_buffer);

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
