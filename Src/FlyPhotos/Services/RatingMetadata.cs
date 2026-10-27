#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FlyPhotos.Core.Model;
using FlyPhotos.Infra.Utils;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.Xmp;

namespace FlyPhotos.Services;

/// <summary>
/// Reads and writes <c>xmp:Rating</c> (0-5). JPEG, TIFF/DNG and PNG get the XMP packet embedded without touching
/// the image data. Proprietary RAW gets a sidecar .xmp, since those containers are not safe to rewrite. Existing XMP
/// is edited as text: only the rating values change.
/// Ratings set in camera are read too. Changing one writes JPEG/TIFF/PNG (XMP, plus existing EXIF rating tags) or the
/// sidecar, never the RAW.
/// </summary>
internal static class RatingMetadata
{
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string MicrosoftPhotoNamespace = "http://ns.microsoft.com/photo/1.0/";

    // Writes read existing XMP strictly: invalid bytes fail the write instead of being replaced and written back.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private const string XmpSegmentId = "http://ns.adobe.com/xap/1.0/";
    private const int MaxXmpPayloadBytes = 65504; // 65535 - 2 length bytes - 29 header bytes

    // Marker bytes
    private const int Soi = 0xD8, Eoi = 0xD9, Sos = 0xDA, App0 = 0xE0, App1 = 0xE1, App2 = 0xE2;

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".jfif" };

    /// <summary>Rating 0-5 from the sidecar or the file itself; null when neither has rating info.</summary>
    public static int? ReadRating(string filePath)
    {
        // Same lock as writes, so a read never races a file being swapped in.
        lock (PathLocks.GetOrAdd(filePath, _ => new object()))
        {
            try
            {
                // The file's own rating is the source of truth. A sidecar (Fly with embedding off, a file Fly could
                // not patch, or darktable/digiKam) is used only when the file has none. Modified dates are not compared:
                // they change with any edit, not only rating changes.
                if (IsEmbeddable(filePath))
                    return ReadInFileRating(filePath) ?? ReadSidecarRating(SidecarPaths(filePath));
                // A sidecar wins even at 0: that is how clearing a rating set in camera is recorded.
                return ReadSidecarRating(SidecarPaths(filePath)) ?? ReadInFileRating(filePath);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Writes rating 0-5 (0 clears). <paramref name="embedInFile"/> false keeps JPEG/TIFF/PNG untouched and uses a
    /// sidecar instead. Never throws.
    /// </summary>
    public static RatingResult WriteRating(string filePath, int rating, bool embedInFile)
    {
        rating = Math.Clamp(rating, 0, 5);
        var gate = PathLocks.GetOrAdd(filePath, _ => new object());
        lock (gate)
        {
            try
            {
                // Clearing a rating stored in the file (by the camera or another app) needs an explicit 0 written
                // over it, or the old value shows again on the next read.
                var recordZero = rating == 0 && ReadInFileRating(filePath) is > 0;

                var embedded = embedInFile && (IsJpeg(filePath) ? TryWriteJpeg(filePath, rating, recordZero)
                    : IsTiffBased(filePath) ? TryWriteTiff(filePath, rating, recordZero)
                    : IsPng(filePath) && TryWritePng(filePath, rating, recordZero));

                var sidecars = SidecarPaths(filePath);
                if (!embedded && (IsEmbeddable(filePath) || IsProprietaryRaw(filePath)))
                {
                    // RAW, or JPEG/TIFF/PNG with embedding off or unsafe (XMP over 64 KB, unreadable MPF, BigTIFF, broken PNG).
                    WriteSidecars(sidecars, rating, createAtZero: recordZero);
                    return new RatingResult(true, rating: rating);
                }

                // Embedded: keep an existing sidecar in step so apps that read it agree, never create one.
                // Formats Fly does not write into get no new .xmp either, since Lightroom and most apps only read
                // ratings stored inside those files. One another app created (darktable) is kept in step.
                var existing = sidecars.Where(File.Exists).ToList();
                if (!embedded && existing.Count == 0) return new RatingResult(false, unsupported: true);
                if (existing.Count > 0) WriteSidecars(existing, rating);
                return new RatingResult(true, rating: rating);
            }
            catch (Exception ex)
            {
                return new RatingResult(false, ex.Message);
            }
        }
    }

    /// <summary>
    /// Sidecars that belong to this file alone, for rename/delete housekeeping (existing files only).
    /// IMG.xmp is left out when another RAW shares the name (e.g. IMG.CR2 next to IMG.NEF): it belongs to both.
    /// </summary>
    public static List<string> ExistingSidecars(string filePath)
    {
        var shared = Path.ChangeExtension(filePath, ".xmp");
        return SidecarPaths(filePath).Where(File.Exists)
            .Where(path => !string.Equals(path, shared, StringComparison.OrdinalIgnoreCase) || !HasSameNamedRaw(filePath))
            .ToList();
    }

    private static bool HasSameNamedRaw(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)) return false;
        // Compare names, not paths: the listing may spell the folder differently (slashes, case).
        var name = Path.GetFileName(filePath);
        return System.IO.Directory.EnumerateFiles(folder, Path.GetFileNameWithoutExtension(filePath) + ".*")
            .Any(other => !string.Equals(Path.GetFileName(other), name, StringComparison.OrdinalIgnoreCase)
                          && IsProprietaryRaw(other));
    }

    /// <summary>Where <paramref name="oldSidecar"/> goes when <paramref name="oldFilePath"/> becomes <paramref name="newFilePath"/>.</summary>
    public static string RenamedSidecar(string oldSidecar, string oldFilePath, string newFilePath) =>
        string.Equals(oldSidecar, oldFilePath + ".xmp", StringComparison.OrdinalIgnoreCase)
            ? newFilePath + ".xmp"
            : Path.ChangeExtension(newFilePath, ".xmp");

    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".tif", ".tiff" };

    private static bool IsJpeg(string filePath) => JpegExtensions.Contains(Path.GetExtension(filePath));

    // DNG is TIFF-based and Lightroom ignores sidecars for it, so it embeds like TIFF.
    private static bool IsTiffBased(string filePath) => TiffExtensions.Contains(Path.GetExtension(filePath));

    private static bool IsPng(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmbeddable(string filePath) => IsJpeg(filePath) || IsTiffBased(filePath) || IsPng(filePath);

    // ---------- XMP packet ----------

    /// <summary>
    /// Returns <paramref name="xml"/> with the rating set, or a fresh packet when it is null (<paramref name="standalone"/>:
    /// for an .xmp file). An existing packet is edited as text: only the rating values change and every other byte
    /// stays as its writer left it. Throws when that is not possible or the result does not read back as
    /// <paramref name="rating"/>, so a damaged packet is never written.
    /// </summary>
    private static string ApplyRating(string? xml, int rating, bool standalone = false)
    {
        if (string.IsNullOrWhiteSpace(xml)) return NewPacket(rating, standalone);

        // NUL or space padding after the packet is kept but is not part of the XML.
        var body = xml.TrimEnd('\0', ' ');
        var edited = EditRatingText(body, rating);
        if (ParseRating(edited) != rating)
            throw new InvalidDataException("The rating could not be changed without rewriting the XMP.");
        return edited + xml[body.Length..];
    }

    private static string EditRatingText(string xml, int rating)
    {
        var value = rating.ToString(CultureInfo.InvariantCulture);
        var edited = ReplacePropertyValue(xml, Xmp.NamespaceName, "Rating", value, out var found);
        if (!found) edited = InsertRatingAttribute(xml, value);
        return ReplacePropertyValue(edited, MicrosoftPhotoNamespace, "Rating",
            MicrosoftPhotoRating(rating).ToString(CultureInfo.InvariantCulture), out _);
    }

    /// <summary>
    /// Replaces the value of every <paramref name="name"/> property in <paramref name="namespaceUri"/>, as attribute or
    /// element, under whatever prefix the writer declared for it (xmp, xap, ...) and in either quote style.
    /// </summary>
    private static string ReplacePropertyValue(string xml, string namespaceUri, string name, string value, out bool found)
    {
        found = false;
        foreach (var prefix in PrefixesOf(xml, namespaceUri))
        {
            var property = Regex.Escape(prefix) + ":" + name;
            var attribute = new Regex(@"(\s" + property + @"\s*=\s*)(""[^""]*""|'[^']*')");
            var element = new Regex(@"(<" + property + @"(?:\s[^>]*)?>)[^<]*(</" + property + @"\s*>)");
            found |= attribute.IsMatch(xml) || element.IsMatch(xml);
            xml = attribute.Replace(xml, m => m.Groups[1].Value + m.Groups[2].Value[0] + value + m.Groups[2].Value[0]);
            xml = element.Replace(xml, m => m.Groups[1].Value + value + m.Groups[2].Value);
        }
        return xml;
    }

    /// <summary>Adds xmp:Rating to the first rdf:Description start tag. Used only when the packet has no rating yet.</summary>
    private static string InsertRatingAttribute(string xml, string value)
    {
        var rdfPrefixes = string.Join("|", PrefixesOf(xml, Rdf.NamespaceName).Select(Regex.Escape));
        // Quoted attribute values may contain '>', so the tag end is found by walking past them.
        var tag = rdfPrefixes.Length == 0 ? Match.Empty
            : Regex.Match(xml, @"<(?:" + rdfPrefixes + @"):Description\b(?:[^>""']|""[^""]*""|'[^']*')*");
        if (!tag.Success) throw new InvalidDataException("The XMP has no rdf:Description to hold the rating.");

        var insertAt = tag.Index + tag.Length;
        if (xml[insertAt - 1] == '/') insertAt--;
        // Declared on the tag itself, so the prefix is in scope whatever the rest of the packet declares.
        var declaration = Regex.IsMatch(tag.Value, @"\sxmlns:xmp\s*=") ? "" : $" xmlns:xmp=\"{Xmp.NamespaceName}\"";
        return xml.Insert(insertAt, $"{declaration} xmp:Rating=\"{value}\"");
    }

    private static IEnumerable<string> PrefixesOf(string xml, string namespaceUri) =>
        Regex.Matches(xml, @"xmlns:([\w.-]+)\s*=\s*([""'])" + Regex.Escape(namespaceUri) + @"\2")
            .Select(m => m.Groups[1].Value).Distinct();

    private static int? ParseRating(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        // Some writers pad the packet with NULs, which XDocument rejects.
        var doc = XDocument.Parse(xml.TrimEnd('\0', ' '));
        foreach (var description in doc.Descendants(Rdf + "Description"))
        {
            var text = description.Attribute(Xmp + "Rating")?.Value ?? description.Element(Xmp + "Rating")?.Value;
            if (ParseRatingValue(text) is { } rating) return rating;
        }
        return null;
    }

    // Spec type is Real, so "3.0" is valid. Adobe's -1 (rejected) clamps to 0 and shows no stars.
    private static int? ParseRatingValue(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp((int)Math.Round(parsed), 0, 5)
            : null;

    // ---------- rating stored in the file ----------

    // Sony and Fujifilm cameras keep the in-camera rating in their maker notes (ExifTool: Sony 0x2002, FujiFilm 0x1431).
    private const int SonyRatingTag = 0x2002;
    private const int FujifilmRatingTag = 0x1431;

    /// <summary>What the file itself says, ignoring sidecars. Null when it has no rating or cannot be read.</summary>
    private static int? ReadInFileRating(string filePath)
    {
        // Each source fails on its own: malformed XMP must not hide a valid EXIF or maker-note rating.
        int? rating = null;
        try
        {
            rating = IsJpeg(filePath) ? ReadJpegRating(filePath)
                : IsTiffBased(filePath) ? ReadTiffRating(filePath)
                : IsPng(filePath) ? ReadPngRating(filePath)
                : null;
        }
        catch
        {
            // fall through to MetadataExtractor
        }
        return rating ?? ReadCameraRating(filePath);
    }

    /// <summary>
    /// Rating set in camera or by another app, for formats Fly does not write into (RAW, HEIC) and for JPEG/TIFF/PNG
    /// without an XMP rating. Checks XMP, then EXIF Rating (0x4746), then Sony and Fujifilm maker notes. Read-only.
    /// </summary>
    private static int? ReadCameraRating(string filePath)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(filePath);
        }
        catch
        {
            return null; // formats MetadataExtractor cannot parse
        }

        foreach (var xmp in directories.OfType<XmpDirectory>())
        {
            string? text;
            try
            {
                text = xmp.XmpMeta?.GetPropertyString(Xmp.NamespaceName, "Rating");
            }
            catch
            {
                continue; // malformed XMP property: try the next source
            }
            if (ParseRatingValue(text) is { } rating) return rating;
        }

        return FirstTagRating(directories.OfType<ExifDirectoryBase>(), ExifDirectoryBase.TagRating)
               ?? FirstTagRating(directories.OfType<SonyType1MakernoteDirectory>(), SonyRatingTag)
               ?? FirstTagRating(directories.OfType<FujifilmMakernoteDirectory>(), FujifilmRatingTag);
    }

    private static int? FirstTagRating(IEnumerable<MetadataExtractor.Directory> directories, int tag)
    {
        foreach (var directory in directories)
            if (directory.TryGetInt32(tag, out var rating))
                return Math.Clamp(rating, 0, 5);
        return null;
    }

    private static int MicrosoftPhotoRating(int rating) => rating switch
    {
        0 => 0, 1 => 1, 2 => 25, 3 => 50, 4 => 75, _ => 99
    };

    // Inside JPEG/TIFF/PNG the packet keeps its xpacket wrapper, which lets readers find it in binary data.
    // A standalone .xmp file has none, like Adobe's own (darktable reportedly skips wrapped ones).
    private static string NewPacket(int rating, bool standalone)
    {
        var meta =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:Rating=\"" +
            rating.ToString(CultureInfo.InvariantCulture) + "\"/>" +
            "</rdf:RDF></x:xmpmeta>";
        return standalone
            ? meta + "\n"
            : "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" + meta + "\n<?xpacket end=\"w\"?>";
    }

    // ---------- sidecar ----------

    /// <summary>
    /// Sidecar names for <paramref name="filePath"/>, the one Fly creates first. RAW uses the Adobe name IMG.xmp
    /// (Lightroom, Bridge, Capture One; darktable imports it too), plus IMG.CR2.xmp (darktable, digiKam) when that
    /// already exists. Everything else uses photo.heic.xmp: the plain photo.xmp would belong to a RAW with the
    /// same name (RAW + HEIF or RAW + JPEG shooting).
    /// </summary>
    private static List<string> SidecarPaths(string filePath)
    {
        var withExtension = filePath + ".xmp";
        return IsProprietaryRaw(filePath) ? [Path.ChangeExtension(filePath, ".xmp"), withExtension] : [withExtension];
    }

    // DNG is RAW too, but it embeds like TIFF.
    private static bool IsProprietaryRaw(string filePath) =>
        !IsEmbeddable(filePath) && CodecDiscovery.IsRawFile(Path.GetExtension(filePath));

    /// <summary>
    /// Rating from the first readable sidecar in <paramref name="candidates"/> order: the standard IMG.xmp wins
    /// whenever it exists, IMG.CR2.xmp (darktable) is used only without it. A corrupt file is skipped.
    /// </summary>
    private static int? ReadSidecarRating(List<string> candidates)
    {
        foreach (var path in candidates.Where(File.Exists))
            if (TryReadSidecarFile(path, out var rating)) return rating;
        return null;
    }

    /// <summary>False when the file cannot be read or parsed; true with a null rating when it has none.</summary>
    private static bool TryReadSidecarFile(string path, out int? rating)
    {
        try
        {
            rating = ParseRating(File.ReadAllText(path, Encoding.UTF8));
            return true;
        }
        catch
        {
            rating = null;
            return false;
        }
    }

    /// <summary>
    /// Writes the first of <paramref name="candidates"/> (creating it if needed) and updates every other one that
    /// already exists. Rating 0 creates a file only with <paramref name="createAtZero"/>, to override a rating
    /// stored in the photo itself.
    /// </summary>
    private static void WriteSidecars(List<string> candidates, int rating, bool createAtZero = false)
    {
        var targets = candidates.Take(1).Concat(candidates.Skip(1).Where(File.Exists));

        foreach (var target in targets)
        {
            var exists = File.Exists(target);
            if (!exists && rating == 0 && !createAtZero) continue;
            WriteAtomic(target, exists
                ? EditExistingSidecar(target, rating)
                : StrictUtf8.GetBytes(ApplyRating(null, rating, standalone: true)));
        }
    }

    /// <summary>Changes only the rating in an existing sidecar. Every other byte, a BOM included, stays as it is.</summary>
    private static byte[] EditExistingSidecar(string target, int rating)
    {
        var bytes = File.ReadAllBytes(target);
        var bom = Encoding.UTF8.GetPreamble();
        var hasBom = bytes.AsSpan().StartsWith(bom);
        var text = StrictUtf8.GetString(bytes, hasBom ? bom.Length : 0, bytes.Length - (hasBom ? bom.Length : 0));
        var edited = StrictUtf8.GetBytes(ApplyRating(text, rating, standalone: true));
        return hasBom ? [.. bom, .. edited] : edited;
    }

    private static void WriteAtomic(string target, byte[] contents) =>
        ReplaceViaTemp(target, temp =>
        {
            File.WriteAllBytes(temp, contents);
            return true;
        });

    /// <summary>
    /// Runs <paramref name="writeTemp"/> against a temp path next to <paramref name="target"/>, then swaps the temp in.
    /// writeTemp returns false when the file cannot be patched, or true without creating the temp when there is
    /// nothing to change; the target is left alone in both cases. The temp never outlives the call.
    /// </summary>
    private static bool ReplaceViaTemp(string target, Func<string, bool> writeTemp)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!writeTemp(temp)) return false;
            if (!File.Exists(temp)) return true;
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
            return true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ---------- JPEG ----------

    /// <summary>Start includes any 0xFF fill bytes before the marker; PayloadStart is after the length field.</summary>
    private readonly record struct Segment(long Start, long PayloadStart, long End, int Marker, string Id);

    private static Segment? FindSegment(List<Segment> segments, int marker, string id)
    {
        foreach (var segment in segments)
            if (segment.Marker == marker && segment.Id == id) return segment;
        return null;
    }

    private static int? ReadJpegRating(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var segments = ScanJpeg(stream);
        return segments != null && FindSegment(segments, App1, XmpSegmentId) is { } xmp ? ParseRating(ReadXmp(stream, xmp)) : null;
    }

    /// <summary>Embeds the rating; false when the file cannot be patched safely (caller falls back to a sidecar).</summary>
    private static bool TryWriteJpeg(string filePath, int rating, bool recordZero) => ReplaceViaTemp(filePath, temp =>
    {
        using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var segments = ScanJpeg(source);
        if (segments == null) return false;

        // Multi-picture JPEG (Ultra HDR, motion photo): its MP entries must follow the size change below.
        var mpf = FindSegment(segments, App2, "MPF");
        var mpEntries = mpf is { } m ? ReadMpEntries(source, m) : null;
        if (mpf != null && mpEntries == null) return false;

        var existing = FindSegment(segments, App1, XmpSegmentId);
        if (existing == null && rating == 0 && !recordZero) return true;

        var xml = ApplyRating(existing is { } e ? ReadXmp(source, e) : null, rating);
        var payload = StrictUtf8.GetBytes(xml);
        if (payload.Length > MaxXmpPayloadBytes) return false;

        var header = Encoding.ASCII.GetBytes(XmpSegmentId + "\0");
        var length = 2 + header.Length + payload.Length;

        // Replace in place, or insert after the leading JFIF/Exif segments.
        var cutStart = existing?.Start ?? InsertionOffset(segments);
        var cutEnd = existing?.End ?? cutStart;

        using var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite);
        CopyRange(source, destination, 0, cutStart);
        destination.Write([0xFF, App1, (byte)(length >> 8), (byte)length]);
        destination.Write(header);
        destination.Write(payload);
        CopyRange(source, destination, cutEnd, source.Length);

        // A segment moved by the size change only when it sits after the XMP segment.
        var delta = 2 + length - (cutEnd - cutStart);
        long ShiftOf(Segment segment) => segment.Start < cutStart ? 0 : delta;

        if (FindSegment(segments, App1, "Exif") is { } exif)
            SyncExifRating(destination, exif.PayloadStart + ShiftOf(exif) + 6, rating, exif.End + ShiftOf(exif)); // 6 = "Exif\0\0"

        if (mpf is { } mpfSegment && mpEntries is { } entries && delta != 0)
            ShiftMpEntries(destination, entries.Header, entries.At + ShiftOf(mpfSegment), entries.Count, delta,
                shiftOffsets: mpfSegment.Start < cutStart);
        return true;
    });

    private const int MpEntryTag = 0xB002;

    /// <summary>
    /// The MP entry table of a multi-picture JPEG: absolute position and entry count (16 bytes each), plus the MP
    /// header layout for its byte order. Null when the MPF segment cannot be read.
    /// </summary>
    private static (TiffLayout Header, long At, int Count)? ReadMpEntries(Stream stream, Segment mpf)
    {
        var header = ReadTiffLayout(stream, mpf.PayloadStart + 4, mpf.End); // 4 = "MPF\0"
        if (header == null) return null;
        for (var i = 0; i < header.EntryCount; i++)
        {
            if (header.Tag(i) != MpEntryTag) continue;
            var count = header.Count(i) / 16; // type UNDEFINED: the count is in bytes
            var at = header.Value(i);
            return count >= 1 && at + count * 16 <= mpf.End ? (header, at, (int)count) : null;
        }
        return null;
    }

    /// <summary>
    /// Keeps the MP entries valid after the primary image grew by <paramref name="delta"/> bytes. Entry 0 holds the
    /// primary image size. The other entries hold offsets relative to the MP header, which change only when the
    /// header sits before the edit (<paramref name="shiftOffsets"/>); otherwise header and images moved together.
    /// </summary>
    private static void ShiftMpEntries(Stream stream, TiffLayout header, long at, int count, long delta, bool shiftOffsets)
    {
        var entries = new byte[count * 16];
        stream.Position = at;
        stream.ReadExactly(entries);
        if (header.U32(entries, 4) is var primarySize and > 0)
            header.Put32(entries, 4, primarySize + delta);
        if (shiftOffsets)
            for (var i = 1; i < count; i++)
                if (header.U32(entries, i * 16 + 8) is var offset and > 0)
                    header.Put32(entries, i * 16 + 8, offset + delta);
        stream.Position = at;
        stream.Write(entries);
    }

    private static long InsertionOffset(List<Segment> segments)
    {
        long offset = 2;
        foreach (var segment in segments)
        {
            var leading = segment.Marker == App0 || segment is { Marker: App1, Id: "Exif" };
            if (!leading) break;
            offset = segment.End;
        }
        return offset;
    }

    private static string ReadXmp(Stream stream, Segment segment)
    {
        var start = segment.PayloadStart + XmpSegmentId.Length + 1;
        var buffer = new byte[segment.End - start];
        stream.Position = start;
        stream.ReadExactly(buffer);
        return StrictUtf8.GetString(buffer); // padding kept: ParseRating trims it, ApplyRating writes it back
    }

    private static List<Segment>? ScanJpeg(Stream stream)
    {
        if (stream.Length < 4) return null;
        stream.Position = 0;
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != Soi) return null;

        var segments = new List<Segment>();
        var idBuffer = new byte[40];
        long position = 2;
        while (true)
        {
            stream.Position = position;
            if (stream.ReadByte() != 0xFF) return null;
            int marker;
            do marker = stream.ReadByte(); while (marker == 0xFF);
            if (marker < 0) return null;
            if (marker is Sos or Eoi) return segments;

            // Markers without a length field
            if (marker is 0x01 or Soi || marker is >= 0xD0 and <= 0xD7 || marker == 0x00)
            {
                position = stream.Position;
                continue;
            }

            var high = stream.ReadByte();
            var low = stream.ReadByte();
            if (low < 0) return null;
            var length = (high << 8) | low;
            if (length < 2) return null;

            var payloadStart = stream.Position;
            var end = payloadStart + length - 2;
            if (end > stream.Length) return null;

            var id = "";
            if (marker is App1 or App2)
            {
                var read = stream.Read(idBuffer, 0, (int)Math.Min(idBuffer.Length, length - 2));
                var nul = Array.IndexOf(idBuffer, (byte)0, 0, read);
                id = Encoding.ASCII.GetString(idBuffer, 0, nul < 0 ? read : nul);
            }

            segments.Add(new Segment(position, payloadStart, end, marker, id));
            position = end;
        }
    }

    // ---------- PNG ----------

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const string PngXmpKeyword = "XML:com.adobe.xmp";

    /// <summary>Start is the length field; the data follows the 8-byte length + type header, then a 4-byte CRC.</summary>
    private readonly record struct PngChunk(long Start, int Length, string Type)
    {
        public long DataStart => Start + 8;
        public long End => DataStart + Length + 4;
    }

    /// <summary>Every chunk up to and including IEND; null for a file that is not a complete PNG.</summary>
    private static List<PngChunk>? ScanPng(Stream stream)
    {
        var signature = new byte[8];
        stream.Position = 0;
        if (stream.Read(signature, 0, 8) != 8 || !signature.AsSpan().SequenceEqual(PngSignature)) return null;

        var chunks = new List<PngChunk>();
        var header = new byte[8];
        long position = 8;
        while (position + 12 <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(header);
            var length = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            var chunk = new PngChunk(position, (int)Math.Min(length, int.MaxValue), Encoding.ASCII.GetString(header, 4, 4));
            if (length > int.MaxValue || chunk.End > stream.Length) return null;
            chunks.Add(chunk);
            if (chunk.Type == "IEND") return chunks;
            position = chunk.End;
        }
        return null;
    }

    /// <summary>The XMP iTXt chunk: its text, plus the bytes before it (keyword, flags, language tags) kept as they are.</summary>
    private sealed record PngXmp(PngChunk Chunk, byte[] Header, string Xml, bool Compressed);

    private static PngXmp? FindPngXmp(Stream stream, List<PngChunk> chunks)
    {
        foreach (var chunk in chunks)
            if (ReadPngXmp(stream, chunk) is { } xmp) return xmp;
        return null;
    }

    /// <summary>Null when <paramref name="chunk"/> is not the XMP chunk.</summary>
    private static PngXmp? ReadPngXmp(Stream stream, PngChunk chunk)
    {
        if (chunk.Type != "iTXt") return null;
        var data = new byte[chunk.Length];
        stream.Position = chunk.DataStart;
        stream.ReadExactly(data);

        var keywordEnd = Array.IndexOf(data, (byte)0);
        if (keywordEnd < 0 || Encoding.ASCII.GetString(data, 0, keywordEnd) != PngXmpKeyword) return null;
        // keyword \0, compression flag, compression method, language tag \0, translated keyword \0, text
        var languageEnd = keywordEnd + 3 < data.Length ? Array.IndexOf(data, (byte)0, keywordEnd + 3) : -1;
        var translatedEnd = languageEnd >= 0 ? Array.IndexOf(data, (byte)0, languageEnd + 1) : -1;
        if (translatedEnd < 0) return null;

        var textStart = translatedEnd + 1;
        var compressed = data[keywordEnd + 1] == 1;
        var text = data.AsSpan(textStart).ToArray();
        if (compressed)
        {
            using var inflated = new MemoryStream();
            using (var zlib = new ZLibStream(new MemoryStream(text), CompressionMode.Decompress)) zlib.CopyTo(inflated);
            text = inflated.ToArray();
        }
        return new PngXmp(chunk, data[..textStart], StrictUtf8.GetString(text), compressed);
    }

    private static int? ReadPngRating(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var chunks = ScanPng(stream);
        return chunks != null && FindPngXmp(stream, chunks) is { } xmp ? ParseRating(xmp.Xml) : null;
    }

    /// <summary>
    /// Embeds the rating in the XMP iTXt chunk. An existing chunk is replaced where it is, keeping its header bytes
    /// and compression; a new one goes before the first IDAT, where readers look for metadata. Every other chunk is
    /// copied byte for byte. False for files that are not a complete PNG.
    /// </summary>
    private static bool TryWritePng(string filePath, int rating, bool recordZero) => ReplaceViaTemp(filePath, temp =>
    {
        using var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var chunks = ScanPng(source);
        if (chunks == null) return false;

        var xmp = FindPngXmp(source, chunks);
        if (xmp == null && rating == 0 && !recordZero) return true;

        var text = StrictUtf8.GetBytes(ApplyRating(xmp?.Xml, rating));
        if (xmp is { Compressed: true })
        {
            using var deflated = new MemoryStream();
            using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(text);
            text = deflated.ToArray();
        }
        // New chunk header: keyword \0, uncompressed, method 0, empty language tag \0, empty translated keyword \0
        var header = xmp?.Header ?? [.. Encoding.ASCII.GetBytes(PngXmpKeyword), 0, 0, 0, 0, 0];
        byte[] data = [.. header, .. text];

        var cutStart = xmp?.Chunk.Start ?? chunks.First(c => c.Type is "IDAT" or "IEND").Start;
        var cutEnd = xmp?.Chunk.End ?? cutStart;

        using var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
        CopyRange(source, destination, 0, cutStart);
        WritePngChunk(destination, "iTXt", data);
        CopyRange(source, destination, cutEnd, source.Length);
        return true;
    });

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        byte[] typeAndData = [.. Encoding.ASCII.GetBytes(type), .. data];
        stream.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
        stream.Write(typeAndData);
        var crc = Crc32.Compute(typeAndData); // covers type and data, not the length
        stream.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    // ---------- TIFF / DNG ----------

    private const int TiffXmpTag = 700;
    private const int TiffByte = 1;

    private sealed class TiffLayout
    {
        public bool LittleEndian;
        public long TiffStart;  // stream position of the TIFF header; offsets stored in the file are relative to it
        public long Ifd0Offset; // absolute stream position
        public int EntryCount;
        public uint NextIfdOffset;
        public byte[] Entries = []; // EntryCount * 12 raw bytes, sorted by tag

        public int Tag(int i) => U16(Entries, i * 12);
        public long Count(int i) => U32(Entries, i * 12 + 4);

        /// <summary>Entry value as an absolute stream position (for entries whose value is an offset).</summary>
        public long Value(int i) => TiffStart + U32(Entries, i * 12 + 8);

        public int U16(byte[] b, int o) => LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o))
            : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));

        public long U32(byte[] b, int o) => LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o))
            : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

        public void Put16(byte[] b, int o, int v)
        {
            if (LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v);
            else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v);
        }

        public void Put32(byte[] b, int o, long v)
        {
            if (LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), (uint)v);
            else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), (uint)v);
        }

        /// <summary>Entry <paramref name="i"/> points at an out-of-line value (over 4 bytes) that ends by <paramref name="limit"/>.</summary>
        public bool HasPacketWithin(int i, long limit) => Count(i) > 4 && Value(i) + Count(i) <= limit;

        /// <summary>Index of the XMP entry, or -1.</summary>
        public int FindXmp()
        {
            for (var i = 0; i < EntryCount; i++)
                if (Tag(i) == TiffXmpTag) return i;
            return -1;
        }
    }

    /// <summary>
    /// Classic TIFF only (magic 42). BigTIFF and malformed files return null. <paramref name="tiffStart"/> is where the
    /// TIFF header sits (0 for a TIFF file, inside the APP1 segment for JPEG EXIF). <paramref name="end"/> bounds the
    /// TIFF data (the segment end for JPEG EXIF), so a corrupt entry count cannot reach into image data.
    /// </summary>
    private static TiffLayout? ReadTiffLayout(Stream stream, long tiffStart = 0, long? end = null)
    {
        var limit = Math.Min(end ?? stream.Length, stream.Length);
        if (limit < tiffStart + 16 || stream.Length >= uint.MaxValue - 1_000_000) return null;
        var header = new byte[8];
        stream.Position = tiffStart;
        stream.ReadExactly(header);
        var layout = new TiffLayout { TiffStart = tiffStart };
        if (header[0] == 'I' && header[1] == 'I') layout.LittleEndian = true;
        else if (!(header[0] == 'M' && header[1] == 'M')) return null;
        if (layout.U16(header, 2) != 42) return null;

        layout.Ifd0Offset = tiffStart + layout.U32(header, 4);
        if (layout.Ifd0Offset < tiffStart + 8 || layout.Ifd0Offset + 2 > limit) return null;

        var countBytes = new byte[2];
        stream.Position = layout.Ifd0Offset;
        stream.ReadExactly(countBytes);
        layout.EntryCount = layout.U16(countBytes, 0);
        var entriesLength = layout.EntryCount * 12;
        if (layout.Ifd0Offset + 2 + entriesLength + 4 > limit) return null;

        layout.Entries = new byte[entriesLength];
        stream.ReadExactly(layout.Entries);
        var next = new byte[4];
        stream.ReadExactly(next);
        layout.NextIfdOffset = (uint)layout.U32(next, 0);
        return layout;
    }

    private static string? ReadTiffXmp(Stream stream, TiffLayout layout)
    {
        var index = layout.FindXmp();
        if (index < 0) return null;
        if (!layout.HasPacketWithin(index, stream.Length)) return null;

        var buffer = new byte[layout.Count(index)];
        stream.Position = layout.Value(index);
        stream.ReadExactly(buffer);
        return StrictUtf8.GetString(buffer); // padding kept: ParseRating trims it, FitPacket reuses it
    }

    private static int? ReadTiffRating(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var layout = ReadTiffLayout(stream);
        return layout == null ? null : ParseRating(ReadTiffXmp(stream, layout));
    }

    /// <summary>
    /// Embeds the rating in IFD0's XMP tag (700) of a copy, then swaps the copy in. Existing bytes are never moved:
    /// the packet is overwritten in place when it fits, otherwise appended at the end and the tag repointed.
    /// A file without the tag gets a rewritten IFD0 appended at the end. False for files this cannot patch (BigTIFF).
    /// </summary>
    private static bool TryWriteTiff(string filePath, int rating, bool recordZero) => ReplaceViaTemp(filePath, temp =>
    {
        // Decide on the original first, so a file that needs no change (or cannot be patched) is never copied.
        string? existingXml;
        using (var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var sourceLayout = ReadTiffLayout(source);
            if (sourceLayout == null) return false;
            existingXml = ReadTiffXmp(source, sourceLayout);
        }
        if (existingXml == null && rating == 0 && !recordZero) return true;

        File.Copy(filePath, temp);
        using var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var layout = ReadTiffLayout(stream)!; // identical copy, and the path lock keeps it that way
        var index = layout.FindXmp();
        var xml = ApplyRating(existingXml, rating);
        var inPlace = index >= 0 && layout.HasPacketWithin(index, stream.Length)
            ? FitPacket(xml, layout.Count(index))
            : null;

        if (inPlace != null)
        {
            // Same size as before, so the tag stays valid and no other bytes move.
            stream.Position = layout.Value(index);
            stream.Write(inPlace);
        }
        else
        {
            // Padded, so later edits fit in place instead of appending again.
            var packet = FitPacket(xml, StrictUtf8.GetByteCount(xml) + XmpPaddingBytes)!;
            var packetOffset = AppendAligned(stream, packet);
            if (index >= 0)
            {
                var entry = layout.Entries.AsSpan(index * 12, 12).ToArray();
                layout.Put16(entry, 2, TiffByte);
                layout.Put32(entry, 4, packet.Length);
                layout.Put32(entry, 8, packetOffset - layout.TiffStart);
                stream.Position = layout.Ifd0Offset + 2 + index * 12;
                stream.Write(entry);

                // One XMP packet per file: blank the old one so nothing that scans the file finds a stale rating.
                if (layout.HasPacketWithin(index, packetOffset))
                {
                    var blank = new byte[layout.Count(index)];
                    Array.Fill(blank, (byte)' ');
                    stream.Position = layout.Value(index);
                    stream.Write(blank);
                }
            }
            else
            {
                AppendIfdWithXmpEntry(stream, layout, packet.Length, packetOffset);
            }
        }

        SyncExifRating(stream, 0, rating);
        return true;
    });

    private const int XmpPaddingBytes = 2048; // what the XMP spec suggests

    /// <summary>
    /// <paramref name="xml"/> as exactly <paramref name="totalBytes"/> bytes. The difference goes into, or comes out
    /// of, the padding right before the packet trailer (the space the XMP spec reserves for in-place edits), or the
    /// end of the packet when it has no trailer. Null when that padding is too small to absorb a growth.
    /// </summary>
    private static byte[]? FitPacket(string xml, long totalBytes)
    {
        var delta = totalBytes - StrictUtf8.GetByteCount(xml);
        var trailer = xml.LastIndexOf("<?xpacket end", StringComparison.Ordinal);
        var at = trailer >= 0 ? trailer : xml.Length;
        if (delta < 0)
        {
            var padding = 0;
            while (padding < -delta && at - padding > 0
                   && (char.IsWhiteSpace(xml[at - padding - 1]) || xml[at - padding - 1] == '\0'))
                padding++;
            if (padding < -delta) return null;
            xml = xml.Remove(at + (int)delta, (int)-delta);
        }
        else if (delta > 0)
        {
            xml = xml.Insert(at, new string(' ', (int)delta));
        }
        return StrictUtf8.GetBytes(xml);
    }

    private const int ExifRatingTag = 0x4746, ExifRatingPercentTag = 0x4749, TiffShort = 3;

    /// <summary>
    /// Keeps EXIF Rating and RatingPercent in IFD0 in step with xmp:Rating, so readers that use EXIF agree.
    /// Only patches tags that already exist: the value sits inside the entry, so no bytes move.
    /// </summary>
    private static void SyncExifRating(Stream stream, long tiffStart, int rating, long? end = null)
    {
        var layout = ReadTiffLayout(stream, tiffStart, end);
        if (layout == null) return;
        for (var i = 0; i < layout.EntryCount; i++)
        {
            int value;
            if (layout.Tag(i) == ExifRatingTag) value = rating;
            else if (layout.Tag(i) == ExifRatingPercentTag) value = MicrosoftPhotoRating(rating);
            else continue;
            if (layout.U16(layout.Entries, i * 12 + 2) != TiffShort || layout.Count(i) != 1) continue;

            var bytes = new byte[2];
            layout.Put16(bytes, 0, value);
            stream.Position = layout.Ifd0Offset + 2 + i * 12 + 8;
            stream.Write(bytes);
        }
    }

    /// <summary>Appends at an even offset (TIFF requires word alignment) and returns where the data starts.</summary>
    private static long AppendAligned(Stream stream, byte[] data)
    {
        stream.Position = stream.Length;
        if (stream.Length % 2 != 0) stream.WriteByte(0);
        var offset = stream.Position;
        stream.Write(data);
        if (stream.Length % 2 != 0) stream.WriteByte(0);
        return offset;
    }

    private static void AppendIfdWithXmpEntry(Stream stream, TiffLayout layout, int packetLength, long packetOffset)
    {
        var xmpEntry = new byte[12];
        layout.Put16(xmpEntry, 0, TiffXmpTag);
        layout.Put16(xmpEntry, 2, TiffByte);
        layout.Put32(xmpEntry, 4, packetLength);
        layout.Put32(xmpEntry, 8, packetOffset);

        // Entries must stay sorted by tag.
        var insertAt = 0;
        while (insertAt < layout.EntryCount && layout.Tag(insertAt) < TiffXmpTag) insertAt++;

        var ifd = new byte[2 + (layout.EntryCount + 1) * 12 + 4];
        layout.Put16(ifd, 0, layout.EntryCount + 1);
        Buffer.BlockCopy(layout.Entries, 0, ifd, 2, insertAt * 12);
        xmpEntry.CopyTo(ifd, 2 + insertAt * 12);
        Buffer.BlockCopy(layout.Entries, insertAt * 12, ifd, 2 + (insertAt + 1) * 12, (layout.EntryCount - insertAt) * 12);
        layout.Put32(ifd, ifd.Length - 4, layout.NextIfdOffset);

        var ifdOffset = AppendAligned(stream, ifd);

        var pointer = new byte[4];
        layout.Put32(pointer, 0, ifdOffset);
        stream.Position = 4;
        stream.Write(pointer);
    }

    private static void CopyRange(Stream source, Stream destination, long from, long to)
    {
        source.Position = from;
        var buffer = new byte[81920];
        var remaining = to - from;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
