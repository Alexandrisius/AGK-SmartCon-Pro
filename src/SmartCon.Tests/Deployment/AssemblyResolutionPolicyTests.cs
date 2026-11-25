using SmartCon.Core.Deployment;
using Xunit;

namespace SmartCon.Tests.Deployment;

/// <summary>
/// Issue #527: on net48 Revit ships its own System.Memory (4.0.1.1) and exact-version
/// references bind to it silently. Loading our newer copy side-by-side split
/// ReadOnlySpan&lt;T&gt; identity and every SQLite prepare died with MissingMethodException.
/// The policy must therefore keep a single physical copy and give the host copy priority.
/// </summary>
public sealed class AssemblyResolutionPolicyTests
{
    [Theory]
    [InlineData("System.Memory")]
    [InlineData("system.memory")]
    [InlineData("SYSTEM.MEMORY")]
    public void TypeIdentityCritical_MatchesSystemMemory(string name)
    {
        Assert.True(AssemblyResolutionPolicy.IsTypeIdentityCritical(name));
    }

    [Theory]
    [InlineData("System.Buffers")]
    [InlineData("System.Runtime.CompilerServices.Unsafe")]
    [InlineData("SQLitePCLRaw.core")]
    [InlineData("Microsoft.Data.Sqlite")]
    [InlineData("")]
    [InlineData(null)]
    public void TypeIdentityCritical_RejectsEverythingElse(string? name)
    {
        Assert.False(AssemblyResolutionPolicy.IsTypeIdentityCritical(name));
    }

    [Fact]
    public void HostCopyWins_OverLoadedAndPlugin()
    {
        var source = AssemblyResolutionPolicy.ChooseIdentityCriticalSource(
            hostCopyExists: true,
            loadedCopyExists: true,
            pluginCopyExists: true);

        Assert.Equal(AssemblyResolutionSource.Host, source);
    }

    [Fact]
    public void LoadedCopyWins_WhenHostHasNoCopy()
    {
        var source = AssemblyResolutionPolicy.ChooseIdentityCriticalSource(
            hostCopyExists: false,
            loadedCopyExists: true,
            pluginCopyExists: true);

        Assert.Equal(AssemblyResolutionSource.Loaded, source);
    }

    [Fact]
    public void PluginCopyIsTheLastResort()
    {
        var source = AssemblyResolutionPolicy.ChooseIdentityCriticalSource(
            hostCopyExists: false,
            loadedCopyExists: false,
            pluginCopyExists: true);

        Assert.Equal(AssemblyResolutionSource.Plugin, source);
    }

    [Fact]
    public void NothingAvailable_YieldsNone()
    {
        var source = AssemblyResolutionPolicy.ChooseIdentityCriticalSource(
            hostCopyExists: false,
            loadedCopyExists: false,
            pluginCopyExists: false);

        Assert.Equal(AssemblyResolutionSource.None, source);
    }
}
