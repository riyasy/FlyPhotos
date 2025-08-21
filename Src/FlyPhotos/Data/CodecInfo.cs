using System.Collections.Generic;

namespace FlyPhotos.Data;

/// <summary>
/// Represents information about a Windows Imaging Component (WIC) codec.
/// </summary>
public class CodecInfo
{
    /// <summary>
    /// The user-friendly name of the codec (e.g., "JPEG Decoder").
    /// </summary>
    public string FriendlyName { get; init; }

    /// <summary>
    /// A list of file extensions associated with this codec (e.g., ".JPG", ".JPEG").
    /// </summary>
    public List<string> FileExtensions { get; init; } = [];

}