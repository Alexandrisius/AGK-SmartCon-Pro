using SmartCon.Core.Deployment;
using Xunit;

namespace SmartCon.Tests.Core.Deployment;

public class DependencyConflictAnalyzerTests
{
    [Fact]
    public void Analyze_OlderVersionLoaded_ReturnsConflict()
    {
        var loaded = new[]
        {
            new LoadedDependencyInfo("CommunityToolkit.Mvvm", new Version(8, 2, 0, 0), @"C:\Addins\Other\CommunityToolkit.Mvvm.dll")
        };

        var conflicts = DependencyConflictAnalyzer.Analyze(loaded);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("CommunityToolkit.Mvvm", conflict.Name);
        Assert.Equal(new Version(8, 2, 0, 0), conflict.LoadedVersion);
        Assert.Equal(DependencyConflictAnalyzer.MinimumVersions["CommunityToolkit.Mvvm"], conflict.MinimumVersion);
        Assert.Contains("Other", conflict.Location);
    }

    [Fact]
    public void Analyze_EqualOrNewerVersion_NoConflict()
    {
        var loaded = new[]
        {
            new LoadedDependencyInfo("CommunityToolkit.Mvvm", new Version(8, 4, 0, 0), @"C:\SmartCon\2025\CommunityToolkit.Mvvm.dll"),
            new LoadedDependencyInfo("System.Text.Json", new Version(9, 0, 0, 0), @"C:\SmartCon\2025\System.Text.Json.dll")
        };

        Assert.Empty(DependencyConflictAnalyzer.Analyze(loaded));
    }

    [Fact]
    public void Analyze_UnknownAssemblies_Ignored()
    {
        var loaded = new[]
        {
            new LoadedDependencyInfo("Newtonsoft.Json", new Version(9, 0, 0, 0), @"C:\Revit\Newtonsoft.Json.dll"),
            new LoadedDependencyInfo("Random.Plugin", new Version(1, 0, 0, 0), @"C:\Addins\Random\Random.Plugin.dll")
        };

        Assert.Empty(DependencyConflictAnalyzer.Analyze(loaded));
    }

    [Fact]
    public void Analyze_DuplicateNames_ReportsOnce()
    {
        var loaded = new[]
        {
            new LoadedDependencyInfo("Microsoft.Bcl.AsyncInterfaces", new Version(6, 0, 0, 0), @"C:\A\Microsoft.Bcl.AsyncInterfaces.dll"),
            new LoadedDependencyInfo("Microsoft.Bcl.AsyncInterfaces", new Version(5, 0, 0, 0), @"C:\B\Microsoft.Bcl.AsyncInterfaces.dll")
        };

        var conflicts = DependencyConflictAnalyzer.Analyze(loaded);
        Assert.Single(conflicts);
    }

    [Fact]
    public void Analyze_NameMatching_CaseInsensitive()
    {
        var loaded = new[]
        {
            new LoadedDependencyInfo("communitytoolkit.mvvm", new Version(8, 2, 0, 0), @"C:\A\communitytoolkit.mvvm.dll")
        };

        Assert.Single(DependencyConflictAnalyzer.Analyze(loaded));
    }

    [Fact]
    public void Analyze_EmptyInput_NoConflict()
    {
        Assert.Empty(DependencyConflictAnalyzer.Analyze(Array.Empty<LoadedDependencyInfo>()));
    }
}
