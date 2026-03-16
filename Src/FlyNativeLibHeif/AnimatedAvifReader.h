#ifndef ANIMATED_AVIF_READER_H
#define ANIMATED_AVIF_READER_H

#include <string>
#include <memory>
#include <vector>
#include <libheif/heif.h>
#include <libheif/heif_sequences.h>

/**
 * @brief A reader class that handles decoding and state management for Animated AVIF and HEIF sequences.
 * 
 * This class uses libheif to read an AVIF/HEIC sequence directly from a memory buffer.
 * It is stateful, keeping track of the current `heif_context`, `heif_track`, and `heif_image`
 * to decode frames sequentially without needing to re-parse the container on every frame.
 */
class AnimatedAvifReader {
public:
    AnimatedAvifReader();
    ~AnimatedAvifReader();

    /**
     * @brief Opens an animated sequence from a raw memory buffer.
     * @param data Pointer to the in-memory AVIF file data.
     * @param size Size of the data buffer in bytes.
     * @return true if the file was successfully parsed and an image or track was found, false otherwise.
     */
    bool Open(const uint8_t* data, size_t size);

    /**
     * @brief Quickly checks if the opened file contains an animated image sequence.
     * @return true if the file contains one or more sequence tracks, false otherwise.
     */
    bool IsAnimated() const;

    /**
     * @brief Gets the width of the animated sequence.
     * @return Width in pixels.
     */
    int GetWidth() const;

    /**
     * @brief Gets the height of the animated sequence.
     * @return Height in pixels.
     */
    int GetHeight() const;

    /**
     * @brief Decodes the next sequence frame from the track into the provided memory buffer.
     * @param out_bgra_buffer Pointer to a pre-allocated byte array of size (Width * Height * 4).
     * @return The duration of the decoded frame in milliseconds. Returns 0 if an error occurs or the end of the sequence is reached.
     */
    int DecodeNextFrame(uint8_t* out_bgra_buffer);

    /**
     * @brief Resets the animation track to the beginning, allowing the sequence to loop.
     */
    void Reset();

private:
    /// @brief Shared pointer to the libheif context managing the file data.
    std::shared_ptr<heif_context> context;

    /// @brief Pointer to the active sequence track being decoded.
    heif_track* track = nullptr;

    /// @brief The ID of the original sequence track, used to reset the animation loop.
    uint32_t track_id = 0;

    /// @brief Pointer to the most recently decoded frame image.
    heif_image* current_image = nullptr;

    /// @brief The width of the bounded animation sequence canvas.
    int width = 0;

    /// @brief The height of the bounded animation sequence canvas.
    int height = 0;

    /// @brief The duration of the currently extracted frame in milliseconds.
    int current_frame_duration_ms = 0;

    /// @brief Cached pointer to the raw file bytes, used for re-creating the context on Reset.
    const uint8_t* cached_data = nullptr;

    /// @brief Cached size of the raw file bytes.
    size_t cached_size = 0;
};

#endif // ANIMATED_AVIF_READER_H
