using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalProjectFamilyUsageRepositoryTests
{
    private static async Task<(TempCatalogFixture Fixture, LocalProjectFamilyUsageRepository Repo)> CreateAndMigrate()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var repo = new LocalProjectFamilyUsageRepository(fixture.GetDatabase());
        return (fixture, repo);
    }

    private static ProjectFamilyUsage CreateUsage(
        string id = "u1",
        string catalogItemId = "item1",
        string versionId = "ver1",
        string projectFingerprint = "proj1",
        string action = "Load")
    {
        return new ProjectFamilyUsage(id, catalogItemId, versionId, "local", projectFingerprint, action, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RecordUsage_StoresRecord()
    {
        var (fixture, repo) = await CreateAndMigrate();
        using var _ = fixture;

        await repo.RecordUsageAsync(CreateUsage());

        var results = await repo.GetUsageForItemAsync("item1");
        Assert.Single(results);
        Assert.Equal("Load", results[0].Action);
    }

    [Fact]
    public async Task GetUsageForItemAsync_ReturnsRecords()
    {
        var (fixture, repo) = await CreateAndMigrate();
        using var _ = fixture;

        await repo.RecordUsageAsync(CreateUsage("u1", "item1", "ver1"));
        await repo.RecordUsageAsync(CreateUsage("u2", "item1", "ver2"));
        await repo.RecordUsageAsync(CreateUsage("u3", "item2", "ver3"));

        var results = await repo.GetUsageForItemAsync("item1");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetUsageForProjectAsync_ReturnsRecords()
    {
        var (fixture, repo) = await CreateAndMigrate();
        using var _ = fixture;

        await repo.RecordUsageAsync(CreateUsage("u1", "item1", "ver1", "projA"));
        await repo.RecordUsageAsync(CreateUsage("u2", "item2", "ver2", "projA"));
        await repo.RecordUsageAsync(CreateUsage("u3", "item3", "ver3", "projB"));

        var results = await repo.GetUsageForProjectAsync("projA");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetUsageForItemAsync_NoRecords_ReturnsEmpty()
    {
        var (fixture, repo) = await CreateAndMigrate();
        using var _ = fixture;

        var results = await repo.GetUsageForItemAsync("nonexistent");
        Assert.Empty(results);
    }

    [Fact]
    public async Task RecordUsage_MultipleRecords_AllStored()
    {
        var (fixture, repo) = await CreateAndMigrate();
        using var _ = fixture;

        for (int i = 0; i < 5; i++)
        {
            await repo.RecordUsageAsync(CreateUsage($"u{i}", "batchItem", "ver1"));
        }

        var results = await repo.GetUsageForItemAsync("batchItem");
        Assert.Equal(5, results.Count);
    }
}
