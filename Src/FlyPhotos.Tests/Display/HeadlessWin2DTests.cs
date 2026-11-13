using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace FlyPhotos.Tests.Display;

/// <summary>The premise the display tests rest on: Win2D makes real bitmaps on a software device, with no window or GPU.</summary>
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
