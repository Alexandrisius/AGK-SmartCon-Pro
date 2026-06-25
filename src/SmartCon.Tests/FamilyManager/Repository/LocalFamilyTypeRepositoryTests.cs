using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyTypeRepositoryTests : IDisposable
{
    private const string NoRunId = "no-run";

    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyTypeRepository _repository;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyTypeRepositoryTests()
    {
        _fixture = new TempCatalogFixture();


        _repository = new LocalFamilyTypeRepository(_fixture.GetDatabase());


        var metadataService = new FileMetadataExtractionService();
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

    private async Task<string> SeedItemAsync(string fileName)
    {
        var path = _fixture.CreateFakeRfaFile(fileName);
        var result = await _importService.ImportFileAsync(new FamilyImportRequest(path, 2025, null, null, null));
        Assert.True(result.Success);
        return result.CatalogItemId!;
    }

    // v2.0.0 (ADR-036, rev #3): SyncTypesAsync collapses all versions for
    // catalog_item (no JOIN on catalog_versions). Tests that exercise the
    // versionId scope must still seed catalog_versions rows (with a real
    // family_files parent for the FK) so other test queries against
    // GetTypesForItemVersionAsync work correctly.
    private async Task SeedCatalogVersionAsync(string catalogItemId, string versionId, string versionLabel)
    {
        var fileId = Guid.NewGuid().ToString();
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using (var fileCmd = connection.CreateCommand())
        {
            fileCmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, 'test.rfa', 'test.rfa', 2025, '2026-06-25T00:00:00Z')
                """;
            fileCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", fileId));
            await fileCmd.ExecuteNonQueryAsync();
        }

        using (var verCmd = connection.CreateCommand())
        {
            verCmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, @label, 2025, '2026-06-25T00:00:00Z')
                """;
            verCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", versionId));
            verCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", catalogItemId));
            verCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@fileId", fileId));
            verCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@label", versionLabel));
            await verCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GetTypesForItemAsync_NoTypes_ReturnsEmptyList()
    {
        var itemId = await SeedItemAsync("NoTypes.rfa");

        var types = await _repository.GetTypesForItemAsync(itemId);

        Assert.Empty(types);
    }

    [Fact]
    public async Task SyncTypesAsync_TwoTypes_BothAppearInGet()
    {
        var itemId = await SeedItemAsync("TwoTypes.rfa");
        var types = new List<FamilyTypeDescriptor>
        {
            new("t1", itemId, "Type A", 0),
            new("t2", itemId, "Type B", 1)
        };

        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, types);
        var result = await _repository.GetTypesForItemAsync(itemId);

        Assert.Equal(2, result.Count);
        Assert.Equal("Type A", result[0].Name);
        Assert.Equal("Type B", result[1].Name);
        Assert.Equal(itemId, result[0].CatalogItemId);
        Assert.Equal(itemId, result[1].CatalogItemId);
    }

    [Fact]
    public async Task SyncTypesAsync_CalledTwice_ReplacesExistingTypes()
    {
        var itemId = await SeedItemAsync("ReplaceTypes.rfa");

        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("old1", itemId, "Old Type 1", 0),
            new("old2", itemId, "Old Type 2", 1)
        });

        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("new1", itemId, "New Type", 0)
        });

        var result = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(result);
        Assert.Equal("New Type", result[0].Name);
    }

    [Fact]
    public async Task GetAllTypesBatchAsync_MultipleItems_AllReturned()
    {
        var itemId1 = await SeedItemAsync("BatchA.rfa");
        var itemId2 = await SeedItemAsync("BatchB.rfa");

        await _repository.SyncTypesAsync(itemId1, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("b1", itemId1, "Batch Type A", 0)
        });
        await _repository.SyncTypesAsync(itemId2, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("b2", itemId2, "Batch Type B1", 0),
            new("b3", itemId2, "Batch Type B2", 1)
        });

        var batch = await _repository.GetAllTypesBatchAsync(new[] { itemId1, itemId2 });

        Assert.Equal(2, batch.Count);
        Assert.Single(batch[itemId1]);
        Assert.Equal(2, batch[itemId2].Count);
    }

    [Fact]
    public async Task HasTypesAsync_NoTypes_ReturnsFalse()
    {
        var itemId = await SeedItemAsync("HasNone.rfa");

        var has = await _repository.HasTypesAsync(itemId);

        Assert.False(has);
    }

    [Fact]
    public async Task HasTypesAsync_WithTypes_ReturnsTrue()
    {
        var itemId = await SeedItemAsync("HasSome.rfa");
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("ht1", itemId, "Has Type", 0)
        });

        var has = await _repository.HasTypesAsync(itemId);

        Assert.True(has);
    }

    [Fact]
    public async Task SyncTypesAsync_PropertiesCorrectlyStored()
    {
        var itemId = await SeedItemAsync("Props.rfa");

        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("prop-id-1", itemId, "First", 0),
            new("prop-id-2", itemId, "Second", 1)
        });

        var result = await _repository.GetTypesForItemAsync(itemId);

        Assert.Equal("prop-id-1", result[0].Id);
        Assert.Equal("prop-id-2", result[1].Id);
        Assert.Equal(0, result[0].SortOrder);
        Assert.Equal(1, result[1].SortOrder);
    }

    // ── ADR-036: bug #1 regression tests ─────────────────────────────────

    [Fact]
    public async Task SyncTypesAsync_RemovesTypesMissingFromNewList()
    {
        // v2.0.0 (ADR-036, bug #1): the old SaveTypesForRunAsync used UPSERT
        // without DELETE, so a type removed from the .rfa would persist in
        // the DB as a ghost. SyncTypesAsync must drop it.
        var itemId = await SeedItemAsync("GhostTypes.rfa");

        // Initial: 3 types
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("t1", itemId, "Type A", 0),
            new("t2", itemId, "Type B", 1),
            new("t3", itemId, "Type C", 2)
        });

        // Second sync: only 1 type (B was removed in Revit)
        var typeIds = await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("t1", itemId, "Type A", 0)
        });

        // Only Type A remains in DB
        var result = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(result);
        Assert.Equal("Type A", result[0].Name);

        // Returned typeIds map should contain only "Type A"
        Assert.Single(typeIds);
        Assert.True(typeIds.ContainsKey("Type A"));
    }

    [Fact]
    public async Task SyncTypesAsync_EmptyList_DeletesAllTypes()
    {
        // v2.0.0 (ADR-036): empty list = delete all types for the item.
        var itemId = await SeedItemAsync("EmptyList.rfa");

        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("t1", itemId, "Type A", 0),
            new("t2", itemId, "Type B", 1)
        });

        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>());

        var result = await _repository.GetTypesForItemAsync(itemId);
        Assert.Empty(result);
        Assert.False(await _repository.HasTypesAsync(itemId));
    }

    [Fact]
    public async Task SyncTypesAsync_ActiveImport_CollapsesAllVersionsToCurrent()
    {
        // v2.0.0 (ADR-036, rev #3 fix for issue #85): for ACTIVE family
        // import (versionId != null), SyncTypesAsync must collapse the
        // type set to the current version — DELETE removes every row
        // for the catalog_item, not just the rows tagged with the
        // current versionId. This is destructive on multi-version
        // families, but that's the point: when the user clicks
        // "Импорт активного файла" they want the catalog to reflect
        // the current .rfa, not a union of historical versions that
        // would otherwise show up as ghost types in the UI.
        //
        // Setup: create catalog_versions rows for two versions ("A"
        // and "B"), seed types into each via separate SyncTypesAsync
        // calls (case a / orchestrator for the seed, which is a
        // replace-all), then run an "active" SyncTypesAsync (case b,
        // versionId != null) with only the "current" types. The old
        // version's types must all be gone.
        var itemId = await SeedItemAsync("MultiVersion.rfa");
        const string versionA = "version-A";
        const string versionB = "version-B";

        await SeedCatalogVersionAsync(itemId, versionA, "A");
        await SeedCatalogVersionAsync(itemId, versionB, "B");

        // Seed version A via orchestrator (case a): 2 types, no versionId.
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("a1", itemId, "A-Type-1", 0),
            new("a2", itemId, "A-Type-2", 1)
        });

        // Seed version B via orchestrator: 2 types (orchestrator is
        // replace-all, so this wipes version A's 2 types and inserts B's 2).
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("b1", itemId, "B-Type-1", 0),
            new("b2", itemId, "B-Type-2", 1)
        });

        // Before active import: 2 types (version B only — version A was
        // wiped by version B's replace-all sync).
        Assert.Equal(2, (await _repository.GetTypesForItemAsync(itemId)).Count);

        // Active import for version A: 1 type only. This must wipe
        // version B's types too (collapse to current).
        await _repository.SyncTypesAsync(itemId, versionA, "file-A", NoRunId, new List<FamilyTypeDescriptor>
        {
            new("a1", itemId, "A-Type-1-Renamed", 0)
        });

        // After: only the single current type exists.
        var remaining = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(remaining);
        Assert.Equal("A-Type-1-Renamed", remaining[0].Name);

        // And version B is gone (no versionLabel="B" types at all).
        var versionBTypes = await _repository.GetTypesForItemVersionAsync(itemId, versionB);
        Assert.Empty(versionBTypes);
    }

    [Fact]
    public async Task SyncTypesAsync_ReturnsTypeIdsMap()
    {
        // v2.0.0 (ADR-036): SyncTypesAsync returns a {name → id} map so
        // callers (e.g. FamilyDataImportService) can link attribute values
        // to types in a single round-trip.
        var itemId = await SeedItemAsync("ReturnMap.rfa");
        var input = new List<FamilyTypeDescriptor>
        {
            new("input-t1", itemId, "Type A", 0),
            new("input-t2", itemId, "Type B", 1)
        };

        var result = await _repository.SyncTypesAsync(itemId, null, null, NoRunId, input);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("Type A"));
        Assert.True(result.ContainsKey("Type B"));
        // IDs from the map are the actual row PKs (not the input.Id from
        // the descriptor) — UPSERT returns the id from RETURNING.
        var stored = await _repository.GetTypesForItemAsync(itemId);
        Assert.Equal(stored[0].Id, result["Type A"]);
        Assert.Equal(stored[1].Id, result["Type B"]);
    }

    [Fact]
    public async Task SyncTypesAsync_VersionIdAndFileId_Persisted()
    {
        // v2.0.0 (ADR-036, rev #2): when caller passes a non-null versionId
        // and fileId, the resulting family_types rows must store them.
        // Regression test for Bug #1: SystemFamilyImportOrchestrator was
        // building descriptors with real VersionId/FileId values but
        // passing null/null to SyncTypesAsync, losing the FK data.
        var itemId = await SeedItemAsync("PersistVerFile.rfa");
        const string versionId = "test-version-id-001";
        const string fileId = "test-file-id-001";

        await _repository.SyncTypesAsync(itemId, versionId, fileId, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("pvf-1", itemId, "Type A", 0, versionId, fileId),
            new("pvf-2", itemId, "Type B", 1, versionId, fileId)
        });

        var stored = await _repository.GetTypesForItemAsync(itemId);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, t =>
        {
            Assert.Equal(versionId, t.VersionId);
            Assert.Equal(fileId, t.FileId);
        });

        // And GetTypesForItemVersionAsync should return the same rows when
        // queried with the versionId we stored.
        var byVersion = await _repository.GetTypesForItemVersionAsync(itemId, versionId);
        Assert.Equal(2, byVersion.Count);
        Assert.Equal("Type A", byVersion[0].Name);
        Assert.Equal("Type B", byVersion[1].Name);
    }

    [Fact]
    public async Task SyncTypesAsync_DescriptorSortOrder_IsUsed()
    {
        // v2.0.0 (ADR-036, Bug #7 fix): the SQL @sort parameter must use
        // types[i].SortOrder (the descriptor's SortOrder), not the loop
        // index `i`. Previously the repository hard-coded `@sort = i`,
        // which discarded the caller's SortOrder. This breaks callers
        // like FamilyDataImportService that compute SortOrder from
        // FamilyExtractionResult.Types (which already has its own ordering).
        var itemId = await SeedItemAsync("DescriptorSort.rfa");

        // Caller passes a SortOrder that does NOT match the list position.
        // Reverse order: Type A claims SortOrder=5, Type B claims SortOrder=2.
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("dso-1", itemId, "Type A", 5),
            new("dso-2", itemId, "Type B", 2)
        });

        var stored = await _repository.GetTypesForItemAsync(itemId);
        // SQL ORDER BY sort_order should return Type B (sort_order=2) before Type A (sort_order=5).
        Assert.Equal(2, stored.Count);
        Assert.Equal("Type B", stored[0].Name);
        Assert.Equal(2, stored[0].SortOrder);
        Assert.Equal("Type A", stored[1].Name);
        Assert.Equal(5, stored[1].SortOrder);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
