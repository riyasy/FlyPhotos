using FlyPhotos.Services;

namespace FlyPhotos.Tests.Display;

/// <summary>A Data folder beside the exe switches settings, cache and log into it (#174).</summary>
[TestClass]
public sealed class PortableModeTests
{
    [TestMethod]
    public void DataFolderBesideExeIsUsed()
    {
        var exe = Directory.CreateTempSubdirectory("flyportable").FullName;
        try
        {
            Assert.IsNull(PathResolver.PortableDataFolder(exe));

            var data = Directory.CreateDirectory(Path.Combine(exe, "Data")).FullName;
            Assert.AreEqual(data, PathResolver.PortableDataFolder(exe));
        }
        finally { Directory.Delete(exe, true); }
    }
}
