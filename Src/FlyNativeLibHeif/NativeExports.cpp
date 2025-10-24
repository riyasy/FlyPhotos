#include "pch.h"
#include "NativeExports.h"
#include <atlstr.h>

/**
 * @brief Converts a std::wstring (UTF-16) to a std::string (ANSI/UTF-8).
 * This is a helper function used to pass file paths from the C#/.NET world
 * to the C++ libraries that expect narrow character strings.
 * @param wstr The wide string to convert.
 * @return The converted narrow string.
 */
static std::string WStringToString(const std::wstring& wstr)
{
    // CW2A is a handy ATL/MFC conversion class that handles the string conversion.
    return std::string(CW2A(wstr.c_str()));
}

/**
 * @brief C-API function to extract the primary image into a raw BGRA buffer.
 * This function serves as the boundary between managed C# and native C++,
 * wrapping the object-oriented C++ HeifReader class in a simple C-style function.
 */
HeifError ExtractPrimaryImageBGRA(const wchar_t* heic_path, PixelBuffer* out_buffer) {
    // Basic input validation to prevent null pointer dereferences.
    if (!heic_path || !out_buffer) { return HeifError::InvalidInput; }
    // Zero out the output buffer to ensure it's in a known-good state, especially on failure.
    memset(out_buffer, 0, sizeof(PixelBuffer));

    // Instantiate the C++ reader class which contains the core logic.
    HeifReader reader;
    // Create a temporary buffer on the stack to be filled by the reader.
    PixelBuffer cppBuffer; // Now contains a raw pointer
    // Convert the incoming UTF-16 path to a string format the reader can use.
    const std::string input_file = WStringToString(heic_path);

    // Call the main C++ implementation. This allocates memory for pixel data inside cppBuffer.
    HeifError result = reader.ExtractPrimaryImageBGRA(input_file, cppBuffer);

    // If successful, just copy the pointer and dimensions directly. NO memcpy!
    // This step effectively transfers ownership of the allocated memory (cppBuffer.data)
    // from the C++ side to the C# caller.
    if (result == HeifError::Ok) {
        *out_buffer = *reinterpret_cast<PixelBuffer*>(&cppBuffer);
    }

    // Return the result code to the caller.
    return result;
}

/**
 * @brief C-API function to extract the thumbnail image into a raw BGRA buffer.
 * This is the C-style wrapper for the thumbnail extraction functionality.
 */
HeifError ExtractThumbnailBGRA(const wchar_t* heic_path, PixelBuffer* out_buffer) {
    // Basic input validation.
    if (!heic_path || !out_buffer) { return HeifError::InvalidInput; }
    // Ensure the output buffer is clean before proceeding.
    memset(out_buffer, 0, sizeof(PixelBuffer));

    // Instantiate the core C++ reader class.
    HeifReader reader;
    PixelBuffer cppBuffer; // Now contains a raw pointer
    // Convert the file path for use by the C++ class.
    const std::string input_file = WStringToString(heic_path);

    // Call the C++ method to extract the thumbnail, which allocates memory inside cppBuffer.
    HeifError result = reader.ExtractThumbnailBGRA(input_file, cppBuffer);

    // If successful, just copy the pointer and dimensions directly. NO memcpy!
    // This transfers ownership of the heap-allocated pixel buffer to the C# caller.
    if (result == HeifError::Ok) {
        // Since the memory layout of PixelBuffer and PixelBuffer are now identical,
        // we can safely copy the struct's contents (pointer and metadata).
        *out_buffer = *reinterpret_cast<PixelBuffer*>(&cppBuffer);
    }

    return result;
}

/**
 * @brief C-API function to free the memory allocated by the extraction functions.
 * This function MUST be called from the managed (C#) side to release the unmanaged
 * memory buffer pointed to by `PixelBuffer.data`. Failure to do so will result
 * in a memory leak.
 */
void FreePixelBuffer(PixelBuffer* buffer) {
    // Check for a valid buffer and an allocated data pointer to prevent crashes.
    if (buffer && buffer->data) {
        // Free the memory that was allocated inside C++ with `new[]`.
        delete[] buffer->data;
        // Null out the pointer to prevent a double-free if this function is accidentally called again.
        buffer->data = nullptr;
    }
}