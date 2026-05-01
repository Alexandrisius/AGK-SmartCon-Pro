using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalProjectFamilyUsageRepositoryTests
{
    private static async Task<(TempCatalogFixture Fixture, LocalProjectFamilyUsageRepository Repo, string CatalogItemId)> CreateSeeded()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        var hasher = new Sha256FileHasher();
        var meta = new FileNameOnlyMetadataExtractionService(hasher);
        var importService = new LocalFamilyImportService(
            fixture.GetDatabase(), fixture.GetMigrator(), fixture.GetProvider(),
            fixture.GetPathResolver(), meta);

        var path = fixture.CreateFakeRfaFile("UsageFamily.rfa");
        var importResult = await importService.ImportFileAsync(new FamilyImportRequest(path, 2025, null, null, null));
        Assert.True(importResult.Success);

        var repo = new LocalProjectFamilyUsageRepository(fixture.GetDatabase());
        return (fixture, repo, importResult.CatalogItemId!);
    }

    [Fact]
    public async Task RecordUsage_StoresRecord()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        var usage = new ProjectFamilyUsage("u1", itemId, null, "project.rvt", @"C:\proj.rvt", 2025, "Load", DateTimeOffset.UtcNow);
        await repo.RecordUsageAsync(usage);

        var results = await repo.GetUsageForItemAsync(itemId);
        Assert.Single(results);
        Assert.Equal("Load", results[0].Action);
    }

    [Fact]
    public async Task GetUsageForItemAsync_ReturnsRecords()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        await repo.RecordUsageAsync(new ProjectFamilyUsage("u1", itemId, null, "p", "p", 2025, "Load", DateTimeOffset.UtcNow));
        await repo.RecordUsageAsync(new ProjectFamilyUsage("u2", itemId, null, "p", "p", 2025, "LoadAndPlace", DateTimeOffset.UtcNow));

        var results = await repo.GetUsageForItemAsync(itemId);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetUsageForProjectAsync_ReturnsRecords()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        var projPath = @"C:\Projects\ProjectA.rvt";
        await repo.RecordUsageAsync(new ProjectFamilyUsage("u1", itemId, null, "ProjectA.rvt", projPath, 2025, "Load", DateTimeOffset.UtcNow));
        await repo.RecordUsageAsync(new ProjectFamilyUsage("u2", itemId, null, "ProjectA.rvt", projPath, 2025, "Load", DateTimeOffset.UtcNow));

        var results = await repo.GetUsageForProjectAsync(projPath);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetUsageForItemAsync_NoRecords_ReturnsEmpty()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        var results = await repo.GetUsageForItemAsync("nonexistent");
        Assert.Empty(results);
    }

    [Fact]
    public async Task RecordUsage_MultipleRecords_AllStored()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        for (int i = 0; i < 5; i++)
        {
            await repo.RecordUsageAsync(new ProjectFamilyUsage($"u{i}", itemId, null, "p", "p", 2025, "Load", DateTimeOffset.UtcNow));
        }

        var results = await repo.GetUsageForItemAsync(itemId);
        Assert.Equal(5, results.Count);
    }
}
