#include "pch.h"
#include "NativeExports.h"

DdsError ExtractDdsImage(const wchar_t* dds_path, int max_dimension, PixelBuffer* out_buffer)
{
    if (!out_buffer) return DdsError::InvalidInput; // DecodeDds checks the path
    // On success this hands ownership of out_buffer->data to the caller.
    return DecodeDds(dds_path, max_dimension, *out_buffer);
}

void FreePixelBuffer(PixelBuffer* buffer)
{
    if (buffer && buffer->data)
    {
        delete[] buffer->data;
        buffer->data = nullptr;
    }
}
