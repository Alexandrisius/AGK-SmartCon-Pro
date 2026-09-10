using System.IO;
using SmartCon.Updater;
using Xunit;

namespace SmartCon.Tests.Updater;

public class AddinManifestWriterTests
{
    [Theory]
    [InlineData("2019-2020", new[] { 2019, 2020 })]
    [InlineData("2021-2023", new[] { 2021, 2022, 2023 })]
    [InlineData("2024", new[] { 2024 })]
    [InlineData("2025", new[] { 2025 })]
    [InlineData("2026", new[] { 2026 })]
    [InlineData("unknown", new int[0])]
    public void GetYearsForTargetFolder_Maps(string folder, int[] expected)
    {
        Assert.Equal(expected, AddinManifestWriter.GetYearsForTargetFolder(folder));
    }

    [Fact]
    public void WriteManifests_2025Folder_WritesManifestWithoutIsolation()
    {
        // Revit 2025 отвергает ManifestSettings — изоляцию там делает toolkit (ADR-051)
        var appData = Path.Combine(Path.GetTempPath(), "smartcon-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = AddinManifestWriter.WriteManifests(appData, "2025", _ => { });

            Assert.Equal(1, written);
            var path = Path.Combine(appData, "Autodesk", "Revit", "Addins", "2025", "SmartCon.addin");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("ManifestSettings", content);
            Assert.Contains(Path.Combine(appData, "SmartCon", "2025", "SmartCon.App.dll"), content);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void WriteManifests_2026Folder_WritesManifestWithIsolation()
    {
        var appData = Path.Combine(Path.GetTempPath(), "smartcon-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = AddinManifestWriter.WriteManifests(appData, "2026", _ => { });

            Assert.Equal(1, written);
            var path = Path.Combine(appData, "Autodesk", "Revit", "Addins", "2026", "SmartCon.addin");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("<ManifestSettings>", content);
            Assert.Contains("<ContextName>SmartCon</ContextName>", content);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void WriteManifests_Net48Folder_WritesAllYearsWithoutIsolation()
    {
        var appData = Path.Combine(Path.GetTempPath(), "smartcon-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = AddinManifestWriter.WriteManifests(appData, "2021-2023", _ => { });

            Assert.Equal(3, written);
            foreach (var year in new[] { 2021, 2022, 2023 })
            {
                var path = Path.Combine(appData, "Autodesk", "Revit", "Addins", year.ToString(), "SmartCon.addin");
                Assert.True(File.Exists(path));
                Assert.DoesNotContain("ManifestSettings", File.ReadAllText(path));
            }
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void WriteManifests_UnknownFolder_WritesNothing()
    {
        var appData = Path.Combine(Path.GetTempPath(), "smartcon-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = AddinManifestWriter.WriteManifests(appData, "unknown", _ => { });
            Assert.Equal(0, written);
        }
        finally
        {
            if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
        }
    }
}
