namespace FlyPhotos.Tests;

/// <summary>Gives each test an empty folder under %TEMP%, deleted afterwards.</summary>
public abstract class TempFolderTest
{
    protected string Folder { get; private set; } = "";

    [TestInitialize]
    public void CreateFolder()
    {
        Folder = Path.Combine(Path.GetTempPath(), "FlyPhotosTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    [TestCleanup]
    public void DeleteFolder() => Directory.Delete(Folder, recursive: true);

    protected string PathOf(string fileName) => Path.Combine(Folder, fileName);
}
