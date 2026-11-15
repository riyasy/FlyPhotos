using FlyPhotos.Services;

namespace FlyPhotos.Tests.Display.Harness;

/// <summary>One-time setup for the whole test assembly.</summary>
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

        // The real decoder's disk cache goes to a temp folder, never the user's.
        DiskCacherWithSqlite.Folder = DiskCacheFolder;
    }

    /// <summary>The disk cache's folder for this test run.</summary>
    public static readonly string DiskCacheFolder =
        Path.Combine(Path.GetTempPath(), "FlyTestDiskCache_" + Guid.NewGuid().ToString("N"));

    [AssemblyCleanup]
    public static void RemoveTheDiskCache()
    {
        DiskCacherWithSqlite.Shutdown();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // the pool keeps the file open otherwise
        try { Directory.Delete(DiskCacheFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
