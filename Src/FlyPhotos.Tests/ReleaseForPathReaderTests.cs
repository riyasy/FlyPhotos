using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace FlyPhotos.Tests;

/// <summary>
/// A reader that opens the file by path must get it from ImageFile.ReleaseForPathReader, which closes our stream and
/// drops the cached bytes first. The Run arms are checked in the source, so a new arm is caught without a decode.
/// </summary>
[TestClass]
public sealed class ReleaseForPathReaderTests
{
    /// <summary>The source of ImageReader.cs, found relative to this test file.</summary>
    private static string ImageReaderSource([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "FlyPhotos", "Display", "ImageReading", "ImageReader.cs"));

    [TestMethod]
    public void No_Run_arm_passes_the_path_without_releasing_the_file()
    {
        var arms = Regex.Matches(ImageReaderSource(), @"DecodeStep\.(\w+)\s*=>[^\r\n]*").Select(m => m.Value).ToList();

        Assert.IsTrue(arms.Count(a => a.Contains("file.ReleaseForPathReader()")) > 5, "Run arms not found; the regex needs updating");
        CollectionAssert.AreEqual(new List<string>(), arms.Where(a => Regex.IsMatch(a, @"\bfile\.Path\b")).ToList());
    }
}
