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

        var usage = new ProjectFamilyUsage("u1", itemId, null, null, "project.rvt", @"C:\proj.rvt", 2025, "Load", DateTimeOffset.UtcNow);
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

        await repo.RecordUsageAsync(new ProjectFamilyUsage("u1", itemId, null, null, "p", "p", 2025, "Load", DateTimeOffset.UtcNow));
        await repo.RecordUsageAsync(new ProjectFamilyUsage("u2", itemId, null, null, "p", "p", 2025, "LoadAndPlace", DateTimeOffset.UtcNow));

        var results = await repo.GetUsageForItemAsync(itemId);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetUsageForProjectAsync_ReturnsRecords()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        var projPath = @"C:\Projects\ProjectA.rvt";
        await repo.RecordUsageAsync(new ProjectFamilyUsage("u1", itemId, null, null, "ProjectA.rvt", projPath, 2025, "Load", DateTimeOffset.UtcNow));
        await repo.RecordUsageAsync(new ProjectFamilyUsage("u2", itemId, null, null, "ProjectA.rvt", projPath, 2025, "Load", DateTimeOffset.UtcNow));

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
            await repo.RecordUsageAsync(new ProjectFamilyUsage($"u{i}", itemId, null, null, "p", "p", 2025, "Load", DateTimeOffset.UtcNow));
        }

        var results = await repo.GetUsageForItemAsync(itemId);
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task GetLoadedVersionLabelsAsync_ReturnsLatestVersions()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        var projPath = @"C:\Projects\ProjectA.rvt";
        
        // Record first load with version "v1"
        await repo.RecordUsageAsync(new ProjectFamilyUsage(
            "u1", itemId, "v1-id", "v1", "ProjectA.rvt", projPath, 2025, "Load", 
            DateTimeOffset.UtcNow.AddMinutes(-10)));
        
        // Record second load with version "v2" (more recent)
        await repo.RecordUsageAsync(new ProjectFamilyUsage(
            "u2", itemId, "v2-id", "v2", "ProjectA.rvt", projPath, 2025, "Load", 
            DateTimeOffset.UtcNow));

        var results = await repo.GetLoadedVersionLabelsAsync(projPath, new[] { itemId });

        Assert.Single(results);
        Assert.Equal("v2", results[itemId]);
    }

    [Fact]
    public async Task GetLoadedVersionLabelsAsync_MultipleItems_ReturnsCorrectVersions()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        // Create second catalog item
        var hasher = new Sha256FileHasher();
        var meta = new FileNameOnlyMetadataExtractionService(hasher);
        var importService = new LocalFamilyImportService(
            fixture.GetDatabase(), fixture.GetMigrator(), fixture.GetProvider(),
            fixture.GetPathResolver(), meta);
        var path2 = fixture.CreateFakeRfaFile("SecondFamily.rfa");
        var importResult2 = await importService.ImportFileAsync(new FamilyImportRequest(path2, 2025, null, null, null));
        Assert.True(importResult2.Success);
        var itemId2 = importResult2.CatalogItemId!;

        var projPath = @"C:\Projects\ProjectA.rvt";
        
        await repo.RecordUsageAsync(new ProjectFamilyUsage(
            "u1", itemId, null, "v1", "ProjectA.rvt", projPath, 2025, "Load", DateTimeOffset.UtcNow));
        await repo.RecordUsageAsync(new ProjectFamilyUsage(
            "u2", itemId2, null, "v3", "ProjectA.rvt", projPath, 2025, "Load", DateTimeOffset.UtcNow));

        var results = await repo.GetLoadedVersionLabelsAsync(projPath, new[] { itemId, itemId2 });

        Assert.Equal(2, results.Count);
        Assert.Equal("v1", results[itemId]);
        Assert.Equal("v3", results[itemId2]);
    }

    [Fact]
    public async Task GetLoadedVersionLabelsAsync_EmptyIds_ReturnsEmpty()
    {
        var (fixture, repo, _) = await CreateSeeded();
        using var _ = fixture;

        var results = await repo.GetLoadedVersionLabelsAsync("any-path", Array.Empty<string>());
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetLoadedVersionLabelsAsync_DifferentProjects_DoNotInterfere()
    {
        var (fixture, repo, itemId) = await CreateSeeded();
        using var _ = fixture;

        var projectA = @"C:\Projects\ProjectA.rvt";
        var projectB = @"C:\Projects\ProjectB.rvt";
        
        await repo.RecordUsageAsync(new ProjectFamilyUsage(
            "u1", itemId, null, "v1", "ProjectA.rvt", projectA, 2025, "Load", DateTimeOffset.UtcNow));
        
        // ProjectB has no usage for this item
        var resultsA = await repo.GetLoadedVersionLabelsAsync(projectA, new[] { itemId });
        var resultsB = await repo.GetLoadedVersionLabelsAsync(projectB, new[] { itemId });

        Assert.Single(resultsA);
        Assert.Equal("v1", resultsA[itemId]);
        Assert.Empty(resultsB);
    }
}
