#nullable enable
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Interop;
using ImageMagick;
using System;
using System.Collections.Generic;

namespace FlyPhotos.Services;

internal static class CodecDiscovery
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly List<CodecInfo> _codecInfoList;
    private static readonly HashSet<string> _wicExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _flyExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _imageMagickExtensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _imageMagickRawExtensions = new(StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> SupportedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);


    private static readonly string[] ProbableImageMagickStandardExtensions =
    [
        // Standard
        ".bmp",".dib",".rle",".gif",".ico",".icon",".cur",
        ".jpeg",".jpe",".jpg",".jfif",".exif",
        ".png",".tiff",".tif",
        // Modern
        ".wdp",".jxr",".dds",
        ".heic",".heif",".hif",
        ".avci",".heics",".heifs",".avcs",
        ".avif",".avifs",
        ".webp",".jxl",
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
        var imageMagickCodecInfo = GetImageMagickCodecs(ProbableImageMagickExtensions);

        foreach (var codecInfo in wicCodecInfos)
            _wicExtensions.UnionWith(codecInfo.FileExtensions);
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
        

    }

    public static bool HasWicSupport(string extension) => _wicExtensions.Contains(extension);

    public static bool HasImageMagickSupport(string extension) => _imageMagickExtensions.Contains(extension);

    public static bool HasImageMagickRawFileSupport(string extension) => _imageMagickRawExtensions.Contains(extension);

    public static IReadOnlyList<CodecInfo> GetAllCodecs() => _codecInfoList;

    private static List<CodecInfo> GetWicCodecs() => NativeWrapper.GetWicDecoders() ?? [];

    private static List<CodecInfo> GetFlyCodecs()
    {
        var list = new List<CodecInfo>
        {
            new CodecInfo { FriendlyName = "PSD Decoder", Type = "Fly", FileExtensions = [".psd"] },
            new CodecInfo { FriendlyName = "SVG Decoder", Type = "Fly", FileExtensions = [".svg"] },
            new CodecInfo { FriendlyName = "HEIC Decoder", Type = "Fly", FileExtensions = [".heic", ".heif", ".hif", ".avif"] }
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
}
