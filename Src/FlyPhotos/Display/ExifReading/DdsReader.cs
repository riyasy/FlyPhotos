#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Localization;
using NLog;

namespace FlyPhotos.Display.ExifReading;

// MetadataExtractor has no DDS parser at all, so ImageMetadataReader throws on a .dds and the
// info panel comes up empty - not even file name or size. Everything a DDS exposes lives in a
// fixed-layout header at the very start of the file: 4-byte magic + 124-byte DDS_HEADER, plus a
// 20-byte DDS_HEADER_DXT10 when the pixel format's FourCC is "DX10". Reading it is 148 bytes at
// known offsets, so we do it here instead of taking a dependency. DirectXTex would only be needed
// to *decode* the compressed texture payload, which is a different job.
internal static class DdsReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const uint MagicDds = 0x20534444;   // "DDS " little-endian
    private const uint FourCcDx10 = 0x30315844; // "DX10" little-endian

    // DDS_HEADER.dwFlags
    private const uint DdsdMipMapCount = 0x20000;

    // DDS_PIXELFORMAT.dwFlags
    private const uint DdpfFourCc = 0x4;

    // DDS_HEADER.dwCaps2
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2Volume = 0x200000;

    // DDS_HEADER_DXT10.miscFlag
    private const uint MiscTextureCube = 0x4;

    // Byte offsets from the start of the file.
    private const int OffHeaderSize = 4;
    private const int OffHeaderFlags = 8;
    private const int OffHeight = 12;
    private const int OffWidth = 16;
    private const int OffPitchOrLinearSize = 20;
    private const int OffDepth = 24;
    private const int OffMipMapCount = 28;
    private const int OffPixelFormatSize = 76;
    private const int OffPixelFormatFlags = 80;
    private const int OffFourCc = 84;
    private const int OffRgbBitCount = 88;
    private const int OffRBitMask = 92;
    private const int OffGBitMask = 96;
    private const int OffBBitMask = 100;
    private const int OffABitMask = 104;
    private const int OffCaps = 108;
    private const int OffCaps2 = 112;
    private const int OffDxgiFormat = 128;
    private const int OffResourceDimension = 132;
    private const int OffMiscFlag = 136;
    private const int OffArraySize = 140;
    private const int OffMiscFlags2 = 144;

    private const int HeaderSize = 128;
    private const int Dx10HeaderSize = 20;

    public static ExifData Read(string filePath)
    {
        // File name and size need no header, so build them first - a truncated or mislabelled
        // .dds then still shows something useful instead of the empty panel it shows today.
        var fileInfo = new FileInfo(filePath);
        var summary = new List<ExifField>
        {
            new(L.Get("Exif_FileName"), fileInfo.Name),
            new(L.Get("Exif_FileSize"), ExifReader.FormatFileSize(fileInfo.Length))
        };
        var raw = new List<ExifField>();

        try
        {
            AppendHeaderFields(filePath, summary, raw);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read DDS header for {0}", filePath);
        }

        var all = new List<ExifFieldGroup> { new("DDS Header", [.. summary, .. raw]) };
        return new ExifData(summary, new Lazy<IReadOnlyList<ExifFieldGroup>>(() => all));
    }

    private static void AppendHeaderFields(string filePath, List<ExifField> summary, List<ExifField> raw)
    {
        Span<byte> buf = stackalloc byte[HeaderSize + Dx10HeaderSize];
        // FileShare.ReadWrite, not File.OpenRead's FileShare.Read: texture authoring tools hold
        // write handles on files they are exporting, and browsing into one mid-save would
        // otherwise fail with a sharing violation instead of showing the header.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.ReadExactly(buf[..HeaderSize]);

        // Both struct sizes are fixed by the spec, so they double as an alignment check: a file
        // that passes the magic but disagrees here is malformed, and every offset below would
        // otherwise yield plausible-looking nonsense.
        if (U32(buf, 0) != MagicDds)
            throw new InvalidDataException($"Not a DDS file (magic 0x{U32(buf, 0):X8}).");
        if (U32(buf, OffHeaderSize) != 124 || U32(buf, OffPixelFormatSize) != 32)
            throw new InvalidDataException(
                $"Malformed DDS header (dwSize={U32(buf, OffHeaderSize)}, ddspf.dwSize={U32(buf, OffPixelFormatSize)}).");

        var pixelFormatFlags = U32(buf, OffPixelFormatFlags);
        var fourCc = U32(buf, OffFourCc);
        var hasDx10 = (pixelFormatFlags & DdpfFourCc) != 0 && fourCc == FourCcDx10;
        if (hasDx10) stream.ReadExactly(buf[HeaderSize..]);

        summary.Add(new ExifField(L.Get("Exif_Dimensions"), $"{U32(buf, OffWidth)} × {U32(buf, OffHeight)}"));
        summary.Add(new ExifField(L.Get("Exif_DdsFormat"), DescribeFormat(buf, hasDx10, pixelFormatFlags)));
        summary.Add(new ExifField(L.Get("Exif_DdsTextureType"), DescribeTextureType(buf, hasDx10)));
        summary.Add(new ExifField(L.Get("Exif_DdsMipmaps"), MipMapCount(buf).ToString()));
        summary.Add(new ExifField(L.Get("Exif_DdsAlpha"), DescribeAlpha(buf, hasDx10)));

        // Spec field names, untranslated - same convention as the panel's "Show All" view, which
        // renders MetadataExtractor's raw English tag names rather than localized labels.
        raw.Add(new ExifField("dwFlags", $"0x{U32(buf, OffHeaderFlags):X8}"));
        raw.Add(new ExifField("dwPitchOrLinearSize", U32(buf, OffPitchOrLinearSize).ToString()));
        raw.Add(new ExifField("dwDepth", U32(buf, OffDepth).ToString()));
        raw.Add(new ExifField("dwCaps", $"0x{U32(buf, OffCaps):X8}"));
        raw.Add(new ExifField("dwCaps2", $"0x{U32(buf, OffCaps2):X8}"));
        raw.Add(new ExifField("ddspf.dwFlags", $"0x{pixelFormatFlags:X8}"));
        raw.Add(new ExifField("ddspf.dwFourCC", FourCcText(buf, fourCc)));
        raw.Add(new ExifField("ddspf.dwRGBBitCount", U32(buf, OffRgbBitCount).ToString()));

        if (!hasDx10) return;
        raw.Add(new ExifField("dxgiFormat", U32(buf, OffDxgiFormat).ToString()));
        raw.Add(new ExifField("resourceDimension", U32(buf, OffResourceDimension).ToString()));
        raw.Add(new ExifField("miscFlag", $"0x{U32(buf, OffMiscFlag):X8}"));
        raw.Add(new ExifField("arraySize", U32(buf, OffArraySize).ToString()));
        raw.Add(new ExifField("miscFlags2", $"0x{U32(buf, OffMiscFlags2):X8}"));
    }

    // dwMipMapCount is only meaningful when DDSD_MIPMAPCOUNT is set; writers leave it at 0
    // otherwise, which still means one (the top) level.
    private static uint MipMapCount(ReadOnlySpan<byte> buf)
    {
        if ((U32(buf, OffHeaderFlags) & DdsdMipMapCount) == 0) return 1;
        var count = U32(buf, OffMipMapCount);
        return count == 0 ? 1 : count;
    }

    // Reported in texdiag's vocabulary so the panel cross-references the .txt dumps that
    // `texdiag info` already produces. DirectXTex resolves a legacy file's FourCC or channel
    // masks to a DXGI format on load and prints that, so "DXT5" reads as BC3_UNORM and "ATI2"
    // as BC5_UNORM - we resolve the same way rather than echoing the raw FourCC.
    private static string DescribeFormat(ReadOnlySpan<byte> buf, bool hasDx10, uint pixelFormatFlags)
    {
        if (hasDx10) return DxgiFormatName(U32(buf, OffDxgiFormat));

        if ((pixelFormatFlags & DdpfFourCc) != 0)
        {
            var fourCc = U32(buf, OffFourCc);
            var dxgi = LegacyFourCcToDxgi(fourCc);
            // texdiag prints *UNKNOWN* for a FourCC DirectXTex cannot map; the raw tag is more
            // use than that in a viewer, and it is what the file literally says.
            return dxgi != 0 ? DxgiFormatName(dxgi) : FourCcText(buf, fourCc);
        }

        var bits = U32(buf, OffRgbBitCount);
        var masked = LegacyMaskToDxgi(bits, U32(buf, OffRBitMask), U32(buf, OffGBitMask),
            U32(buf, OffBBitMask), U32(buf, OffABitMask));
        if (masked != 0) return DxgiFormatName(masked);

        // ponytail: the mask table covers the layouts DirectXTex maps; anything else falls back to
        // bit depth rather than texdiag's *UNKNOWN*. Extend the table if a real file lands here.
        return bits > 0 ? $"Uncompressed ({bits}-bit)" : "Uncompressed";
    }

    // D3D9-era FourCC and D3DFMT codes, mapped exactly as DirectXTex's GetDXGIFormat does.
    private static uint LegacyFourCcToDxgi(uint fourCc) => fourCc switch
    {
        0x31545844 => 71,  // "DXT1" -> BC1_UNORM
        0x32545844 => 74,  // "DXT2" -> BC2_UNORM (premultiplied alpha)
        0x33545844 => 74,  // "DXT3" -> BC2_UNORM
        0x34545844 => 77,  // "DXT4" -> BC3_UNORM (premultiplied alpha)
        0x35545844 => 77,  // "DXT5" -> BC3_UNORM
        0x31495441 => 80,  // "ATI1" -> BC4_UNORM
        0x55344342 => 80,  // "BC4U" -> BC4_UNORM
        0x53344342 => 81,  // "BC4S" -> BC4_SNORM
        0x32495441 => 83,  // "ATI2" -> BC5_UNORM
        0x55354342 => 83,  // "BC5U" -> BC5_UNORM
        0x53354342 => 84,  // "BC5S" -> BC5_SNORM
        0x47424752 => 68,  // "RGBG" -> R8G8_B8G8_UNORM
        0x42475247 => 69,  // "GRGB" -> G8R8_G8B8_UNORM
        36 => 11,          // D3DFMT_A16B16G16R16   -> R16G16B16A16_UNORM
        110 => 13,         // D3DFMT_Q16W16V16U16   -> R16G16B16A16_SNORM
        111 => 54,         // D3DFMT_R16F           -> R16_FLOAT
        112 => 34,         // D3DFMT_G16R16F        -> R16G16_FLOAT
        113 => 10,         // D3DFMT_A16B16G16R16F  -> R16G16B16A16_FLOAT
        114 => 41,         // D3DFMT_R32F           -> R32_FLOAT
        115 => 16,         // D3DFMT_G32R32F        -> R32G32_FLOAT
        116 => 2,          // D3DFMT_A32B32G32R32F  -> R32G32B32A32_FLOAT
        _ => 0             // DXGI_FORMAT_UNKNOWN
    };

    // Uncompressed legacy layouts, keyed on bit count + the four channel masks, again following
    // DirectXTex. Note the channel order flips: D3D9 named masks high-to-low, DXGI names them
    // low-to-high, so A8R8G8B8 is DXGI's B8G8R8A8_UNORM.
    private static uint LegacyMaskToDxgi(uint bits, uint r, uint g, uint b, uint a) => (bits, r, g, b, a) switch
    {
        (32, 0x00ff0000, 0x0000ff00, 0x000000ff, 0xff000000) => 87,  // A8R8G8B8 -> B8G8R8A8_UNORM
        (32, 0x00ff0000, 0x0000ff00, 0x000000ff, 0) => 88,           // X8R8G8B8 -> B8G8R8X8_UNORM
        (32, 0x000000ff, 0x0000ff00, 0x00ff0000, 0xff000000) => 28,  // A8B8G8R8 -> R8G8B8A8_UNORM
        (32, 0x000000ff, 0x0000ff00, 0x00ff0000, 0) => 28,           // X8B8G8R8 -> R8G8B8A8_UNORM
        (32, 0x0000ffff, 0xffff0000, 0, 0) => 35,                    // G16R16   -> R16G16_UNORM
        (32, 0x000003ff, 0x000ffc00, 0x3ff00000, 0xc0000000) => 24,  // A2B10G10R10 -> R10G10B10A2_UNORM
        (16, 0xf800, 0x07e0, 0x001f, 0) => 85,                       // R5G6B5   -> B5G6R5_UNORM
        (16, 0x7c00, 0x03e0, 0x001f, 0x8000) => 86,                  // A1R5G5B5 -> B5G5R5A1_UNORM
        (16, 0x0f00, 0x00f0, 0x000f, 0xf000) => 115,                 // A4R4G4B4 -> B4G4R4A4_UNORM
        (16, 0x00ff, 0, 0, 0xff00) => 49,                            // A8L8     -> R8G8_UNORM
        (16, 0xffff, 0, 0, 0) => 56,                                 // L16      -> R16_UNORM
        (8, 0xff, 0, 0, 0) => 61,                                    // L8       -> R8_UNORM
        (8, 0, 0, 0, 0xff) => 65,                                    // A8       -> A8_UNORM
        _ => 0                                                       // DXGI_FORMAT_UNKNOWN
    };

    // Most FourCCs are four printable characters ("DXT5", "ATI2"); a handful of D3DFMT values are
    // instead written as small integers (e.g. 113 = A16B16G16R16F).
    private static string FourCcText(ReadOnlySpan<byte> buf, uint fourCc)
    {
        var tag = buf.Slice(OffFourCc, 4);
        foreach (var c in tag)
            if (c is < 0x20 or > 0x7E) return $"D3DFMT {fourCc}";
        return Encoding.ASCII.GetString(tag).TrimEnd();
    }

    // texdiag's `dimension` line: 1D, 2D, 3D, or Cube. It prints arraySize separately, but the
    // panel has no such row, so an array count is appended here instead of dropped.
    private static string DescribeTextureType(ReadOnlySpan<byte> buf, bool hasDx10)
    {
        if (hasDx10)
        {
            var isCube = (U32(buf, OffMiscFlag) & MiscTextureCube) != 0;
            var kind = isCube
                ? "Cube"
                : U32(buf, OffResourceDimension) switch
                {
                    2 => "1D",
                    4 => "3D",
                    _ => "2D"
                };
            // The header field counts cubes, not faces: a single cubemap stores arraySize 1.
            // (DirectXTex's in-memory TexMetadata.arraySize is 6x this, because DecodeDDSHeader
            // multiplies by the face count - that is the converted value, not what the file says.)
            var arraySize = U32(buf, OffArraySize);
            return arraySize > 1 ? $"{kind} array ({arraySize})" : kind;
        }

        var caps2 = U32(buf, OffCaps2);
        if ((caps2 & Caps2Cubemap) != 0)
        {
            // The six face bits sit directly above DDSCAPS2_CUBEMAP; partial cubemaps are legal,
            // so report the face count when it is not the usual full six.
            var faces = 0;
            for (var bit = 0x400u; bit <= 0x8000u; bit <<= 1)
                if ((caps2 & bit) != 0) faces++;
            return faces == 6 ? "Cube" : $"Cube ({faces} faces)";
        }
        return (caps2 & Caps2Volume) != 0 ? "3D" : "2D";
    }

    private static string DescribeAlpha(ReadOnlySpan<byte> buf, bool hasDx10)
    {
        if (!hasDx10)
        {
            // A legacy header has nowhere to record how alpha is encoded, so DirectXTex reports
            // Unknown for everything except DXT2/DXT4, the two FourCCs that mean premultiplied.
            // DDPF_ALPHAPIXELS is not a substitute: a plain "DXT5" header leaves it clear even
            // though BC3 always carries alpha, so trusting it would report "None" on an alpha
            // texture. Deciding it properly needs a per-format alpha table - not worth it when
            // texdiag, the tool being cross-referenced, says Unknown here too.
            var fourCc = U32(buf, OffFourCc);
            return fourCc is 0x32545844 or 0x34545844 ? "Premultiplied" : "Unknown";
        }

        // DDS_HEADER_DXT10.miscFlags2 stores DDS_ALPHA_MODE in its low three bits.
        return (U32(buf, OffMiscFlags2) & 0x7) switch
        {
            1 => "Straight",
            2 => "Premultiplied",
            3 => "Opaque",
            4 => "Custom",
            _ => "Unknown"
        };
    }

    // Names match DirectXTex's DEFFMT table verbatim - i.e. texdiag's spelling, without the
    // DXGI_FORMAT_ prefix. The full enum runs to ~130 entries, almost all of which never appear in
    // a texture file; anything unlisted reports its numeric value rather than texdiag's *UNKNOWN*.
    private static string DxgiFormatName(uint format) => format switch
    {
        2 => "R32G32B32A32_FLOAT",
        10 => "R16G16B16A16_FLOAT",
        11 => "R16G16B16A16_UNORM",
        13 => "R16G16B16A16_SNORM",
        16 => "R32G32_FLOAT",
        24 => "R10G10B10A2_UNORM",
        28 => "R8G8B8A8_UNORM",
        29 => "R8G8B8A8_UNORM_SRGB",
        34 => "R16G16_FLOAT",
        35 => "R16G16_UNORM",
        41 => "R32_FLOAT",
        49 => "R8G8_UNORM",
        54 => "R16_FLOAT",
        56 => "R16_UNORM",
        61 => "R8_UNORM",
        65 => "A8_UNORM",
        68 => "R8G8_B8G8_UNORM",
        69 => "G8R8_G8B8_UNORM",
        71 => "BC1_UNORM",
        72 => "BC1_UNORM_SRGB",
        74 => "BC2_UNORM",
        75 => "BC2_UNORM_SRGB",
        77 => "BC3_UNORM",
        78 => "BC3_UNORM_SRGB",
        80 => "BC4_UNORM",
        81 => "BC4_SNORM",
        83 => "BC5_UNORM",
        84 => "BC5_SNORM",
        85 => "B5G6R5_UNORM",
        86 => "B5G5R5A1_UNORM",
        87 => "B8G8R8A8_UNORM",
        88 => "B8G8R8X8_UNORM",
        91 => "B8G8R8A8_UNORM_SRGB",
        95 => "BC6H_UF16",
        96 => "BC6H_SF16",
        98 => "BC7_UNORM",
        99 => "BC7_UNORM_SRGB",
        115 => "B4G4R4A4_UNORM",
        _ => $"DXGI_FORMAT {format}"
    };

    private static uint U32(ReadOnlySpan<byte> buf, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buf[offset..]);
}
