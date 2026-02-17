#nullable enable

namespace FlyPhotos.Core.Model;

public readonly struct FileDisplayDetails(string? position, string fileName, string? dimensions)
{
    public readonly string? Position = position;
    public readonly string FileName = fileName;
    public readonly string? Dimensions = dimensions;

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