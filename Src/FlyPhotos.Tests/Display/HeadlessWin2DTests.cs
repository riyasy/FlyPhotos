using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace FlyPhotos.Tests.Display;

[TestClass]
public sealed class HeadlessWin2DTests
{
    [TestMethod]
    public void A_software_device_makes_bitmaps_without_a_window()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var bitmap = CanvasBitmap.CreateFromColors(device, new Color[4], 2, 2);
        Assert.AreEqual(2u, bitmap.SizeInPixels.Width);
    }
}
