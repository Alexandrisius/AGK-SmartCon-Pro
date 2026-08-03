using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Tests for the V17 migration (ADR-041): adds FOREIGN KEY
/// (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
/// on family_types and extracted_attribute_values. A fresh catalog database
/// ends up at schema version 21.
/// </summary>
public sealed class LocalCatalogV17MigrationTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;

    public LocalCatalogV17MigrationTests()
    {
        _fixture = new TempCatalogFixture();
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Migrate_FreshDb_SetsSchemaVersion27()
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version'";
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal("27", result?.ToString());
    }

    [Fact]
    public async Task Migrate_FreshDb_FamilyTypesHasVersionIdFk()
    {
        // Walk sqlite_master to find the CREATE TABLE text for family_types
        // and verify it includes the FK constraint on version_id.
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='family_types'";
        var sql = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(sql);
        Assert.Contains("FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE", sql);
    }

    [Fact]
    public async Task Migrate_FreshDb_ExtractedAttributeValuesHasVersionIdFk()
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='extracted_attribute_values'";
        var sql = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(sql);
        Assert.Contains("FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE", sql);
    }

    [Fact]
    public async Task Migrate_FreshDb_HasCatalogVersionsItemLabelIndex()
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_catalog_versions_item_label'";
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1L, result);
    }
}

/// <summary>
/// Tests for SetActiveVersionAsync (ADR-041 UC-3): switches active version by
/// updating catalog_items.current_version_label and synchronizing
/// content_hash from the activated version.
/// </summary>
public sealed class SetActiveVersionAsyncTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;

    public SetActiveVersionAsyncTests()
    {
        _fixture = new TempCatalogFixture();
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            new FileMetadataExtractionService(),
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<(string ItemId, string V1Id, string V2Id)> SeedItemWithTwoVersionsAsync()
    {
        var provider = _fixture.GetProvider();

        var v1Path = _fixture.CreateFakeRfaFile("Family-V1.rfa");
        var v1Request = new FamilyImportRequest(
            FilePath: v1Path, RevitMajorVersion: 2025, Category: null,
            Tags: null, Description: null, CategoryId: null,
            FamilySource: "loadable", RevitCategory: null, FileName: "Family-V1");
        var v1Result = await _importService.ImportFileAsync(v1Request);
        var itemId = v1Result.CatalogItemId!;

        // Create a v2 version by simulating re-import with different content.
        // We bypass the full import path and seed the DB manually so the test
        // is deterministic.
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        var v2Id = Guid.NewGuid().ToString();
        var v2FileId = Guid.NewGuid().ToString();
        var v2ContentHash = "AAAA_v2_hash";
        using var tx = conn.BeginTransaction();

        // family_files: v2 file record
        using var fCmd = conn.CreateCommand();
        fCmd.Transaction = tx;
        fCmd.CommandText = """
            INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
            VALUES (@id, @path, @name, 2025, @t)
            """;
        fCmd.Parameters.Add(new SqliteParameter("@id", v2FileId));
        fCmd.Parameters.Add(new SqliteParameter("@path", $"files/{itemId}/v2/Family-V2.rfa"));
        fCmd.Parameters.Add(new SqliteParameter("@name", "Family-V2.rfa"));
        fCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await fCmd.ExecuteNonQueryAsync();

        // catalog_versions: v2 — content_hash differs from v1
        using var vCmd = conn.CreateCommand();
        vCmd.Transaction = tx;
        vCmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                          revit_major_version, types_count, parameters_count,
                                          content_hash, hash_format_version, published_at_utc)
            VALUES (@id, @itemId, @fileId, @label, 2025, 1, 1, @hash, 1, @t)
            """;
        vCmd.Parameters.Add(new SqliteParameter("@id", v2Id));
        vCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        vCmd.Parameters.Add(new SqliteParameter("@fileId", v2FileId));
        vCmd.Parameters.Add(new SqliteParameter("@label", "v2"));
        vCmd.Parameters.Add(new SqliteParameter("@hash", v2ContentHash));
        vCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await vCmd.ExecuteNonQueryAsync();
        tx.Commit();

        // Capture v1's id
        using var v1Cmd = conn.CreateCommand();
        v1Cmd.CommandText = "SELECT id FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = 'v1'";
        v1Cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        var v1Id = (string)(await v1Cmd.ExecuteScalarAsync())!;

        // Update catalog_items.content_hash to v1's hash (whatever ImportFileAsync set).
        using var readV1Cmd = conn.CreateCommand();
        readV1Cmd.CommandText = "SELECT content_hash FROM catalog_versions WHERE id = @v1Id";
        readV1Cmd.Parameters.Add(new SqliteParameter("@v1Id", v1Id));
        var v1HashObj = await readV1Cmd.ExecuteScalarAsync();
        string? v1ContentHash = v1HashObj is DBNull or null ? null : (string)v1HashObj;

        using var updItemCmd = conn.CreateCommand();
        updItemCmd.CommandText = "UPDATE catalog_items SET content_hash = @hash, hash_format_version = 1 WHERE id = @itemId";
        updItemCmd.Parameters.Add(new SqliteParameter("@hash", (object?)v1ContentHash ?? DBNull.Value));
        updItemCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        await updItemCmd.ExecuteNonQueryAsync();

        return (itemId, v1Id, v2Id);
    }

    [Fact]
    public async Task SetActiveVersion_FromV1ToV2_UpdatesCurrentVersionLabel()
    {
        var (itemId, v1Id, v2Id) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Sanity: v1 is active right after the seed.
        var afterSeed = await provider.GetItemAsync(itemId);
        Assert.Equal("v1", afterSeed?.CurrentVersionLabel);

        var result = await provider.SetActiveVersionAsync(itemId, "v2");

        Assert.True(result.Success);
        Assert.Equal("v2", result.VersionLabel);
        Assert.Equal("v1", result.PreviousVersionLabel);
        Assert.True(result.ContentHashSynced);

        var afterSwitch = await provider.GetItemAsync(itemId);
        Assert.Equal("v2", afterSwitch?.CurrentVersionLabel);
    }

    [Fact]
    public async Task SetActiveVersion_SyncsContentHash_FromActivatedVersion()
    {
        // ADR-041: switching active version MUST propagate content_hash so
        // subsequent content-hash dedup is consistent with the new active version.
        var (itemId, v1Id, v2Id) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var v2HashCmd = conn.CreateCommand();
        v2HashCmd.CommandText = "SELECT content_hash FROM catalog_versions WHERE id = @v2Id";
        v2HashCmd.Parameters.Add(new SqliteParameter("@v2Id", v2Id));
        var v2Hash = (string?)await v2HashCmd.ExecuteScalarAsync();
        Assert.Equal("AAAA_v2_hash", v2Hash);

        var result = await provider.SetActiveVersionAsync(itemId, "v2");
        Assert.True(result.Success);
        Assert.True(result.ContentHashSynced);

        var item = await provider.GetItemAsync(itemId);
        Assert.Equal(v2Hash, item?.ContentHash);
    }

    [Fact]
    public async Task SetActiveVersion_NonExistentVersionLabel_ReturnsFailure()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.SetActiveVersionAsync(itemId, "v99");

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetActiveVersion_NonExistentCatalogItem_ReturnsFailure()
    {
        var provider = _fixture.GetProvider();
        var result = await provider.SetActiveVersionAsync("non-existent-item-id", "v1");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task SetActiveVersion_AlreadyActive_ReturnsSuccess_NoOp()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.SetActiveVersionAsync(itemId, "v1");
        Assert.True(result.Success);
        Assert.Equal("v1", result.PreviousVersionLabel);
    }

    [Fact]
    public async Task SetActiveVersion_ThenStaleDetector_UsesNewActiveVersion()
    {
        // ADR-041: stale detection reads current_version_label via SQL join
        // and via FamilyCatalogItem.CurrentVersionLabel. After switching,
        // the next GetItemAsync must return the new active label.
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        await provider.SetActiveVersionAsync(itemId, "v2");

        var item = await provider.GetItemAsync(itemId);
        Assert.Equal("v2", item?.CurrentVersionLabel);
    }

    [Fact]
    public async Task SetActiveVersion_AfterDeleteVersion_RejectsDeletedLabel()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Delete v2 first
        var delResult = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(delResult.Success);

        // Now attempt to activate v2 — must fail.
        var setResult = await provider.SetActiveVersionAsync(itemId, "v2");
        Assert.False(setResult.Success);
    }

    [Fact]
    public async Task SetActiveVersion_DifferentFileName_UpdatesItemName()
    {
        // Issue #126: the catalog item name follows the ACTIVE version's
        // file name. The seed stores v1 as "Family-V1.rfa" and v2 as
        // "Family-V2.rfa" — activating v2 must rename the item.
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.SetActiveVersionAsync(itemId, "v2");

        Assert.True(result.Success);
        Assert.True(result.NameChanged);
        Assert.Equal("Family-V2", result.NewName);

        var item = await provider.GetItemAsync(itemId);
        Assert.Equal("Family-V2", item?.Name);
        Assert.Equal(
            SmartCon.Core.Services.FamilyManager.FamilyNameNormalizer.Normalize("Family-V2"),
            item?.NormalizedName);
    }

    [Fact]
    public async Task SetActiveVersion_SameFileName_NameUnchanged()
    {
        // Activating the version whose file name matches the current item
        // name must NOT rewrite the name (NameChanged = false). The seed
        // already names the item "Family-V1" — the v1 file base name.
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.SetActiveVersionAsync(itemId, "v1");

        Assert.True(result.Success);
        Assert.False(result.NameChanged);

        var item = await provider.GetItemAsync(itemId);
        Assert.Equal("Family-V1", item?.Name);
    }
}

/// <summary>
/// Tests for DeleteVersionAsync (ADR-041 UC-4): hard-deletes a non-active
/// version and its dependent assets/files/types via FK CASCADE (V17).
/// </summary>
public sealed class DeleteVersionAsyncTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;

    public DeleteVersionAsyncTests()
    {
        _fixture = new TempCatalogFixture();
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            new FileMetadataExtractionService(),
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<(string ItemId, string V1Id, string V2Id)> SeedItemWithTwoVersionsAsync()
    {
        var provider = _fixture.GetProvider();

        var v1Path = _fixture.CreateFakeRfaFile("Family-Del-V1.rfa");
        var v1Request = new FamilyImportRequest(
            FilePath: v1Path, RevitMajorVersion: 2025, Category: null,
            Tags: null, Description: null, CategoryId: null,
            FamilySource: "loadable", RevitCategory: null, FileName: "Family-Del-V1.rfa");
        var v1Result = await _importService.ImportFileAsync(v1Request);
        var itemId = v1Result.CatalogItemId!;

        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();

        var v2Id = Guid.NewGuid().ToString();
        var v2FileId = Guid.NewGuid().ToString();
        using var fCmd = conn.CreateCommand();
        fCmd.CommandText = """
            INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
            VALUES (@id, @path, @name, 2025, @t)
            """;
        fCmd.Parameters.Add(new SqliteParameter("@id", v2FileId));
        fCmd.Parameters.Add(new SqliteParameter("@path", $"files/{itemId}/v2/Family-Del-V2.rfa"));
        fCmd.Parameters.Add(new SqliteParameter("@name", "Family-Del-V2.rfa"));
        fCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await fCmd.ExecuteNonQueryAsync();

        using var v2Cmd = conn.CreateCommand();
        v2Cmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                          revit_major_version, types_count, published_at_utc)
            VALUES (@id, @itemId, @fileId, @label, 2025, 1, @t)
            """;
        v2Cmd.Parameters.Add(new SqliteParameter("@id", v2Id));
        v2Cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        v2Cmd.Parameters.Add(new SqliteParameter("@fileId", v2FileId));
        v2Cmd.Parameters.Add(new SqliteParameter("@label", "v2"));
        v2Cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await v2Cmd.ExecuteNonQueryAsync();

        using var v1Cmd = conn.CreateCommand();
        v1Cmd.CommandText = "SELECT id FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = 'v1'";
        v1Cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        var v1Id = (string)(await v1Cmd.ExecuteScalarAsync())!;

        return (itemId, v1Id, v2Id);
    }

    [Fact]
    public async Task DeleteVersion_NonActiveVersion_DeletesRow()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // v2 is non-active (v1 is the current_version_label after import).
        var result = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(result.Success);
        Assert.Equal(1, result.VersionsDeleted);

        var versions = await provider.GetVersionsAsync(itemId);
        Assert.Single(versions); // only v1 left
    }

    [Fact]
    public async Task DeleteVersion_ActiveVersion_ReturnsFailure()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.DeleteVersionAsync(itemId, "v1");
        Assert.False(result.Success);
        Assert.Contains("active", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.VersionsDeleted);
    }

    [Fact]
    public async Task DeleteVersion_NonExistentLabel_ReturnsSuccessWithZeroRows()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Deleting a non-existent version is idempotent. Returns success
        // but VersionsDeleted=0. This mirrors DeleteItemAsync's pattern of
        // not raising on missing entities.
        var result = await provider.DeleteVersionAsync(itemId, "v99");
        Assert.True(result.Success);
        Assert.Equal(0, result.VersionsDeleted);
    }

    [Fact]
    public async Task DeleteVersion_CascadesFamilyTypes()
    {
        var (itemId, v1Id, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Create a fake family_type row linked to v2's versionId via
        // family_types.version_id (which has ON DELETE CASCADE after V17).
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();

        var v2Id = (await provider.GetVersionByLabelAsync(itemId, "v2"))!.Id;

        // Seed a type row for v2 version
        using var typeCmd = conn.CreateCommand();
        typeCmd.CommandText = """
            INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
            VALUES (@id, @itemId, 'V2-Type', 0, @v2Id, NULL)
            """;
        typeCmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        typeCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        typeCmd.Parameters.Add(new SqliteParameter("@v2Id", v2Id));
        await typeCmd.ExecuteNonQueryAsync();

        // Sanity: type exists
        using var countBeforeCmd = conn.CreateCommand();
        countBeforeCmd.CommandText = "SELECT COUNT(*) FROM family_types WHERE version_id = @v2Id";
        countBeforeCmd.Parameters.Add(new SqliteParameter("@v2Id", v2Id));
        var beforeCount = (long)(await countBeforeCmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, beforeCount);

        // Delete v2 — should cascade to family_types
        var result = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(result.Success);
        Assert.Equal(1, result.VersionsDeleted);

        // Sanity: type is gone via FK CASCADE
        using var countAfterCmd = conn.CreateCommand();
        countAfterCmd.CommandText = "SELECT COUNT(*) FROM family_types WHERE version_id = @v2Id";
        countAfterCmd.Parameters.Add(new SqliteParameter("@v2Id", v2Id));
        var afterCount = (long)(await countAfterCmd.ExecuteScalarAsync())!;
        Assert.Equal(0L, afterCount);
    }

    [Fact]
    public async Task DeleteVersion_RemovesPhysicalFilesFromDisk()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Place a fake file inside the v2 storage directory
        var v2Dir = Path.Combine(_fixture.GetDatabaseRoot(), "files", itemId, "v2");
        Directory.CreateDirectory(v2Dir);
        var fakeFile = Path.Combine(v2Dir, "Family-Del-V2.rfa");
        File.WriteAllText(fakeFile, "FAKE_RFA_CONTENT");

        Assert.True(File.Exists(fakeFile));

        var result = await provider.DeleteVersionAsync(itemId, "v2");

        Assert.True(result.Success);
        Assert.True(result.FilesDeleted);
        Assert.False(File.Exists(fakeFile));
        Assert.False(Directory.Exists(v2Dir));
    }

    [Fact]
    public async Task DeleteVersion_DoesNotAffectOtherVersions()
    {
        var (itemId, v1Id, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(result.Success);

        var item = await provider.GetItemAsync(itemId);
        Assert.NotNull(item);
        Assert.Equal("v1", item?.CurrentVersionLabel);

        var versions = await provider.GetVersionsAsync(itemId);
        Assert.Single(versions);
        Assert.Equal("v1", versions[0].VersionLabel);
    }

    [Fact]
    public async Task GetVersionByIdAsync_ReturnsCorrectVersion()
    {
        var (itemId, v1Id, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.GetVersionByIdAsync(itemId, v1Id);
        Assert.NotNull(result);
        Assert.Equal("v1", result!.VersionLabel);
    }

    [Fact]
    public async Task GetVersionByIdAsync_NonExistent_ReturnsNull()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.GetVersionByIdAsync(itemId, "non-existent-version-id");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetVersionByLabelAsync_MatchesRevitVariant()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.GetVersionByLabelAsync(itemId, "v1", targetRevitMajorVersion: 2025);
        Assert.NotNull(result);
        Assert.Equal("v1", result!.VersionLabel);
    }

    [Fact]
    public async Task GetVersionByLabelAsync_NonExistent_ReturnsNull()
    {
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        var result = await provider.GetVersionByLabelAsync(itemId, "v999");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteVersion_RemovesOrphanFamilyFilesRows()
    {
        // ADR-041: deleting a version must also DELETE the corresponding
        // family_files row BEFORE deleting catalog_versions (because the FK
        // direction is catalog_versions.file_id → family_files.id — deleting
        // catalog_versions does NOT cascade to family_files). Orphan file rows
        // would leak storage metadata until manual cleanup.
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Read v2's file_id BEFORE deleting
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var readFileIdCmd = conn.CreateCommand();
        readFileIdCmd.CommandText = "SELECT file_id FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = 'v2'";
        readFileIdCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        var v2FileId = (string)(await readFileIdCmd.ExecuteScalarAsync())!;

        // Sanity: file row exists
        using var countBeforeCmd = conn.CreateCommand();
        countBeforeCmd.CommandText = "SELECT COUNT(*) FROM family_files WHERE id = @fileId";
        countBeforeCmd.Parameters.Add(new SqliteParameter("@fileId", v2FileId));
        Assert.Equal(1L, (long)(await countBeforeCmd.ExecuteScalarAsync())!);

        // Delete v2 — family_files row must be cleaned up explicitly by DeleteVersionAsync.
        var result = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(result.Success);

        using var countAfterCmd = conn.CreateCommand();
        countAfterCmd.CommandText = "SELECT COUNT(*) FROM family_files WHERE id = @fileId";
        countAfterCmd.Parameters.Add(new SqliteParameter("@fileId", v2FileId));
        Assert.Equal(0L, (long)(await countAfterCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DeleteVersion_ClearsDanglingImportRunsVersionId()
    {
        // ADR-041: family_data_import_runs.version_id is a soft pointer (no FK);
        // DeleteVersionAsync must NULL it out so audit rows survive but the
        // dangling pointer is gone.
        var (itemId, _, _) = await SeedItemWithTwoVersionsAsync();
        var provider = _fixture.GetProvider();

        // Get v2's versionId
        var v2Version = await provider.GetVersionByLabelAsync(itemId, "v2");
        Assert.NotNull(v2Version);

        // Seed a family_data_import_runs row pointing at v2 (audit row)
        var runId = Guid.NewGuid().ToString();
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var seedRunCmd = conn.CreateCommand();
        seedRunCmd.CommandText = """
            INSERT INTO family_data_import_runs (id, catalog_item_id, version_id, revit_major_version, status, types_count, started_at_utc)
            VALUES (@id, @itemId, @v2Id, 2025, 'Succeeded', 0, @t)
            """;
        seedRunCmd.Parameters.Add(new SqliteParameter("@id", runId));
        seedRunCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        seedRunCmd.Parameters.Add(new SqliteParameter("@v2Id", v2Version!.Id));
        seedRunCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await seedRunCmd.ExecuteNonQueryAsync();

        // Sanity: row exists with version_id pointing at v2
        using var beforeCmd = conn.CreateCommand();
        beforeCmd.CommandText = "SELECT version_id FROM family_data_import_runs WHERE id = @id";
        beforeCmd.Parameters.Add(new SqliteParameter("@id", runId));
        Assert.Equal(v2Version.Id, (string)(await beforeCmd.ExecuteScalarAsync())!);

        // Delete v2 — audit row must survive but version_id must be NULLed
        var result = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(result.Success);

        using var afterCmd = conn.CreateCommand();
        afterCmd.CommandText = "SELECT version_id FROM family_data_import_runs WHERE id = @id";
        afterCmd.Parameters.Add(new SqliteParameter("@id", runId));
        var afterRow = await afterCmd.ExecuteScalarAsync();
        Assert.True(afterRow is DBNull || afterRow is null);
    }
}
