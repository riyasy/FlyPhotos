#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Localization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using NLog;

namespace FlyPhotos.Display.ExifReading;

// Mirrors the handful of fields a typical photo viewer (Windows Photos,
// macOS Preview, Google Photos, etc.) surfaces by default, out of the
// hundreds of raw tags a photo can contain.
public static class ExifReader
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static Task<ExifData> ReadAsync(string filePath) => Task.Run(() => Read(filePath));

    private static ExifData Read(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var summary = BuildSummary(filePath, directories);
            // Deferred: formatting every tag's description across every directory is only
            // worth paying for if the user actually clicks "Show All".
            var all = new Lazy<IReadOnlyList<ExifFieldGroup>>(() => BuildAll(directories));
            return new ExifData(summary, all);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read EXIF data for {0}", filePath);
            return ExifData.Empty;
        }
    }

    private static IReadOnlyList<ExifField> BuildSummary(string filePath, IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var fileInfo = new FileInfo(filePath);

        var fields = new List<ExifField>();
        AddField(fields, L.Get("Exif_FileName"), fileInfo.Name);
        AddField(fields, L.Get("Exif_FileSize"), $"{fileInfo.Length / 1024.0:F1} KB");
        AddField(fields, L.Get("Exif_DateTaken"), subIfd?.GetDescription(ExifDirectoryBase.TagDateTimeOriginal));
        AddField(fields, L.Get("Exif_CameraMake"), ifd0?.GetDescription(ExifDirectoryBase.TagMake));
        AddField(fields, L.Get("Exif_CameraModel"), ifd0?.GetDescription(ExifDirectoryBase.TagModel));
        AddField(fields, L.Get("Exif_LensModel"), subIfd?.GetDescription(ExifDirectoryBase.TagLensModel));
        AddField(fields, L.Get("Exif_FocalLength"), subIfd?.GetDescription(ExifDirectoryBase.TagFocalLength));
        AddField(fields, L.Get("Exif_Aperture"), subIfd?.GetDescription(ExifDirectoryBase.TagFNumber));
        AddField(fields, L.Get("Exif_ShutterSpeed"), subIfd?.GetDescription(ExifDirectoryBase.TagExposureTime));
        AddField(fields, L.Get("Exif_Iso"), subIfd?.GetDescription(ExifDirectoryBase.TagIsoEquivalent));
        AddField(fields, L.Get("Exif_ExposureBias"), subIfd?.GetDescription(ExifDirectoryBase.TagExposureBias));
        AddField(fields, L.Get("Exif_MeteringMode"), subIfd?.GetDescription(ExifDirectoryBase.TagMeteringMode));
        AddField(fields, L.Get("Exif_ExposureProgram"), subIfd?.GetDescription(ExifDirectoryBase.TagExposureProgram));
        AddField(fields, L.Get("Exif_Flash"), subIfd?.GetDescription(ExifDirectoryBase.TagFlash));
        AddField(fields, L.Get("Exif_Dimensions"), GetDimensions(subIfd));
        AddField(fields, L.Get("Exif_Orientation"), ifd0?.GetDescription(ExifDirectoryBase.TagOrientation));
        AddField(fields, L.Get("Exif_ColorSpace"), subIfd?.GetDescription(ExifDirectoryBase.TagColorSpace));
        AddField(fields, L.Get("Exif_GpsLocation"), GetGpsLocation(gps));
        return fields;
    }

    private static IReadOnlyList<ExifFieldGroup> BuildAll(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var groups = new List<ExifFieldGroup>();
        foreach (var directory in directories)
        {
            var fields = directory.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag.Description))
                .Select(tag => new ExifField(tag.Name, tag.Description!))
                .ToList();
            if (fields.Count > 0) groups.Add(new ExifFieldGroup(directory.Name, fields));
        }
        return groups;
    }

    private static string? GetDimensions(ExifSubIfdDirectory? subIfd)
    {
        var width = subIfd?.GetDescription(ExifDirectoryBase.TagExifImageWidth);
        var height = subIfd?.GetDescription(ExifDirectoryBase.TagExifImageHeight);
        return width != null && height != null ? $"{width} x {height}" : null;
    }

    private static string? GetGpsLocation(GpsDirectory? gps)
    {
        var latitude = gps?.GetDescription(GpsDirectory.TagLatitude);
        var longitude = gps?.GetDescription(GpsDirectory.TagLongitude);
        return latitude != null && longitude != null ? $"{latitude}, {longitude}" : null;
    }

    private static void AddField(List<ExifField> fields, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields.Add(new ExifField(label, value));
    }
}
