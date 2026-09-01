using Microsoft.Data.Sqlite;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Issue #249, Phase 4: <see cref="LocalContentHashAnalyticsRepository"/>
/// — the pending-vs-typeless distinction of
/// <see cref="LocalContentHashAnalyticsRepository.GetTypeHashesAsync"/>
/// (a fake all-added diff must never be shown for a pending backfill).
/// </summary>
public sealed class LocalContentHashAnalyticsRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalContentHashAnalyticsRepository _sut;

    public LocalContentHashAnalyticsRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new LocalContentHashAnalyticsRepository(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetTypeHashes_PendingBackfill_ReturnsNull()
    {
        // family_types rows exist, no hash rows — the backfill is pending.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamP");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        var result = await _sut.GetTypeHashesAsync(itemId, "v1", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTypeHashes_TypelessFamily_ReturnsEmptyNotNull()
    {
        // No family_types rows — a typeless family legitimately has zero
        // per-type hashes. Empty list (NOT null) — the diff can trust it.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamT");

        var result = await _sut.GetTypeHashesAsync(itemId, "v1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public async Task GetTypeHashes_RowsPresent_ReturnsRows()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamR");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO family_type_hashes (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc)
                VALUES (@vid, 'DN50', 'DN50', 'ABC', @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await _sut.GetTypeHashesAsync(itemId, "v1", CancellationToken.None);

        var row = Assert.Single(result!);
        Assert.Equal("DN50", row.TypeIdentityKey);
        Assert.Equal("ABC", row.HashHex);
    }

    [Fact]
    public async Task GetSectionHashes_Missing_ReturnsNull_Present_Parses()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamS");

        Assert.Null(await _sut.GetSectionHashesAsync(itemId, "v1", CancellationToken.None));

        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE catalog_versions SET section_hashes = @h WHERE id = @vid";
            cmd.Parameters.Add(new SqliteParameter("@h", "{\"META\":\"ABC\"}"));
            cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
            await cmd.ExecuteNonQueryAsync();
        }

        var map = await _sut.GetSectionHashesAsync(itemId, "v1", CancellationToken.None);
        Assert.NotNull(map);
        Assert.Equal("ABC", map!["META"]);
    }
}
