using SmartCon.Core.Deployment;
using Xunit;

namespace SmartCon.Tests.Core.Deployment;

public class SmartConAddinManifestTests
{
    [Fact]
    public void Build_WithoutIsolation_OmitsManifestSettings()
    {
        var xml = SmartConAddinManifest.Build(@"C:\Users\u\AppData\Roaming\SmartCon\2024\SmartCon.App.dll", includeIsolationSettings: false);

        Assert.Contains("<Assembly>C:\\Users\\u\\AppData\\Roaming\\SmartCon\\2024\\SmartCon.App.dll</Assembly>", xml);
        Assert.DoesNotContain("ManifestSettings", xml);
        Assert.DoesNotContain("ContextName", xml);
        Assert.Contains($"<AddInId>{SmartConAddinManifest.AddInId}</AddInId>", xml);
        Assert.Contains("<FullClassName>SmartCon.App.App</FullClassName>", xml);
    }

    [Fact]
    public void Build_WithIsolation_IncludesManifestSettingsWithContextName()
    {
        var xml = SmartConAddinManifest.Build(@"C:\Users\u\AppData\Roaming\SmartCon\2025\SmartCon.App.dll", includeIsolationSettings: true);

        Assert.Contains("<ManifestSettings>", xml);
        Assert.Contains("<UseRevitContext>False</UseRevitContext>", xml);
        Assert.Contains($"<ContextName>{SmartConAddinManifest.IsolationContextName}</ContextName>", xml);
    }

    [Theory]
    [InlineData(2019, "2019-2020")]
    [InlineData(2020, "2019-2020")]
    [InlineData(2021, "2021-2023")]
    [InlineData(2022, "2021-2023")]
    [InlineData(2023, "2021-2023")]
    [InlineData(2024, "2024")]
    [InlineData(2025, "2025")]
    [InlineData(2026, "2026")]
    [InlineData(2027, "2026")]
    public void GetInstallSubfolder_MapsYears(int year, string expected)
    {
        Assert.Equal(expected, SmartConAddinManifest.GetInstallSubfolder(year));
    }

    [Theory]
    [InlineData("2019-2020", new[] { 2019, 2020 })]
    [InlineData("2021-2023", new[] { 2021, 2022, 2023 })]
    [InlineData("2024", new[] { 2024 })]
    [InlineData("2025", new[] { 2025 })]
    [InlineData("2026", new[] { 2026 })]
    public void GetManifestYearsForSubfolder_MapsBack(string subfolder, int[] expected)
    {
        Assert.Equal(expected, SmartConAddinManifest.GetManifestYearsForSubfolder(subfolder));
    }

    [Fact]
    public void GetManifestYearsForSubfolder_Unknown_ReturnsEmpty()
    {
        Assert.Empty(SmartConAddinManifest.GetManifestYearsForSubfolder("unknown-folder"));
    }
}
