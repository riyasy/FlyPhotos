#include "pch.h"
#include "DdsReader.h"

#include <algorithm>
#include <climits>
#include <cstring>
#include <new>
#include "DirectXTex.h"

using namespace DirectX;

namespace
{
    // Never let DirectXTex route a conversion or resize through WIC: the WIC paths need COM initialised
    // on the calling thread, which the .NET thread pool doesn't promise, and the non-WIC code covers
    // every format here.
    constexpr TEX_FILTER_FLAGS ConvertFlags = TEX_FILTER_DEFAULT | TEX_FILTER_FORCE_NON_WIC;
    constexpr TEX_FILTER_FLAGS ResizeFlags = TEX_FILTER_TRIANGLE | TEX_FILTER_FORCE_NON_WIC;

    /// The smallest stored mip level whose longer side is still >= maxDimension; 0 (the top) when
    /// maxDimension is 0 or the texture has no mips.
    size_t PickMip(const TexMetadata& meta, int maxDimension)
    {
        if (maxDimension <= 0) return 0;
        size_t mip = 0;
        for (size_t i = 1; i < meta.mipLevels; ++i)
        {
            const size_t w = std::max<size_t>(1, meta.width >> i);
            const size_t h = std::max<size_t>(1, meta.height >> i);
            if (std::max(w, h) < static_cast<size_t>(maxDimension)) break;
            mip = i;
        }
        return mip;
    }

    /// Copies BGRA8 rows into a packed buffer, premultiplying alpha on the way when asked. Integer math on
    /// the stored bytes (gamma space), as the ImageMagick path did; DirectXTex's PremultiplyAlpha would
    /// cost another full-size image and a float pass for the same result.
    void CopyRows(const Image& src, uint8_t* dst, bool premultiply)
    {
        const size_t rowBytes = src.width * 4;
        for (size_t y = 0; y < src.height; ++y)
        {
            const uint8_t* in = src.pixels + y * src.rowPitch;
            uint8_t* out = dst + y * rowBytes;
            memcpy(out, in, rowBytes);
            if (!premultiply) continue;
            for (size_t x = 0; x < rowBytes; x += 4)
            {
                const unsigned a = out[x + 3];
                if (a == 255) continue;
                out[x + 0] = static_cast<uint8_t>((out[x + 0] * a + 127) / 255);
                out[x + 1] = static_cast<uint8_t>((out[x + 1] * a + 127) / 255);
                out[x + 2] = static_cast<uint8_t>((out[x + 2] * a + 127) / 255);
            }
        }
    }

    DdsError Decode(const wchar_t* path, int maxDimension, PixelBuffer& out)
    {
        TexMetadata meta;
        ScratchImage dds;
        // EXPAND_LUMINANCE: legacy L8 / L16 / A8L8 become grey RGBA instead of one- or two-channel R/RG data
        // (A8L8 would show red-green, and L8 doesn't load at all without it).
        if (FAILED(LoadFromDDSFile(path, DDS_FLAGS_EXPAND_LUMINANCE, &meta, dds))) return DdsError::FileReadError;

        // Face 0 of a cubemap, item 0 of an array, slice 0 of a volume.
        const Image* src = dds.GetImage(PickMip(meta, maxDimension), 0, 0);
        if (!src) return DdsError::DecodeError;

        // Keep sRGB data as sRGB. Converting to plain UNORM would linearise it and the image would show
        // too dark; the _SRGB target makes it a byte reorder. Win2D gets the bytes as stored either way,
        // as it does for every other format.
        const DXGI_FORMAT target = IsSRGB(meta.format) ? DXGI_FORMAT_B8G8R8A8_UNORM_SRGB : DXGI_FORMAT_B8G8R8A8_UNORM;

        ScratchImage stage; // at most one intermediate is alive at a time
        auto replace = [&](ScratchImage& next) { stage = std::move(next); src = stage.GetImage(0, 0, 0); };

        // Block-compressed: decompress straight into the target, no separate Convert pass.
        if (IsCompressed(src->format))
        {
            ScratchImage next;
            if (FAILED(Decompress(*src, target, next))) return DdsError::DecodeError;
            replace(next);
        }

        // Still bigger than asked for: no mip level was small enough (or there are no mips). Resized before
        // converting, so an uncompressed source converts the small image rather than the full one.
        if (maxDimension > 0 && std::max(src->width, src->height) > static_cast<size_t>(maxDimension))
        {
            const double scale = static_cast<double>(maxDimension) / static_cast<double>(std::max(src->width, src->height));
            const size_t w = std::max<size_t>(1, static_cast<size_t>(src->width * scale));
            const size_t h = std::max<size_t>(1, static_cast<size_t>(src->height * scale));
            // Resize sRGB bytes as if linear: a gamma-space downscale, as the WIC and ImageMagick previews do.
            // Left as _SRGB, DirectXTex linearises every pixel through floats - slower than the full decode.
            Image view = *src;
            view.format = MakeLinear(view.format);
            ScratchImage next;
            if (FAILED(Resize(view, w, h, ResizeFlags, next))) return DdsError::DecodeError;
            replace(next);
        }

        // A resize above relabels sRGB data as UNORM. Converting that to the _SRGB target would gamma-encode
        // already-encoded bytes (too bright), so convert to the target's UNORM form instead: a pure reorder.
        // The output tag doesn't matter, only the bytes.
        const DXGI_FORMAT convertTo = IsSRGB(src->format) ? target : MakeLinear(target);
        if (src->format != convertTo)
        {
            ScratchImage next;
            if (FAILED(Convert(*src, convertTo, ConvertFlags, TEX_THRESHOLD_DEFAULT, next))) return DdsError::DecodeError;
            replace(next);
        }

        const size_t size = src->width * 4 * src->height;
        if (size > INT_MAX) return DdsError::OutOfMemory;
        out.data = new (std::nothrow) uint8_t[size];
        if (!out.data) return DdsError::OutOfMemory;

        // Win2D wants premultiplied alpha.
        const TEX_ALPHA_MODE alphaMode = meta.GetAlphaMode();
        CopyRows(*src, out.data,
            HasAlpha(meta.format) && alphaMode != TEX_ALPHA_MODE_OPAQUE && alphaMode != TEX_ALPHA_MODE_PREMULTIPLIED);

        out.dataSize = static_cast<int>(size);
        out.width = static_cast<int>(src->width);
        out.height = static_cast<int>(src->height);
        out.primaryImageWidth = static_cast<int>(meta.width);
        out.primaryImageHeight = static_cast<int>(meta.height);
        return DdsError::Ok;
    }
}

DdsError DecodeDds(const wchar_t* path, int maxDimension, PixelBuffer& out)
{
    memset(&out, 0, sizeof(out));
    if (!path) return DdsError::InvalidInput;
    try
    {
        return Decode(path, maxDimension, out);
    }
    catch (const std::bad_alloc&)
    {
        return DdsError::OutOfMemory;
    }
    catch (...)
    {
        return DdsError::DecodeError;
    }
}
