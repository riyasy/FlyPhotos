#nullable enable
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FlyPhotos.Services;

internal static class CodecDiscovery
{
    private static readonly List<CodecInfo> _codecInfoList;
    private static readonly HashSet<string> _wicExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _wicRawExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _wicNonRawExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _flyExtensions = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _imageMagickExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _imageMagickRawExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _imageMagickNonRawExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _rawlerRawExtensions = new(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> SupportedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);


    private static readonly string[] ProbableImageMagickStandardExtensions =
    [
        // Standard
        ".bmp",".dib",".rle",".gif",".ico",".icon",".cur",
        ".jpeg",".jpe",".jpg",".jfif",".exif",
        ".png",".tiff",".tif",
        // Traditional formats (added)
        ".tga",".pcx",".ras",".sun",".sgi",".rgb",".rgba",
        ".pict",".pct",".pix",
        // HDR / professional formats (added)
        ".exr",".hdr",".dpx",".cin",".pfm",
        // Scientific / portable formats (added)
        ".pbm",".pgm",".ppm",".pnm",".pam",".fits",
        // Modern
        ".wdp",".jxr",".dds",
        ".heic",".heif",".hif",
        ".avci",".heics",".heifs",".avcs",
        ".avif",".avifs",
        ".webp",".jxl",
        // New lightweight formats (added)
        ".qoi",".ff",
        // Fly-specific
        ".psd",".svg"
    ];

    private static readonly string[] ProbableImageMagickRawExtensions =
    [
        ".3fr",".ari",".arw",".bay",".cap",
        ".cr2",".cr3",".crw",
        ".dcs",".dcr",".drf",
        ".eip",".erf",".fff",
        ".iiq",".k25",".kdc",
        ".mef",".mos",".mrw",
        ".nef",".nrw",
        ".orf",".ori",
        ".pef",".ptx",".pxn",
        ".raf",".raw",
        ".rw2",".rwl",
        ".sr2",".srf",".srw",
        ".x3f",".dng"
    ];



    // If no WIC codec (or any Fly custom codec) is found for a file extension,
    // we can check this list to see if ImageMagick supports it.
    // This is not an exhaustive list of ImageMagick supported formats.
    private static readonly string[] ProbableImageMagickExtensions =
        [.. ProbableImageMagickStandardExtensions, .. ProbableImageMagickRawExtensions];

    static CodecDiscovery()
    {
        var wicCodecInfos = GetWicCodecs();
        var flyCodecInfos = GetFlyCodecs();
        var imageMagickCodecInfo = GetImageMagickCodecs_StaticSnapShot(ProbableImageMagickExtensions);

        foreach (var codecInfo in wicCodecInfos)
        {
            _wicExtensions.UnionWith(codecInfo.FileExtensions);
            if (codecInfo.FriendlyName.Contains("RAW", StringComparison.OrdinalIgnoreCase))
                _wicRawExtensions.UnionWith(codecInfo.FileExtensions);
        }
        foreach (var ext in _wicExtensions)
            if (!_wicRawExtensions.Contains(ext))
                _wicNonRawExtensions.Add(ext);

        foreach (var codecInfo in flyCodecInfos)
            _flyExtensions.UnionWith(codecInfo.FileExtensions);
        _imageMagickExtensions.UnionWith(imageMagickCodecInfo.FileExtensions);

        // Remove any ImageMagick extensions that are already supported
        // by WIC or Fly codecs to avoid duplication in the GUI.
        // _codecInfoList will be used in GUI to display list of Codecs.
        imageMagickCodecInfo.FileExtensions.RemoveAll(item => _wicExtensions.Contains(item));
        imageMagickCodecInfo.FileExtensions.RemoveAll(item => _flyExtensions.Contains(item));

        _codecInfoList = [];
        _codecInfoList.AddRange(wicCodecInfos);
        _codecInfoList.AddRange(flyCodecInfos);
        if (imageMagickCodecInfo.FileExtensions.Count > 0)
            _codecInfoList.Add(imageMagickCodecInfo);

        foreach (var codecInfo in _codecInfoList)
            SupportedExtensions.UnionWith(codecInfo.FileExtensions);

        foreach (var ext in ProbableImageMagickRawExtensions)
            if (_imageMagickExtensions.Contains(ext))
                _imageMagickRawExtensions.Add(ext);

        foreach (var ext in _imageMagickExtensions)
            if (!_imageMagickRawExtensions.Contains(ext))
                _imageMagickNonRawExtensions.Add(ext);

        _rawlerRawExtensions.UnionWith(GetRawlerSupportedExtensions_FromSnapShot());
    }

    public static bool IsWicSupported(string extension) => _wicExtensions.Contains(extension);

    public static bool IsWicRaw(string extension) => _wicRawExtensions.Contains(extension);

    public static bool IsWicNonRaw(string extension) => _wicNonRawExtensions.Contains(extension);

    public static bool IsMagickSupported(string extension) => _imageMagickExtensions.Contains(extension);

    public static bool IsMagickRaw(string extension) => _imageMagickRawExtensions.Contains(extension);

    public static bool IsMagickNonRaw(string extension) => _imageMagickNonRawExtensions.Contains(extension);

    public static bool IsRawlerRaw(string extension) => _rawlerRawExtensions.Contains(extension);

    public static IReadOnlyList<CodecInfo> GetAllCodecs()
    {
        return _codecInfoList;
    }

    private static List<CodecInfo> GetWicCodecs() => NativeWrapper.GetWicDecoders();

    private static List<CodecInfo> GetFlyCodecs()
    {
        var list = new List<CodecInfo>
        {
            new() { FriendlyName = "PSD Decoder", Type = "Fly", FileExtensions = [".psd"] },
            new() { FriendlyName = "SVG Decoder", Type = "Fly", FileExtensions = [".svg"] },
            new() { FriendlyName = "HEIC Decoder", Type = "Fly", FileExtensions = [".heic", ".heif", ".hif", ".avif"] }
        };
        return list;
    }

    private static CodecInfo GetImageMagickCodecs(string[] imageMagickExtensionsToCheck)
    {
        var magickSupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in MagickNET.SupportedFormats)
            if (format.SupportsReading)
                magickSupported.Add("." + format.Format);

        var magickExtensions = new List<string>();
        foreach (var ext in imageMagickExtensionsToCheck)
            if (magickSupported.Contains(ext))
                magickExtensions.Add(ext);

        return new CodecInfo { FriendlyName = "ImageMagick Decoder", Type = "ImageMagick", FileExtensions = magickExtensions };

    }

    // Native-load-free equivalent of GetImageMagickCodecs. Unlike GetImageMagickCodecs,
    // this method does not call MagickNET.SupportedFormats, which would cause native DLLs
    // to be loaded. This may save a few milliseconds on startup.
    private static CodecInfo GetImageMagickCodecs_StaticSnapShot(string[] imageMagickExtensionsToCheck)
    {
        var magickExtensions = new List<string>();
        foreach (var ext in imageMagickExtensionsToCheck)
            if (MagickSupportedFormatsSnapshot.Contains(ext))
                magickExtensions.Add(ext);

        return new CodecInfo { FriendlyName = "ImageMagick Decoder", Type = "ImageMagick", FileExtensions = magickExtensions };
    }

    /// <summary>
    /// Calls the Rust DLL to retrieve every RAW extension rawler can decode.
    /// Rawler returns upper-case strings without a leading dot (e.g. "ARW"),
    /// so we lower-case them and prepend "."
    /// </summary>
    private static IEnumerable<string> GetRawlerSupportedExtensions()
    {
        var result = new List<string>();
        nint formats = NativeRustBridge.rawler_get_supported_formats(out int size);
        if (formats == nint.Zero || size <= 0)
            return result;

        try
        {
            for (int i = 0; i < size; i++)
            {
                nint strPtr = Marshal.ReadIntPtr(formats, i * IntPtr.Size);
                string? ext = Marshal.PtrToStringAnsi(strPtr);
                if (!string.IsNullOrWhiteSpace(ext))
                    result.Add("." + ext.ToLowerInvariant());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CodecDiscovery] Failed to marshal rawler supported formats: {ex}");
        }
        finally
        {
            NativeRustBridge.free_formats_buffer(formats, size);
        }

        return result;
    }

    // Native-load-free equivalent of GetRawlerSupportedExtensions. Unlike GetRawlerSupportedExtensions,
    // this method does not call the Rust DLL, which would cause native DLLs
    // to be loaded. This may save a few milliseconds on startup.
    private static IEnumerable<string> GetRawlerSupportedExtensions_FromSnapShot() => RawlerSupportedFormatsSnapshot;

    // Snapshot of MagickNET.SupportedFormats (SupportsReading == true) for the bundled Magick.NET build,
    // captured as the enum-name strings the live query produces (e.g. ".ThreeFr", ".Cr2"). Filtering this
    // against the probable-extension arrays yields byte-identical results to GetImageMagickCodecs WITHOUT
    // touching MagickNET — so the native Magick library no longer loads during CodecDiscovery's static ctor.
    // Re-snapshot if the bundled Magick.NET version changes.
    private static readonly HashSet<string> MagickSupportedFormatsSnapshot = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".3G2", ".3Gp", ".A", ".Aai", ".Ai", ".APng", ".Art", ".Arw", ".Avci",
        ".Avi", ".Avif", ".Avs", ".B", ".Bayer", ".Bayera", ".Bgr", ".Bgra", ".Bgro", ".Bmp",
        ".Bmp2", ".Bmp3", ".C", ".Cal", ".Cals", ".Canvas", ".Caption", ".Cin", ".Clip", ".Clipboard",
        ".Cmyk", ".Cmyka", ".Cr2", ".Cr3", ".Crw", ".Cube", ".Cur", ".Cut", ".Data", ".Dcm",
        ".Dcr", ".Dcraw", ".Dcx", ".Dds", ".Dfont", ".Dng", ".Dpx", ".Dxt1", ".Dxt5", ".Emf",
        ".Epdf", ".Epi", ".Eps", ".Epsf", ".Epsi", ".Ept", ".Ept2", ".Ept3", ".Erf", ".Exr",
        ".Farbfeld", ".Fax", ".Ff", ".Fff", ".File", ".Fits", ".Fl32", ".Flv", ".Fractal", ".Ftp",
        ".Fts", ".Ftxt", ".G", ".G3", ".G4", ".Gif", ".Gif87", ".Gradient", ".Gray", ".Graya",
        ".Group4", ".Hald", ".Hdr", ".Heic", ".Heif", ".Hrz", ".Http", ".Https", ".Icb", ".Icn",
        ".Ico", ".Icon", ".Iiq", ".Inline", ".Ipl", ".J2c", ".J2k", ".Jng", ".Jnx", ".Jp2",
        ".Jpc", ".Jpe", ".Jpeg", ".Jpg", ".Jpm", ".Jps", ".Jpt", ".Jxl", ".K", ".K25",
        ".Kdc", ".Label", ".M", ".M2v", ".M4v", ".Mac", ".Map", ".Mask", ".Mat", ".Mdc",
        ".Mef", ".Miff", ".Mkv", ".Mng", ".Mono", ".Mos", ".Mov", ".Mp4", ".Mpc", ".Mpeg",
        ".Mpg", ".Mpo", ".Mrw", ".Msl", ".Msvg", ".Mtv", ".Mvg", ".Nef", ".Nrw", ".Null",
        ".O", ".Ora", ".Orf", ".Otb", ".Otf", ".Pal", ".Palm", ".Pam", ".Pango", ".Pattern",
        ".Pbm", ".Pcd", ".Pcds", ".Pcl", ".Pct", ".Pcx", ".Pdb", ".Pdf", ".Pdfa", ".Pef",
        ".Pes", ".Pfa", ".Pfb", ".Pfm", ".Pgm", ".Pgx", ".Phm", ".Picon", ".Pict", ".Pix",
        ".Pjpeg", ".Plasma", ".Png", ".Png00", ".Png24", ".Png32", ".Png48", ".Png64", ".Png8", ".Pnm",
        ".Pocketmod", ".Ppm", ".Ps", ".Psb", ".Psd", ".Ptif", ".Pwp", ".Qoi", ".R", ".RadialGradient",
        ".Raf", ".Ras", ".Raw", ".Rgb", ".Rgb565", ".Rgba", ".Rgbo", ".Rgf", ".Rla", ".Rle",
        ".Rmf", ".Rsvg", ".Rw2", ".Rwl", ".Scr", ".Screenshot", ".Sct", ".Sf3", ".Sfw", ".Sgi",
        ".Six", ".Sixel", ".Sr2", ".Srf", ".Srw", ".Stegano", ".Sti", ".StrImg", ".Sun", ".Svg",
        ".Svgz", ".Text", ".Tga", ".Tiff", ".Tiff64", ".Tile", ".Tim", ".Tm2", ".Ttc", ".Ttf",
        ".Txt", ".Uyvy", ".Vda", ".Vicar", ".Vid", ".Viff", ".Vips", ".Vst", ".Wbmp", ".WebM",
        ".WebP", ".Wmf", ".Wmv", ".Wpg", ".X3f", ".Xbm", ".Xc", ".Xcf", ".Xpm", ".Xps",
        ".Xv", ".Y", ".Ycbcr", ".Ycbcra", ".Yuv", ".Dib", ".Tif"
    };

    // Snapshot of rawler_get_supported_formats() for the bundled fly_rust_bridge.dll, already shaped the
    // way GetRawlerSupportedExtensions emits them (lower-case, leading dot). Using this instead of the live
    // P/Invoke keeps fly_rust_bridge.dll unloaded during CodecDiscovery's static ctor — the Rust DLL now
    // loads on first actual use (rawler RAW decode or SVG render). Re-snapshot if the rawler crate changes.
    private static readonly string[] RawlerSupportedFormatsSnapshot =
    [
        ".ari", ".arw", ".cr2", ".cr3", ".crm", ".crw", ".dcr", ".dcs", ".dng", ".erf",
        ".iiq", ".kdc", ".mef", ".mos", ".mrw", ".nef", ".nrw", ".orf", ".ori", ".pef",
        ".raf", ".raw", ".rw2", ".rwl", ".srw", ".3fr", ".fff", ".x3f", ".qtk"
    ];
}
