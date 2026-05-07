using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyDataImportRunRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyDataImportRunRepository _repo;

    public LocalFamilyDataImportRunRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();
        _repo = new LocalFamilyDataImportRunRepository(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task SeedCatalogItemAsync(string id)
    {
        using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, 'Active', @t, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", id));
        cmd.Parameters.Add(new SqliteParameter("@norm", id.ToLowerInvariant()));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private static FamilyDataImportRun CreateRun(
        string catalogItemId,
        DateTimeOffset startedAtUtc,
        FamilyDataImportStatus status = FamilyDataImportStatus.Succeeded,
        int typesCount = 0,
        string? errorMessage = null)
    {
        return new FamilyDataImportRun(
            Guid.NewGuid().ToString(),
            catalogItemId,
            null,
            null,
            null,
            2025,
            status,
            typesCount,
            startedAtUtc,
            null,
            errorMessage);
    }

    [Fact]
    public async Task CreateRunAsync_SavesRun_ReturnsInGetLatest()
    {
        await SeedCatalogItemAsync("item1");
        var startedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var run = CreateRun("item1", startedAt);

        await _repo.CreateRunAsync(run);

        var latest = await _repo.GetLatestRunAsync("item1");
        Assert.NotNull(latest);
        Assert.Equal(run.Id, latest.Id);
        Assert.Equal("item1", latest.CatalogItemId);
        Assert.Equal(FamilyDataImportStatus.Succeeded, latest.Status);
    }

    [Fact]
    public async Task GetLatestRunAsync_NoRuns_ReturnsNull()
    {
        var result = await _repo.GetLatestRunAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestRunAsync_MultipleRuns_ReturnsMostRecent()
    {
        await SeedCatalogItemAsync("item1");
        var earlier = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 1, 2, 14, 0, 0, TimeSpan.Zero);

        var run1 = CreateRun("item1", earlier);
        var run2 = CreateRun("item1", later);

        await _repo.CreateRunAsync(run1);
        await _repo.CreateRunAsync(run2);

        var latest = await _repo.GetLatestRunAsync("item1");
        Assert.NotNull(latest);
        Assert.Equal(run2.Id, latest.Id);
    }

    [Fact]
    public async Task GetRunsForItemAsync_ReturnsAllOrdered()
    {
        await SeedCatalogItemAsync("item1");
        var t1 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 1, 3, 10, 0, 0, TimeSpan.Zero);

        var run1 = CreateRun("item1", t1);
        var run2 = CreateRun("item1", t2);
        var run3 = CreateRun("item1", t3);

        await _repo.CreateRunAsync(run1);
        await _repo.CreateRunAsync(run2);
        await _repo.CreateRunAsync(run3);

        var runs = await _repo.GetRunsForItemAsync("item1");
        Assert.Equal(3, runs.Count);
        Assert.Equal(run3.Id, runs[0].Id);
        Assert.Equal(run2.Id, runs[1].Id);
        Assert.Equal(run1.Id, runs[2].Id);
    }

    [Fact]
    public async Task GetRunsForItemAsync_DifferentItem_ReturnsEmpty()
    {
        await SeedCatalogItemAsync("item1");
        var run = CreateRun("item1", DateTimeOffset.UtcNow);
        await _repo.CreateRunAsync(run);

        var runs = await _repo.GetRunsForItemAsync("item2");
        Assert.Empty(runs);
    }

    [Fact]
    public async Task UpdateRunAsync_UpdatesStatusAndTypesCount()
    {
        await SeedCatalogItemAsync("item1");
        var run = CreateRun("item1", DateTimeOffset.UtcNow, FamilyDataImportStatus.Succeeded, 0);
        await _repo.CreateRunAsync(run);

        var completedAt = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero);
        var updated = await _repo.UpdateRunAsync(run.Id, FamilyDataImportStatus.Partial, 5, completedAt, null);

        Assert.Equal(FamilyDataImportStatus.Partial, updated.Status);
        Assert.Equal(5, updated.TypesCount);
    }

    [Fact]
    public async Task UpdateRunAsync_SetsCompletedAtUtc()
    {
        await SeedCatalogItemAsync("item1");
        var run = CreateRun("item1", DateTimeOffset.UtcNow);
        await _repo.CreateRunAsync(run);

        var completedAt = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero);
        var updated = await _repo.UpdateRunAsync(run.Id, FamilyDataImportStatus.Succeeded, 3, completedAt, null);

        Assert.NotNull(updated.CompletedAtUtc);
        Assert.Equal(completedAt, updated.CompletedAtUtc!.Value);
    }

    [Fact]
    public async Task UpdateRunAsync_SetsErrorMessage()
    {
        await SeedCatalogItemAsync("item1");
        var run = CreateRun("item1", DateTimeOffset.UtcNow);
        await _repo.CreateRunAsync(run);

        var completedAt = DateTimeOffset.UtcNow;
        var updated = await _repo.UpdateRunAsync(run.Id, FamilyDataImportStatus.Failed, 0, completedAt, "File not found");

        Assert.Equal("File not found", updated.ErrorMessage);
    }
}
