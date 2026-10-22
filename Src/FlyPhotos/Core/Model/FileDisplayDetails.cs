#nullable enable

namespace FlyPhotos.Core.Model;

public readonly struct FileDisplayDetails(string? position, string fileName, string? dimensions, int rating = 0)
{
    public readonly string? Position = position;
    public readonly string FileName = fileName;
    public readonly string? Dimensions = dimensions;

    /// <summary>Star rating 0-5 shown after the file name; 0 shows nothing.</summary>
    public readonly int Rating = rating;

    public string DisplayText
    {
        get
        {
            if (Position != null && Dimensions != null)
                return string.Concat(Position, " ", FileName, " ", Dimensions);
            else if (Position != null)
                return string.Concat(Position, " ", FileName);
            else if (Dimensions != null)
                return string.Concat(FileName, " ", Dimensions);
            else
                return FileName;
        }
    }
}