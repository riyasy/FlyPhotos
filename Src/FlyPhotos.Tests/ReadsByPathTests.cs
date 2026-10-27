using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace FlyPhotos.Tests;

/// <summary>
/// ImageReader.ReadsByPath decides when the shared stream is closed before a reader runs. It must list exactly
/// the Run arms whose reader takes file.Path; ImageReader can't be loaded here (Win2D), so its source is checked.
/// </summary>
[TestClass]
public sealed class ReadsByPathTests
{
    /// <summary>The source of ImageReader.cs, found relative to this test file.</summary>
    private static string ImageReaderSource([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "FlyPhotos", "Display", "ImageReading", "ImageReader.cs"));

    [TestMethod]
    public void ReadsByPath_lists_exactly_the_Run_arms_that_pass_the_path()
    {
        var source = ImageReaderSource();

        var byPathArms = Regex.Matches(source, @"DecodeStep\.(\w+)\s*=>[^\r\n]*\bfile\.Path\b")
            .Select(m => m.Groups[1].Value).ToHashSet();

        var list = Regex.Match(source, @"ReadsByPath\(DecodeStep step\) => step is(?<body>[^;]+);").Groups["body"].Value;
        var listed = Regex.Matches(list, @"DecodeStep\.(\w+)").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.IsTrue(byPathArms.Count > 5, "Run arms not found; the regex needs updating");
        CollectionAssert.AreEquivalent(byPathArms.Order().ToList(), listed.Order().ToList());
    }
}
