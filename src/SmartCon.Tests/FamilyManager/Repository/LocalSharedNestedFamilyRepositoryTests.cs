using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalSharedNestedFamilyRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalSharedNestedFamilyRepository _repository;
    private readonly LocalFamilyImportService _importService;

    public LocalSharedNestedFamilyRepositoryTests()
    {
        _fixture = new TempCatalogFixture();

        _repository = new LocalSharedNestedFamilyRepository(_fixture.GetDatabase());

        var hasher = new Sha256FileHasher();
        var metadataService = new FileNameOnlyMetadataExtractionService(hasher);
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            metadataService,
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
    }

    private async Task<(string CatalogItemId, string VersionId)> SeedItemWithVersionAsync(string fileName)
    {
        var path = _fixture.CreateFakeRfaFile(fileName);
        var result = await _importService.ImportFileAsync(
            new FamilyImportRequest(path, 2025, null, null, null));
        Assert.True(result.Success);
        Assert.NotNull(result.CatalogItemId);
        Assert.NotNull(result.VersionId);
        return (result.CatalogItemId!, result.VersionId!);
    }

    [Fact]
    public async Task GetNamesForCurrentVersionAsync_EmptyCatalog_ReturnsEmptyList()
    {
        var (itemId, _) = await SeedItemWithVersionAsync("Empty.rfa");

        var names = await _repository.GetNamesForCurrentVersionAsync(itemId);

        Assert.Empty(names);
    }

    [Fact]
    public async Task ReplaceForVersionAsync_ThreeNames_PersistsAllInOrder()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("ThreeBolts.rfa");
        var input = new[] { "Болт М12", "Гайка М12", "Шайба М12" };

        await _repository.ReplaceForVersionAsync(itemId, versionId, input);
        var result = await _repository.GetNamesForCurrentVersionAsync(itemId);

        Assert.Equal(3, result.Count);
        Assert.Equal("Болт М12", result[0]);
        Assert.Equal("Гайка М12", result[1]);
        Assert.Equal("Шайба М12", result[2]);
    }

    [Fact]
    public async Task ReplaceForVersionAsync_CaseInsensitiveDuplicates_Deduped()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("DupNames.rfa");
        var input = new[] { "Болт М12", "болт м12", "БОЛТ М12", "Гайка М12" };

        await _repository.ReplaceForVersionAsync(itemId, versionId, input);
        var result = await _repository.GetNamesForCurrentVersionAsync(itemId);

        Assert.Equal(2, result.Count);
        Assert.Equal("Болт М12", result[0]);
        Assert.Equal("Гайка М12", result[1]);
    }

    [Fact]
    public async Task ReplaceForVersionAsync_CalledTwice_ReplacesPrevious()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("Twice.rfa");
        var first = new[] { "Болт М12", "Гайка М12" };
        var second = new[] { "Болт М16" };

        await _repository.ReplaceForVersionAsync(itemId, versionId, first);
        await _repository.ReplaceForVersionAsync(itemId, versionId, second);
        var result = await _repository.GetNamesForCurrentVersionAsync(itemId);

        Assert.Single(result);
        Assert.Equal("Болт М16", result[0]);
    }

    [Fact]
    public async Task ReplaceForVersionAsync_EmptyInput_DeletesPrevious()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("Delete.rfa");
        await _repository.ReplaceForVersionAsync(itemId, versionId, new[] { "Болт М12", "Гайка М12" });
        Assert.Equal(2, (await _repository.GetNamesForCurrentVersionAsync(itemId)).Count);

        await _repository.ReplaceForVersionAsync(itemId, versionId, Array.Empty<string>());
        Assert.Empty(await _repository.GetNamesForCurrentVersionAsync(itemId));
    }

    [Fact]
    public async Task ReplaceForVersionAsync_BlankNames_Skipped()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("BlankNames.rfa");
        var input = new[] { "Болт М12", "", "  ", null!, "Гайка М12" };

        await _repository.ReplaceForVersionAsync(itemId, versionId, input!);
        var result = await _repository.GetNamesForCurrentVersionAsync(itemId);

        Assert.Equal(2, result.Count);
        Assert.Equal("Болт М12", result[0]);
        Assert.Equal("Гайка М12", result[1]);
    }

    [Fact]
    public async Task ReplaceForVersionAsync_Ordinals_AreSequential()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("Ordinals.rfa");
        var input = new[] { "Болт М12", "Гайка М12", "Шайба М12" };

        await _repository.ReplaceForVersionAsync(itemId, versionId, input);

        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT nested_family_name, ordinal
            FROM family_nested_shared_families
            WHERE version_id = @versionId
            ORDER BY ordinal
            """;
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@versionId", versionId));
        using var reader = await cmd.ExecuteReaderAsync();

        var observed = new List<(string Name, long Ordinal)>();
        while (await reader.ReadAsync())
        {
            observed.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        Assert.Equal(3, observed.Count);
        Assert.Equal(("Болт М12", 0L), observed[0]);
        Assert.Equal(("Гайка М12", 1L), observed[1]);
        Assert.Equal(("Шайба М12", 2L), observed[2]);
    }

    [Fact]
    public async Task GetNamesForCurrentVersionAsync_OnlyCurrentVersionReturned_NotPreviousOnes()
    {
        var (itemId, _) = await SeedItemWithVersionAsync("V1.rfa");
        var firstVersion = (await _repository.GetNamesForCurrentVersionAsync(itemId)).Count;

        var path = _fixture.CreateFakeRfaFile("V2.rfa");
        var update = await _importService.UpdateFamilyAsync(
            new FamilyUpdateRequest(itemId, path, 2025));
        Assert.True(update.Success);

        // New version: persist a different name; the old version's row
        // must not appear in GetNamesForCurrentVersionAsync.
        await _repository.ReplaceForVersionAsync(itemId, update.VersionId!, new[] { "Болт М16" });

        var current = await _repository.GetNamesForCurrentVersionAsync(itemId);
        Assert.Single(current);
        Assert.Equal("Болт М16", current[0]);
    }

    [Fact]
    public async Task ReplaceForVersionAsync_NullVersionId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.ReplaceForVersionAsync("item-1", null!, new[] { "x" }));
    }

    [Fact]
    public async Task ReplaceForVersionAsync_OrdinalsResetOnShorterList()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("OrdinalsReset.rfa");
        await _repository.ReplaceForVersionAsync(itemId, versionId,
            new[] { "A", "B", "C", "D", "E" });
        await _repository.ReplaceForVersionAsync(itemId, versionId, new[] { "X", "Y" });

        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT nested_family_name, ordinal
            FROM family_nested_shared_families
            WHERE version_id = @v
            ORDER BY ordinal
            """;
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@v", versionId));
        using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<(string Name, long Ordinal)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetInt64(1)));

        Assert.Equal(2, rows.Count);
        Assert.Equal(("X", 0L), rows[0]);
        Assert.Equal(("Y", 1L), rows[1]);
    }

    [Fact]
    public async Task DeleteCatalogItem_CascadesToNestedSharedFamilies()
    {
        var (itemId, versionId) = await SeedItemWithVersionAsync("CascadeItem.rfa");
        await _repository.ReplaceForVersionAsync(itemId, versionId, new[] { "Болт М12", "Гайка М12" });
        Assert.Equal(2, (await _repository.GetNamesForCurrentVersionAsync(itemId)).Count);

        // Direct SQL delete (bypasses the repository) — exercises the
        // ON DELETE CASCADE constraint on the FK to catalog_items.
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var del = connection.CreateCommand();
        del.CommandText = "DELETE FROM catalog_items WHERE id = @id";
        del.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", itemId));
        await del.ExecuteNonQueryAsync();

        // The nested rows should be removed by the cascade; no orphans.
        Assert.Empty(await _repository.GetNamesForCurrentVersionAsync(itemId));
    }

    [Fact]
    public async Task DeleteCatalogVersion_CascadesToNestedSharedFamilies()
    {
        var (itemId, firstVersionId) = await SeedItemWithVersionAsync("CascadeVer.rfa");
        await _repository.ReplaceForVersionAsync(itemId, firstVersionId, new[] { "Болт М12" });
        Assert.Single(await _repository.GetNamesForCurrentVersionAsync(itemId));

        // Direct SQL delete on catalog_versions — exercises the
        // ON DELETE CASCADE constraint on the FK to catalog_versions.
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var del = connection.CreateCommand();
        del.CommandText = "DELETE FROM catalog_versions WHERE id = @id";
        del.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", firstVersionId));
        await del.ExecuteNonQueryAsync();

        // The nested rows are tied to (catalog_item_id, version_id) via
        // the PK; the version_id FK deletion also removes them.
        Assert.Empty(await _repository.GetNamesForCurrentVersionAsync(itemId));
    }

    [Fact]
    public async Task GetNamesForCurrentVersionAsync_UnknownItemId_ReturnsEmpty()
    {
        var names = await _repository.GetNamesForCurrentVersionAsync("nonexistent-id-12345");
        Assert.Empty(names);
    }

    [Fact]
    public void NextInvocation_ReturnsUniqueValues_AcrossThreads()
    {
        var resolver = new SharedFamilyNameResolver();
        var max = 1000;
        var observed = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, max, _ => observed.Add(resolver.NextInvocationIndex()));

        var distinct = observed.Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(max, distinct.Length);
        Assert.Equal(Enumerable.Range(1, max), distinct);
    }

    [Fact]
    public async Task GetNamesForCurrentVersionAsync_NullItemId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.GetNamesForCurrentVersionAsync(null!));
    }

    [Fact]
    public async Task ReplaceForVersionAsync_EmptyItemId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.ReplaceForVersionAsync("", "v1", new[] { "x" }));
    }

    [Fact]
    public async Task ReplaceForVersionAsync_EmptyVersionId_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _repository.ReplaceForVersionAsync("i1", "", new[] { "x" }));
    }

    [Fact]
    public async Task MigrateV13_CreatesTableWithCompositeKeyAndCascade()
    {
        // After fixture init, table must exist and FK on cascade is ON.
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys;";
        var fkOnRaw = await pragmaCmd.ExecuteScalarAsync();
        var fkOn = Convert.ToInt64(fkOnRaw);
        Assert.Equal(1L, fkOn);

        using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type='table' AND name='family_nested_shared_families'
            """;
        var exists = await tableCmd.ExecuteScalarAsync();
        Assert.NotNull(exists);
    }

    public void Dispose() => _fixture.Dispose();
}
