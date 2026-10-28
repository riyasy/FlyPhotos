namespace FlyPhotos.Services;

/// <summary>
/// Stands in for the app's CodecDiscovery (WIC, ImageMagick and native DLLs) so RatingMetadata can be linked in.
/// Only the RAW check is used.
/// </summary>
internal static class CodecDiscovery
{
    internal static class Support
    {
        private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cr2", ".nef", ".arw" };

        public static bool Raw(string extension) => RawExtensions.Contains(extension);
    }
}
