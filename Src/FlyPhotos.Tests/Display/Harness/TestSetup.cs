using FlyPhotos.Services;

namespace FlyPhotos.Tests.Display.Harness;

[TestClass]
public static class TestSetup
{
    /// <summary>
    /// Codec discovery asks WIC for its decoders, which needs an STA thread (the app does it on its UI thread);
    /// test threads are MTA. Run it once, up front, the way the app does.
    /// </summary>
    [AssemblyInitialize]
    public static void DiscoverCodecsOnAnStaThread(TestContext context)
    {
        var thread = new Thread(() => _ = CodecDiscovery.SupportedExtensions.Count);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
