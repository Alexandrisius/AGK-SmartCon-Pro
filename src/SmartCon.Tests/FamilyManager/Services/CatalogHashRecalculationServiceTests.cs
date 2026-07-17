using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="CatalogHashRecalculationService"/> (Issue #126):
/// the one-shot v1 → v2 hash-recalculation migration.
/// </summary>
public sealed class CatalogHashRecalculationServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FakeMigrationExtractor _extractor = new();
    private readonly FamilyContentHasher _hasher = new();
    private readonly CatalogHashRecalculationService _sut;

    public CatalogHashRecalculationServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new CatalogHashRecalculationService(
            _fixture.GetDatabase(),
            _fixture.GetPathResolver(),
            _hasher,
            _extractor,
            _fixture.GetProvider(),
            _fixture.GetProvider());
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class FakeMigrationExtractor : IFamilyMigrationExtractor
    {
        public Queue<FamilyMigrationExtractResult> Results { get; } = new();
        public List<string> OpenedPaths { get; } = new();
        public FamilySnapshot? DefaultSnapshot { get; set; }

        public Task<FamilyMigrationExtractResult> ExtractLoadableAsync(
            string absolutePath, CancellationToken ct = default)
        {
            OpenedPaths.Add(absolutePath);
            var result = Results.Count > 0
                ? Results.Dequeue()
                : FamilyMigrationExtractResult.Ok(DefaultSnapshot!);
            return Task.FromResult(result);
        }
    }

    private static FamilySnapshot CreateSnapshot(string category = "Pipe Fittings")
    {
        return new FamilySnapshot(
            FamilyName: "IgnoredByV2",
            Category: category,
            Parameters:
            [
                new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)
            ],
            Types:
            [
                new FamilyTypeSnapshot("DN50",
                    [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])
            ],
            Geometry: new GeometryMetrics(1, [new FormMetrics("Extrusion", true, 1250.0, 6, 12, null)]),
            SharedNestedFamilyNames: Array.Empty<string>());
    }

    /// <summary>
    /// Seeds a loadable item WITHOUT a content hash (legacy v1 row):
    /// file record + version + item, all through raw SQL so the hash
    /// columns stay NULL (pending for the migration).
    /// </summary>
    private async Task<(string ItemId, string VersionId, string ManagedRelativePath)> SeedLegacyLoadableAsync(
        string name = "FamA",
        string versionLabel = "v1",
        int revitVersion = 2025,
        string? currentLabel = "v1",
        string familySource = "loadable",
        int? hashFormatVersion = null,
        bool createFileOnDisk = true)
    {
        var itemId = Guid.NewGuid().ToString("N");
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        var ext = familySource == "system" ? ".rvt" : ".rfa";
        var relativePath = $"files/{itemId}/{versionLabel}/{name}{ext}";

        if (createFileOnDisk)
        {
            var absolute = Path.Combine(_fixture.GetDatabaseRoot(), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, "FAKE");
        }

        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, @name, @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@name", name + ext));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, current_version_label,
                                           family_source, hash_format_version, created_at_utc, updated_at_utc)
                VALUES (@id, @name, @norm, @currentLabel, @source, @fmt, @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            cmd.Parameters.Add(new SqliteParameter("@name", name));
            cmd.Parameters.Add(new SqliteParameter("@norm", name.ToLowerInvariant()));
            cmd.Parameters.Add(new SqliteParameter("@currentLabel", (object?)currentLabel ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@source", familySource));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)hashFormatVersion ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, hash_format_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, @label, @revit, @fmt, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", versionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)hashFormatVersion ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
        return (itemId, versionId, relativePath);
    }

    private async Task<(int? Fmt, string? Hash)> ReadVersionHashAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash_format_version, content_hash FROM catalog_versions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "version row must exist");
        var fmt = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
        var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (fmt, hash);
    }

    [Fact]
    public async Task CountPending_CountsLoadableLegacyAndSystem()
    {
        await SeedLegacyLoadableAsync("FamA");
        await SeedLegacyLoadableAsync("FamB");
        await SeedLegacyLoadableAsync("SysCat", familySource: "system");

        var pending = await _sut.CountPendingAsync(2025);

        Assert.Equal(3, pending);
    }

    [Fact]
    public async Task CountPending_SkipsV2AndSkippedAndNewerRevit()
    {
        await SeedLegacyLoadableAsync("Migrated", hashFormatVersion: 2);
        await SeedLegacyLoadableAsync("Skipped", hashFormatVersion: -1);
        await SeedLegacyLoadableAsync("TooNew", revitVersion: 2026);

        var pending = await _sut.CountPendingAsync(2025);

        Assert.Equal(0, pending);
    }

    [Fact]
    public async Task Recalculate_Loadable_UpdatesHashToV2_AndSyncsActiveItem()
    {
        var (itemId, versionId, _) = await SeedLegacyLoadableAsync("FamA");
        _extractor.DefaultSnapshot = CreateSnapshot();

        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Empty(result.MissingFiles);
        Assert.Empty(result.FailedFiles);
        Assert.Single(_extractor.OpenedPaths);

        var expectedHash = _hasher.ComputeForLoadable(CreateSnapshot())!.HexString;
        var (fmt, hash) = await ReadVersionHashAsync(versionId);
        Assert.Equal(2, fmt);
        Assert.Equal(expectedHash, hash);

        // Active version's hash is synced to the denormalized item columns.
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Equal(expectedHash, item?.ContentHash);
        Assert.Equal(2, item?.HashFormatVersion);
    }

    [Fact]
    public async Task Recalculate_MultipleRevitVariants_OneOpen_AllUpdated()
    {
        // Same label stored for two Revit versions (2021 + 2025 files).
        // The migration must open ONE file and apply the hash to BOTH rows.
        var (itemId, v2025, _) = await SeedLegacyLoadableAsync("FamA", revitVersion: 2025);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@fid, @path, 'FamA.rfa', 2021, @t);
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, published_at_utc)
                VALUES (@vid, @itemId, @fid, 'v1', 2021, @t);
                """;
            var fileId = Guid.NewGuid().ToString();
            cmd.Parameters.Add(new SqliteParameter("@fid", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", $"files/{itemId}/v1/FamA.rfa"));
            cmd.Parameters.Add(new SqliteParameter("@vid", Guid.NewGuid().ToString()));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        _extractor.DefaultSnapshot = CreateSnapshot();

        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);

        Assert.Single(_extractor.OpenedPaths);
        Assert.Equal(2, result.UpdatedCount);

        using var conn2 = _fixture.GetDatabase().CreateConnection();
        await conn2.OpenAsync();
        using var check = conn2.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*) FROM catalog_versions
            WHERE catalog_item_id = @itemId AND hash_format_version = 2 AND content_hash IS NOT NULL
            """;
        check.Parameters.Add(new SqliteParameter("@itemId", itemId));
        var count = (long)(await check.ExecuteScalarAsync())!;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Recalculate_SystemRows_ReflaggedWithoutOpeningFiles()
    {
        var (itemId, versionId, _) = await SeedLegacyLoadableAsync(
            "Трубы", familySource: "system", createFileOnDisk: false);

        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, result.SystemRelabeledCount);
        Assert.Empty(_extractor.OpenedPaths);

        var (fmt, _) = await ReadVersionHashAsync(versionId);
        Assert.Equal(2, fmt);
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Equal(2, item?.HashFormatVersion);
    }

    [Fact]
    public async Task Recalculate_MissingFile_ReportedAndLeftPending()
    {
        var (_, versionId, _) = await SeedLegacyLoadableAsync("FamA", createFileOnDisk: false);

        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);

        Assert.Single(result.MissingFiles);
        Assert.Equal("FamA", result.MissingFiles[0].ItemName);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(_extractor.OpenedPaths);

        // NOT marked — the user decides (purge or keep), so it stays pending.
        var (fmt, _) = await ReadVersionHashAsync(versionId);
        Assert.Null(fmt);
    }

    [Fact]
    public async Task Recalculate_ExtractorFailure_MarkedSkippedForever()
    {
        var (_, versionId, _) = await SeedLegacyLoadableAsync("FamA");
        _extractor.Results.Enqueue(FamilyMigrationExtractResult.Fail("corrupt file"));

        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);

        Assert.Single(result.FailedFiles);
        var (fmt, _) = await ReadVersionHashAsync(versionId);
        Assert.Equal(-1, fmt);

        // Permanently skipped rows are excluded from the pending count.
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Recalculate_NewerRevitOnly_LeftPendingAndCounted()
    {
        var (_, versionId, _) = await SeedLegacyLoadableAsync("FamA", revitVersion: 2026);

        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, result.NewerRevitCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(_extractor.OpenedPaths);

        var (fmt, _) = await ReadVersionHashAsync(versionId);
        Assert.Null(fmt);

        // In Revit 2026 the same row becomes processable.
        Assert.Equal(1, await _sut.CountPendingAsync(2026));
    }

    [Fact]
    public async Task Recalculate_CancelledBeforeStart_KeepsRowsPending()
    {
        var (_, versionId, _) = await SeedLegacyLoadableAsync("FamA");
        _extractor.DefaultSnapshot = CreateSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.RecalculateAsync(2025, null, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.UpdatedCount);
        var (fmt, _) = await ReadVersionHashAsync(versionId);
        Assert.Null(fmt);
    }

    [Fact]
    public async Task PurgeMissing_AllVersionsMissing_DeletesWholeItem()
    {
        var (itemId, _, _) = await SeedLegacyLoadableAsync("FamA", createFileOnDisk: false);
        var result = await _sut.RecalculateAsync(2025, null, CancellationToken.None);
        Assert.Single(result.MissingFiles);

        var (deletedItems, deletedVersions) = await _sut.PurgeMissingAsync(result.MissingFiles, CancellationToken.None);

        Assert.Equal(1, deletedItems);
        Assert.Equal(1, deletedVersions);
        Assert.Null(await _fixture.GetProvider().GetItemAsync(itemId));
    }

    [Fact]
    public async Task PurgeMissing_ActiveMissing_SwitchesActiveAndDeletesVersion()
    {
        // v1 is ACTIVE and its file is missing; v2 is intact.
        var (itemId, _, _) = await SeedLegacyLoadableAsync(
            "FamA", versionLabel: "v1", currentLabel: "v1", createFileOnDisk: false);
        var (_, v2Id, _) = await SeedAdditionalVersionAsync(itemId, "FamA", "v2", revitVersion: 2025, hashFormatVersion: 2);

        var missing = new[]
        {
            new HashRecalculationMissingFile(itemId, "FamA", "v1", "FamA.rfa")
        };
        var (deletedItems, deletedVersions) = await _sut.PurgeMissingAsync(missing, CancellationToken.None);

        Assert.Equal(0, deletedItems);
        Assert.Equal(1, deletedVersions);

        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.NotNull(item);
        Assert.Equal("v2", item!.CurrentVersionLabel);

        var versions = await _fixture.GetProvider().GetVersionsAsync(itemId);
        Assert.Single(versions);
        Assert.Equal(v2Id, versions[0].Id);
    }

    private async Task<(string ItemId, string VersionId, string RelativePath)> SeedAdditionalVersionAsync(
        string itemId, string name, string versionLabel, int revitVersion, int? hashFormatVersion)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        var relativePath = $"files/{itemId}/{versionLabel}/{name}.rfa";
        var absolute = Path.Combine(_fixture.GetDatabaseRoot(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "FAKE");

        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, @name, @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@name", name + ".rfa"));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, content_hash, hash_format_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, @label, @revit, @hash, @fmt, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", versionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@hash", (object?)"SEEDEDHASH" ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)hashFormatVersion ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
        return (itemId, versionId, relativePath);
    }
}
