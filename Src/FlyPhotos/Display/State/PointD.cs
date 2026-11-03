namespace FlyPhotos.Display.State;

/// <summary>
/// A point in double precision. Windows.Foundation.Point looks like double but stores floats (its WinRT layout is
/// two Singles), which can't hold a pan position at extreme zoom: around 1e9 px a float only resolves steps of
/// 128 px, so the image jumps while zooming and dragging. Mutable fields, so <c>ImagePos.X += dx</c> works.
/// </summary>
internal struct PointD(double x, double y)
{
    public double X = x;
    public double Y = y;
}
