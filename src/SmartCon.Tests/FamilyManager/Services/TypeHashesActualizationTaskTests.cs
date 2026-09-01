using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="TypeHashesActualizationTask"/> (Issue #249,
/// Phase 2): detection of versions lacking <c>family_type_hashes</c>
/// rows and the backfill write for ALL Revit variants of a group.
/// </summary>
public sealed class TypeHashesActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FamilyContentHasher _hasher = new();
    private readonly TypeHashesActualizationTask _sut;

    public TypeHashesActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new TypeHashesActualizationTask(_fixture.GetDatabase(), _hasher);
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<int> CountHashRowsAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM family_type_hashes WHERE catalog_version_id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<(string Key, string Name, string Hash)>> ReadHashRowsAsync(string versionId)
    {
        var rows = new List<(string, string, string)>();
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type_identity_key, type_name, type_hash FROM family_type_hashes WHERE catalog_version_id = @id ORDER BY type_identity_key";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    [Fact]
    public async Task Detection_TypesPresentNoHashes_Pending()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamP");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Detection_HashesPresent_NotPending()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamH");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        await SeedHashRowAsync(versionId, "DN50", "DN50", "ABC");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Detection_TypelessFamily_NotPending()
    {
        // No family_types rows — a typeless family legitimately has zero
        // per-type hashes and must not stay pending forever.
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamT");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Detection_TypelessLegacyDefaultRow_NotPending()
    {
        // Pre-FHV8 imports wrote a phantom '<default>' family_types row
        // for typeless families. The hash engine has nothing to backfill
        // for them — they must not be re-detected on every run forever
        // (owner repro 2026-08-30: the optional task kept "updating" the
        // same family, banner never cleared).
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamD");
        await SeedFamilyTypeRowAsync(itemId, versionId, fileId, FamilyTypeSnapshot.DefaultTypeName);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_TypelessLegacyDefaultRow_ConvergedNoWrite()
    {
        // Defensive: even if ApplyAsync reaches a phantom-only family,
        // the empty hash set is the CONVERGED state — no warn-drift, no
        // rows written, family_types rows untouched.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamD2");
        await SeedFamilyTypeRowAsync(itemId, versionId, fileId, FamilyTypeSnapshot.DefaultTypeName);

        var ctx = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamD2", "v1", true,
                new[] { new ActualizationVariant(versionId, fileId, 2025, "x", "FamD2.rfa") }),
            new ActualizationVariant(versionId, fileId, 2025, "x", "FamD2.rfa"),
            "C:\\root\\x",
            CreateTypelessSnapshot(),
            null);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        Assert.Equal(0, await CountHashRowsAsync(versionId));
        Assert.Equal(1, await CountFamilyTypeRowsAsync(versionId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_EmptySetWithNamedRows_GenuineDriftStaysPending()
    {
        // Named family_types rows + empty extraction = genuine drift:
        // nothing written, the group stays detected so the drift remains
        // visible until the family is re-imported.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamG");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        var ctx = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamG", "v1", true,
                new[] { new ActualizationVariant(versionId, fileId, 2025, "x", "FamG.rfa") }),
            new ActualizationVariant(versionId, fileId, 2025, "x", "FamG.rfa"),
            "C:\\root\\x",
            CreateTypelessSnapshot(),
            null);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        Assert.Equal(0, await CountHashRowsAsync(versionId));
        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Detection_TerminalHashSentinel_NotPending()
    {
        // -1/-2 (missing/unreadable file) — re-opening is known to fail
        // (ADR-050 §2), the group must not be detected.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamS", hashFormatVersion: -1);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_Loadable_WritesRowsForAllVariants()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var variant2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v1", 2024);

        var snapshot = CatalogSeedHelper.CreateSnapshot();
        var ctx = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamA", "v1", true,
                new[]
                {
                    new ActualizationVariant(versionId, fileId, 2025, "x", "FamA.rfa"),
                    new ActualizationVariant(variant2, "f2", 2024, "y", "FamA.rfa"),
                }),
            new ActualizationVariant(versionId, fileId, 2025, "x", "FamA.rfa"),
            "C:\\root\\x",
            snapshot,
            null);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var expectedHash = _hasher.ComputePerTypeHashesForLoadable(snapshot)!["DN50"];
        foreach (var vid in new[] { versionId, variant2 })
        {
            var rows = await ReadHashRowsAsync(vid);
            var row = Assert.Single(rows);
            Assert.Equal("DN50", row.Key);
            Assert.Equal("DN50", row.Name);
            Assert.Equal(expectedHash, row.Hash);
        }

        // Detection clears after the write.
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_System_WritesIdentityKeyedRows()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Трубы", familySource: "system");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        var systemSnapshot = new SystemFamilySnapshot(
            "Трубы",
            -2001160,
            new[]
            {
                new SystemTypeSnapshot(
                    "Стандартный",
                    new[] { new SystemParameterValue("Диаметр", "Double", true, null, 0.05, null) },
                    FamilyName: "Pipe Type",
                    FamilyKey: "Pipe"),
            });
        var ctx = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "Трубы", "v1", true,
                new[] { new ActualizationVariant(versionId, fileId, 2025, "x", "Трубы.rvt") }),
            new ActualizationVariant(versionId, fileId, 2025, "x", "Трубы.rvt"),
            "C:\\root\\x",
            CatalogSeedHelper.CreateSnapshot(),
            null,
            SystemSnapshot: systemSnapshot);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var rows = await ReadHashRowsAsync(versionId);
        var row = Assert.Single(rows);
        Assert.Equal("PIPE|СТАНДАРТНЫЙ", row.Key);
        Assert.Equal("Стандартный", row.Name);
        var expected = _hasher.ComputePerTypeHashesForSystem(systemSnapshot)!;
        Assert.Equal(expected[0].HashHex, row.Hash);
    }

    [Fact]
    public async Task Apply_RewriteReplacesPreviousRows()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamR");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        await SeedHashRowAsync(versionId, "STALE", "Stale", "DEADBEEF");

        var ctx = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamR", "v1", true,
                new[] { new ActualizationVariant(versionId, fileId, 2025, "x", "FamR.rfa") }),
            new ActualizationVariant(versionId, fileId, 2025, "x", "FamR.rfa"),
            "C:\\root\\x",
            CatalogSeedHelper.CreateSnapshot(),
            null);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var rows = await ReadHashRowsAsync(versionId);
        var row = Assert.Single(rows);
        Assert.Equal("DN50", row.Key);
    }

    private async Task SeedHashRowAsync(string versionId, string key, string name, string hash)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_type_hashes (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc)
            VALUES (@vid, @key, @name, @hash, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@vid", versionId));
        cmd.Parameters.Add(new SqliteParameter("@key", key));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@hash", hash));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedFamilyTypeRowAsync(string itemId, string versionId, string fileId, string typeName)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
            VALUES (@id, @itemId, @typeName, 0, @versionId, @fileId)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@typeName", typeName));
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountFamilyTypeRowsAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM family_types WHERE version_id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private static FamilySnapshot CreateTypelessSnapshot()
    {
        return new FamilySnapshot(
            FamilyName: "FamTypeless",
            Category: "Pipe Fittings",
            Parameters: [],
            Types: [],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: []);
    }
}
