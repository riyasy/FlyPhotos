#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
            // Deferred: formatting every tag across every directory is only worth paying for if
            // the user clicks "Show All". This runs later on the UI thread, outside this
            // try/catch, so guard it here — a descriptor throwing on a malformed tag must not
            // crash the app, and Lazy would otherwise cache that exception permanently.
            var all = new Lazy<IReadOnlyList<ExifFieldGroup>>(() =>
            {
                try
                {
                    return BuildAll(directories);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to build full EXIF tag list for {0}", filePath);
                    return [];
                }
            });
            return new ExifData(summary, all);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read EXIF data for {0}", filePath);
            return ExifData.Empty;
        }
    }

    private static List<ExifField> BuildSummary(string filePath, IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        // TIFF-based RAW (ARW, NEF, CR2, DNG) emits multiple Exif SubIFD directories: one
        // describing the raw/preview image structure (no capture settings) and a separate one
        // holding the real DateTimeOriginal / lens / exposure tags. FirstOrDefault() would pick
        // the wrong (empty) one, so read each tag from whichever SubIFD actually contains it.
        var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var fileInfo = new FileInfo(filePath);

        var fields = new List<ExifField>();
        AddField(fields, L.Get("Exif_FileName"), fileInfo.Name);
        AddField(fields, L.Get("Exif_FileSize"), FormatFileSize(fileInfo.Length));
        AddField(fields, L.Get("Exif_Dimensions"), GetDimensions(subIfds));
        AddField(fields, L.Get("Exif_CameraMake"), ifd0?.GetDescription(ExifDirectoryBase.TagMake));
        AddField(fields, L.Get("Exif_CameraModel"), ifd0?.GetDescription(ExifDirectoryBase.TagModel));
        AddField(fields, L.Get("Exif_DateTaken"), FormatExifDate(SubDescription(subIfds, ExifDirectoryBase.TagDateTimeOriginal)));
        AddField(fields, L.Get("Exif_LensModel"), SubDescription(subIfds, ExifDirectoryBase.TagLensModel));
        AddField(fields, L.Get("Exif_FocalLength"), SubDescription(subIfds, ExifDirectoryBase.TagFocalLength));
        AddField(fields, L.Get("Exif_Aperture"), SubDescription(subIfds, ExifDirectoryBase.TagFNumber));
        AddField(fields, L.Get("Exif_ShutterSpeed"), GetShutterSpeed(subIfds));
        AddField(fields, L.Get("Exif_Iso"), SubDescription(subIfds, ExifDirectoryBase.TagIsoEquivalent));
        AddField(fields, L.Get("Exif_ExposureBias"), SubDescription(subIfds, ExifDirectoryBase.TagExposureBias));
        AddField(fields, L.Get("Exif_MeteringMode"), SubDescription(subIfds, ExifDirectoryBase.TagMeteringMode));
        AddField(fields, L.Get("Exif_ExposureProgram"), SubDescription(subIfds, ExifDirectoryBase.TagExposureProgram));
        AddField(fields, L.Get("Exif_Flash"), SubDescription(subIfds, ExifDirectoryBase.TagFlash));
        AddField(fields, L.Get("Exif_Orientation"), ifd0?.GetDescription(ExifDirectoryBase.TagOrientation));
        AddField(fields, L.Get("Exif_ColorSpace"), SubDescription(subIfds, ExifDirectoryBase.TagColorSpace));
        AddGpsField(fields, gps);
        return fields;
    }

    private static string FormatFileSize(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:F1} GB";
        return bytes >= mb ? $"{bytes / mb:F1} MB" : $"{bytes / kb:F1} KB";
    }

    // EXIF stores dates as "yyyy:MM:dd HH:mm:ss" (colons in the date part per spec);
    // rewrite to "yyyy-MM-dd HH:mm:ss" for readability.
    private static string? FormatExifDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var parts = raw.Split(' ', 2);
        return parts.Length == 2 ? $"{parts[0].Replace(':', '-')} {parts[1]}" : raw;
    }

    private static List<ExifFieldGroup> BuildAll(IReadOnlyList<MetadataExtractor.Directory> directories)
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

    // Finds the SubIFD that carries a given tag (see BuildSummary for why more than one
    // SubIFD can exist). Null when no SubIFD holds the tag.
    private static ExifSubIfdDirectory? SubIfdWith(List<ExifSubIfdDirectory> subIfds, int tag)
        => subIfds.FirstOrDefault(s => s.ContainsTag(tag));

    private static string? SubDescription(List<ExifSubIfdDirectory> subIfds, int tag)
        => SubIfdWith(subIfds, tag)?.GetDescription(tag);

    private static string? GetDimensions(List<ExifSubIfdDirectory> subIfds)
    {
        // Use the raw pixel counts rather than GetDescription(), which appends
        // a " pixels" unit suffix to each value (e.g. "5472 pixels x 3648 pixels").
        var subIfd = SubIfdWith(subIfds, ExifDirectoryBase.TagExifImageWidth);
        if (subIfd == null) return null;
        if (!subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var width)) return null;
        if (!subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var height)) return null;
        return $"{width} × {height}";
    }

    // GetDescription() for this tag just echoes the raw EXIF rational (e.g. "19983/1000000 sec")
    // instead of the conventional "1/N sec" notation most viewers show, since many cameras store
    // exposure time as an unreduced fraction rather than a clean 1/N value. Fall back to the raw
    // description for degenerate values (e.g. a bulb-mode 0/1 sentinel) instead of hiding the field.
    private static string? GetShutterSpeed(List<ExifSubIfdDirectory> subIfds)
    {
        var subIfd = SubIfdWith(subIfds, ExifDirectoryBase.TagExposureTime);
        if (subIfd == null) return null;
        if (subIfd.TryGetDouble(ExifDirectoryBase.TagExposureTime, out var seconds) && seconds > 0)
        {
            return seconds < 1
                ? $"1/{Math.Round(1.0 / seconds)} s"
                : $"{seconds:0.##} s";
        }
        return subIfd.GetDescription(ExifDirectoryBase.TagExposureTime);
    }

    private static void AddGpsField(List<ExifField> fields, GpsDirectory? gps)
    {
        if (gps == null) return;

        if (gps.GetGeoLocation() is { IsZero: false } location)
        {
            var lat = location.Latitude.ToString(CultureInfo.InvariantCulture);
            var lon = location.Longitude.ToString(CultureInfo.InvariantCulture);
            var mapsUrl = new Uri($"https://www.google.com/maps/search/?api=1&query={lat},{lon}");
            fields.Add(new ExifField(L.Get("Exif_GpsLocation"), location.ToDmsString(), mapsUrl));
            return;
        }

        // GetGeoLocation() needs both the coordinate and its N/S/E/W ref tag to resolve; fall back
        // to the raw per-tag descriptions so a file missing/malformed only in the ref tag still
        // shows something, just without a map link.
        var latDesc = gps.GetDescription(GpsDirectory.TagLatitude);
        var lonDesc = gps.GetDescription(GpsDirectory.TagLongitude);
        if (!string.IsNullOrWhiteSpace(latDesc) && !string.IsNullOrWhiteSpace(lonDesc))
            fields.Add(new ExifField(L.Get("Exif_GpsLocation"), $"{latDesc}, {lonDesc}"));
    }

    private static void AddField(List<ExifField> fields, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields.Add(new ExifField(label, value));
    }
}
