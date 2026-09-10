using Microsoft.Data.Sqlite;
using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class DatabaseCompatibilityServiceTests
{
    private static async Task<TempCatalogFixture> MakeDbWithMinVersionAsync(string? minPluginVersion)
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = minPluginVersion is null
            ? "INSERT INTO database_meta (id, name, created_at_utc, schema_version) VALUES ('db1', 'Test DB', '2026-07-28T00:00:00Z', 2)"
            : "INSERT INTO database_meta (id, name, created_at_utc, schema_version, min_plugin_version) VALUES ('db1', 'Test DB', '2026-07-28T00:00:00Z', 2, @min)";
        if (minPluginVersion is not null)
            cmd.Parameters.Add(new SqliteParameter("@min", minPluginVersion));
        await cmd.ExecuteNonQueryAsync();
        return fixture;
    }

    private static Mock<IUpdateService> MakeUpdateService(string currentVersion)
    {
        var mock = new Mock<IUpdateService>();
        mock.Setup(s => s.GetCurrentVersion()).Returns(currentVersion);
        return mock;
    }

    [Fact]
    public async Task RefreshAsync_MinVersionNewerThanPlugin_Gates()
    {
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.5");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.4").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.True(sut.IsDatabaseNewerThanPlugin);
        Assert.Equal("2.0.1-beta.5", sut.DatabaseMinPluginVersion);
    }

    [Fact]
    public async Task RefreshAsync_MinVersionEqualsPlugin_DoesNotGate()
    {
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.5");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.5").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
    }

    [Fact]
    public async Task RefreshAsync_PluginNewerThanMinVersion_DoesNotGate()
    {
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.5");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.6").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
    }

    [Fact]
    public async Task RefreshAsync_StablePluginAgainstBetaFloor_DoesNotGate()
    {
        // 2.0.1 stable > 2.0.1-beta.5 (ADR-021: stable wins over prerelease).
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.5");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
    }

    [Fact]
    public async Task RefreshAsync_TwoDigitBetaNumber_NumericNotLexicalCompare()
    {
        // beta.10 > beta.9 numerically — a lexical compare would break the
        // gate the day beta numbers reach two digits.
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.10");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.9").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.True(sut.IsDatabaseNewerThanPlugin);
    }

    [Fact]
    public async Task RefreshAsync_NoMarker_DoesNotGate()
    {
        using var fixture = await MakeDbWithMinVersionAsync(null);
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.4").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
        Assert.Null(sut.DatabaseMinPluginVersion);
    }

    [Fact]
    public async Task RefreshAsync_UnparseableMarker_DoesNotGate()
    {
        // Fail-open: a corrupt marker must never lock users out.
        using var fixture = await MakeDbWithMinVersionAsync("not-a-version");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.4").Object, DatabaseCompatibilityService.OverrideMode.Force);

        await sut.RefreshAsync();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
    }

    [Fact]
    public async Task Reset_ClearsGateState()
    {
        using var fixture = await MakeDbWithMinVersionAsync("9.9.9");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.4").Object, DatabaseCompatibilityService.OverrideMode.Force);
        await sut.RefreshAsync();
        Assert.True(sut.IsDatabaseNewerThanPlugin);

        sut.Reset();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
        Assert.Null(sut.DatabaseMinPluginVersion);
    }

    [Fact]
    public async Task RefreshAsync_GatedMarker_OverrideDisable_SuppressesGate()
    {
        // ADR-058 §5: the escape hatch — even a positively gated marker must
        // not block when the override disables the gate (dev/support scenario).
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.5");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.4").Object, DatabaseCompatibilityService.OverrideMode.Disable);

        await sut.RefreshAsync();

        Assert.False(sut.IsDatabaseNewerThanPlugin);
        Assert.Equal("2.0.1-beta.5", sut.DatabaseMinPluginVersion);
    }

    [Fact]
    public async Task RefreshAsync_GatedMarker_OverrideDefault_InDebugBuild_SuppressesGate()
    {
        // ADR-058 §5: DEBUG builds keep the gate OFF by default — a local
        // dev build (stable Version.txt version, older than any beta floor)
        // must not lock the developer-owner out of his databases. In RELEASE
        // the same Default mode enables the gate; this test asserts the
        // DEBUG-branch behavior and is compiled accordingly.
        using var fixture = await MakeDbWithMinVersionAsync("2.0.1-beta.5");
        var sut = new DatabaseCompatibilityService(fixture.GetDatabase(), MakeUpdateService("2.0.1-beta.4").Object, DatabaseCompatibilityService.OverrideMode.Default);

        await sut.RefreshAsync();

#if DEBUG
        Assert.False(sut.IsDatabaseNewerThanPlugin);
#else
        Assert.True(sut.IsDatabaseNewerThanPlugin);
#endif
    }
}
