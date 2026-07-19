using System.IO;
using System.Linq;
using SmartCon.Updater;
using Xunit;

namespace SmartCon.Tests.Updater;

public class ObsoleteFileCleanerTests
{
    [Fact]
    public void Parse_FiltersCommentsAndBlankLines()
    {
        var lines = new[]
        {
            "# comment",
            "",
            "   ",
            "CommunityToolkit.Mvvm.dll",
            "  System.Text.Json.dll  "
        };

        var result = ObsoleteFileCleaner.Parse(lines);

        Assert.Equal(2, result.Count);
        Assert.Contains("CommunityToolkit.Mvvm.dll", result);
        Assert.Contains("System.Text.Json.dll", result);
    }

    [Theory]
    [InlineData("..\\evil.dll")]
    [InlineData("../evil.dll")]
    [InlineData("sub\\file.dll")]
    [InlineData("sub/file.dll")]
    [InlineData("malware.exe")]
    [InlineData("script.bat")]
    [InlineData("noextension")]
    [InlineData(" .dll")]
    [InlineData(".dll")]
    [InlineData("..dll")]
    public void Parse_RejectsUnsafeEntries(string entry)
    {
        Assert.Empty(ObsoleteFileCleaner.Parse(new[] { entry }));
    }

    [Fact]
    public void Parse_DeduplicatesCaseInsensitive()
    {
        var result = ObsoleteFileCleaner.Parse(new[] { "A.dll", "a.DLL" });
        Assert.Single(result);
    }

    [Fact]
    public void DeleteFrom_DeletesOnlyListedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartcon-obsolete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var obsoletePath = Path.Combine(dir, "Old.dll");
            var keepPath = Path.Combine(dir, "Keep.dll");
            File.WriteAllText(obsoletePath, "old");
            File.WriteAllText(keepPath, "keep");

            var deleted = ObsoleteFileCleaner.DeleteFrom(dir, new[] { "Old.dll", "Missing.dll" }, _ => { });

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(obsoletePath));
            Assert.True(File.Exists(keepPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
