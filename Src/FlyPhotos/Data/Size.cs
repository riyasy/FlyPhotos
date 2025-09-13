#nullable enable
namespace FlyPhotos.Data;

internal class Size(double w, double h)
{
    public double Width { get; } = w;
    public double Height { get; } = h;

    public static Size FromFoundationSize(Windows.Foundation.Size foundationSize)
    {
        return new Size(foundationSize.Width, foundationSize.Height);
    }
}