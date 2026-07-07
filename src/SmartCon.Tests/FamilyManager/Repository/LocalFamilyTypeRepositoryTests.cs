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
    private async Task<string> SeedCatalogVersionAsync(string catalogItemId, string versionId, string versionLabel)
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

        return fileId;
    }

    /// <summary>
    /// v2.1.0 (ADR-041 rev #2): creates a catalog_item WITHOUT a
    /// catalog_versions row — the operationally correct setup for the
    /// orchestrator case (LoadableFamilyImportOrchestrator /
    /// SystemFamilyImportOrchestrator import system/loadable families from
    /// the project with no version handle). Such catalog_items have
    /// <c>current_version_label = NULL</c> and family_types rows with
    /// <c>version_id IS NULL</c>.
    ///
    /// Tests that exercise the orchestrator path must use this helper instead
    /// of <see cref="SeedItemAsync"/> (which seeds a catalog_versions row via
    /// ImportFileAsync — that would make <c>GetTypesForItemAsync</c> filter
    /// to the active version's types and exclude the orchestrator rows).
    /// </summary>
    private async Task<string> SeedBareCatalogItemAsync(string fileName)
    {
        var itemId = Guid.NewGuid().ToString("N");
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, family_source, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, 'Active', 'loadable', @now, @now)
            """;
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", itemId));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@name", fileName));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@norm", fileName.ToLowerInvariant()));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
        return itemId;
    }

    /// <summary>
    /// v2.1.0 (ADR-041 rev #2): sets catalog_items.current_version_label
    /// for the given catalog_item, marking <paramref name="versionId"/> as
    /// the active version. Tests use this to make <paramref name="versionId"/>
    /// the one returned by <c>GetTypesForItemAsync</c>'s active-version
    /// subquery.
    /// </summary>
    private async Task SetActiveVersionAsync(string catalogItemId, string versionLabel)
    {
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE catalog_items SET current_version_label = @label WHERE id = @id";
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@label", versionLabel));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", catalogItemId));
        await cmd.ExecuteNonQueryAsync();
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
        // v2.1.0 (ADR-041 rev #2): orchestrator case. A bare catalog_item
        // (no catalog_versions) stores types with version_id IS NULL. The
        // active-version fallback in GetTypesForItemAsync returns those.
        var itemId = await SeedBareCatalogItemAsync("TwoTypes.rfa");
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
        // v2.1.0 (ADR-041 rev #2): orchestrator case (no catalog_versions).
        var itemId = await SeedBareCatalogItemAsync("ReplaceTypes.rfa");

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
        // v2.1.0 (ADR-041 rev #2): batch read uses the same active-version
        // filter. Both items use the orchestrator setup (bare catalog_items
        // → version_id IS NULL types, no catalog_versions fallback).
        var itemId1 = await SeedBareCatalogItemAsync("BatchA.rfa");
        var itemId2 = await SeedBareCatalogItemAsync("BatchB.rfa");

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
        var itemId = await SeedBareCatalogItemAsync("HasSome.rfa");
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
        // v2.1.0 (ADR-041 rev #2): orchestrator case (no catalog_versions).
        var itemId = await SeedBareCatalogItemAsync("Props.rfa");

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
        var itemId = await SeedBareCatalogItemAsync("GhostTypes.rfa");

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
        var itemId = await SeedBareCatalogItemAsync("EmptyList.rfa");

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
    public async Task SyncTypesAsync_ActiveImport_PreservesOtherVersionsTypes()
    {
        // v2.1.0 (ADR-041 rev #2 — replaces ADR-036 rev #3 "collapse to
        // current"): for ACTIVE family import (versionId != null),
        // SyncTypesAsync now deletes ONLY the types whose version_id =
        // the supplied versionId. Types of OTHER versions stay untouched
        // so the user can roll back via SetActiveVersionAsync and still
        // find their type rows. The catalog_versions history together
        // with per-version type storage is what makes rollback usable.
        //
        // Setup: two catalog_versions rows (active = v1, inactive = v2)
        // for a single catalog_item. Seed types into each via the
        // active-import path (versionId != null) — the per-version
        // UNIQUE(catalog_item_id, version_id, type_name) (V18 migration)
        // lets the same type name ("Type 1") coexist in both versions.
        var itemId = await SeedItemAsync("MultiVersion.rfa");
        const string versionA = "version-A-guid";
        const string versionB = "version-B-guid";
        const string labelA = "vA";
        const string labelB = "vB";

        var fileA = await SeedCatalogVersionAsync(itemId, versionA, labelA);
        var fileB = await SeedCatalogVersionAsync(itemId, versionB, labelB);

        await SetActiveVersionAsync(itemId, labelA);

        // Seed version A (active): 2 types, version_id = versionA.
        await _repository.SyncTypesAsync(itemId, versionA, fileA, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("a1", itemId, "A-Type-1", 0, versionA, fileA),
            new("a2", itemId, "A-Type-2", 1, versionA, fileA)
        });

        // Seed version B (inactive): 2 types, version_id = versionB. In
        // the cross-version collapse world (ADR-036 rev #3), this would
        // have wiped version A's types. With the per-version UNIQUE +
        // version-scoped DELETE (ADR-041 rev #2), version A still has
        // its 2 types.
        await _repository.SyncTypesAsync(itemId, versionB, fileB, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("b1", itemId, "B-Type-1", 0, versionB, fileB),
            new("b2", itemId, "B-Type-2", 1, versionB, fileB)
        });

        // Active version (vA) → 2 types returned by GetTypesForItemAsync.
        Assert.Equal(2, (await _repository.GetTypesForItemAsync(itemId)).Count);

        // Version B types are also still on disk — verify via the explicit
        // version-scoped query. This is the precondition for rollback:
        // switching current_version_label to "vB" must surface them.
        var versionBTypes = await _repository.GetTypesForItemVersionAsync(itemId, versionB);
        Assert.Equal(2, versionBTypes.Count);

        // Replace version A types (OverwriteCurrent-style semantics): 1
        // type only. Version scoped DELETE removes versionA's 2 types, then
        // inserts the new 1 — versionB stays untouched.
        await _repository.SyncTypesAsync(itemId, versionA, fileA, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("a1", itemId, "A-Type-1-Renamed", 0, versionA, fileA)
        });

        // Active version (still vA) → 1 type now.
        var remaining = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(remaining);
        Assert.Equal("A-Type-1-Renamed", remaining[0].Name);

        // Version B is untouched — still 2 types.
        var versionBTypesAfterReplace = await _repository.GetTypesForItemVersionAsync(itemId, versionB);
        Assert.Equal(2, versionBTypesAfterReplace.Count);

        // Rollback: switch active version to vB. GetTypesForItemAsync must
        // now return vB's types — the regression that ADR-041 rev #2 fixes.
        await SetActiveVersionAsync(itemId, labelB);

        var afterRollback = await _repository.GetTypesForItemAsync(itemId);
        Assert.Equal(2, afterRollback.Count);
        Assert.Equal("B-Type-1", afterRollback[0].Name);
        Assert.Equal("B-Type-2", afterRollback[1].Name);
    }

    [Fact]
    public async Task SyncTypesAsync_ReturnsTypeIdsMap()
    {
        // v2.0.0 (ADR-036): SyncTypesAsync returns a {name → id} map so
        // callers (e.g. FamilyDataImportService) can link attribute values
        // to types in a single round-trip.
        // v2.1.0 (ADR-041 rev #2): orchestrator case (no catalog_versions).
        var itemId = await SeedBareCatalogItemAsync("ReturnMap.rfa");
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

        // v2.0.0 (ADR-041): family_types.version_id FK → catalog_versions(id)
        // and family_types.file_id FK → family_files(id) (V17). Both must be
        // seeded before SyncTypesAsync inserts types referencing them.
        var realFileId = await SeedCatalogVersionAsync(itemId, versionId, "PersistVerFile");

        // v2.1.0 (ADR-041 rev #2): mark this version as active so
        // GetTypesForItemAsync's active-version filter returns our rows.
        await SetActiveVersionAsync(itemId, "PersistVerFile");

        await _repository.SyncTypesAsync(itemId, versionId, realFileId, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("pvf-1", itemId, "Type A", 0, versionId, realFileId),
            new("pvf-2", itemId, "Type B", 1, versionId, realFileId)
        });

        var stored = await _repository.GetTypesForItemAsync(itemId);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, t =>
        {
            Assert.Equal(versionId, t.VersionId);
            Assert.Equal(realFileId, t.FileId);
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
        // v2.1.0 (ADR-041 rev #2): orchestrator case (no catalog_versions).
        var itemId = await SeedBareCatalogItemAsync("DescriptorSort.rfa");

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

    // ── ADR-041 rev #2: per-version unique behavior ────────────────────

    [Fact]
    public async Task SyncTypesAsync_SameTypeNameInDifferentVersions_BothStored()
    {
        // v2.1.0 (ADR-041 rev #2): the per-version UNIQUE (catalog_item_id,
        // version_id, type_name) — added by V18 migration — allows the
        // same type name ("100", "200") to coexist in multiple versions
        // of the same catalog item. Without this, rolling back to an
        // earlier version would have nothing in family_types for that
        // version (the new version's SyncTypesAsync UPSERT would have
        // reassigned existing rows to the new version_id).
        var itemId = await SeedBareCatalogItemAsync("SameNamePerVersion.rfa");
        const string versionA = "ver-A-same-name";
        const string versionB = "ver-B-same-name";
        const string labelA = "vA";
        const string labelB = "vB";

        var fileA = await SeedCatalogVersionAsync(itemId, versionA, labelA);
        var fileB = await SeedCatalogVersionAsync(itemId, versionB, labelB);

        await _repository.SyncTypesAsync(itemId, versionA, fileA, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("a1", itemId, "Type 100", 0, versionA, fileA)
        });

        // Insert vB with the SAME type name "Type 100" — must NOT throw
        // (per-version UNIQUE makes this a separate row), and must NOT
        // wipe vA's row (version-scoped DELETE).
        await _repository.SyncTypesAsync(itemId, versionB, fileB, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("b1", itemId, "Type 100", 0, versionB, fileB)
        });

        // Both versions have their own "Type 100" row.
        Assert.Single(await _repository.GetTypesForItemVersionAsync(itemId, versionA));
        Assert.Single(await _repository.GetTypesForItemVersionAsync(itemId, versionB));

        // Active version filter: with vA active, "Type 100" from vA is returned.
        await SetActiveVersionAsync(itemId, labelA);
        var aRows = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(aRows);
        Assert.Equal(versionA, aRows[0].VersionId);

        // After switching to vB, "Type 100" from vB is returned.
        await SetActiveVersionAsync(itemId, labelB);
        var bRows = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(bRows);
        Assert.Equal(versionB, bRows[0].VersionId);
    }

    [Fact]
    public async Task GetTypesForItemAsync_NoActiveVersion_FallsBackToOrchestratorTypes()
    {
        // v2.1.0 (ADR-041 rev #2): when catalog_item has a current_version_label
        // that does NOT match any catalog_versions row (anomaly) OR is NULL,
        // and there are types with version_id IS NULL in the DB (orchestrator
        // case), GetTypesForItemAsync returns those orchestrator types as a
        // fallback rather than returning an empty list.
        var itemId = await SeedBareCatalogItemAsync("OrchestratorFallback.rfa");
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("orch1", itemId, "Orch Type", 0)
        });

        // No catalog_versions → current_version_label is irrelevant;
        // GetTypesForItemAsync falls back to orchestrator types.
        var result = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(result);
        Assert.Equal("Orch Type", result[0].Name);
        Assert.Null(result[0].VersionId);
    }

    [Fact]
    public async Task GetAllTypesBatchAsync_ActiveVersionFilter_PicksPerItemActiveVersion()
    {
        // v2.1.0 (ADR-041 rev #2): batch variant of the active-version filter.
        // Two items, each with two versions: item1 active=vA (1 type), item2
        // active=vB (2 types). The batch must return only the active version's
        // types per item.
        var itemId1 = await SeedBareCatalogItemAsync("Batch1.rfa");
        var itemId2 = await SeedBareCatalogItemAsync("Batch2.rfa");

        var f1A = await SeedCatalogVersionAsync(itemId1, "v1A-id", "vA");
        await SeedCatalogVersionAsync(itemId1, "v1B-id", "vB");
        await SetActiveVersionAsync(itemId1, "vA");

        await SeedCatalogVersionAsync(itemId2, "v2A-id", "vA");
        var f2B = await SeedCatalogVersionAsync(itemId2, "v2B-id", "vB");
        await SetActiveVersionAsync(itemId2, "vB");

        await _repository.SyncTypesAsync(itemId1, "v1A-id", f1A, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("i1a-1", itemId1, "Item1 Active Type", 0, "v1A-id", f1A)
        });
        await _repository.SyncTypesAsync(itemId2, "v2B-id", f2B, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("i2b-1", itemId2, "Item2 Active Type B1", 0, "v2B-id", f2B),
            new("i2b-2", itemId2, "Item2 Active Type B2", 1, "v2B-id", f2B)
        });

        var batch = await _repository.GetAllTypesBatchAsync(new[] { itemId1, itemId2 });
        Assert.Single(batch[itemId1]);
        Assert.Equal("Item1 Active Type", batch[itemId1][0].Name);
        Assert.Equal(2, batch[itemId2].Count);
    }

    [Fact]
    public async Task SyncTypesAsync_OrchestratorScope_DoesNotDeleteVersionedTypes()
    {
        // v2.1.0 (ADR-041 rev #2): the orchestrator case (versionId == null
        // AND fileId == null) must DELETE only rows with version_id IS NULL.
        // Versioned types of other versions stay untouched — protects against
        // a regression where orchestrator imports would silently wipe a
        // managed family's versioned types.
        var itemId = await SeedBareCatalogItemAsync("OrchestratorSafe.rfa");
        const string versionId = "ver-orch-safe";
        var fileId = await SeedCatalogVersionAsync(itemId, versionId, "vActive");
        await SetActiveVersionAsync(itemId, "vActive");

        // Seed versioned types (version_id != null) for vActive.
        await _repository.SyncTypesAsync(itemId, versionId, fileId, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("ver-1", itemId, "Versioned Type", 0, versionId, fileId)
        });

        // Run orchestrator sync: must NOT delete the versioned row.
        await _repository.SyncTypesAsync(itemId, null, null, NoRunId, new List<FamilyTypeDescriptor>
        {
            new("orch-1", itemId, "Orchestrator Type", 0)
        });

        // The versioned type is still queryable by its version.
        var versionedTypes = await _repository.GetTypesForItemVersionAsync(itemId, versionId);
        Assert.Single(versionedTypes);
        Assert.Equal("Versioned Type", versionedTypes[0].Name);

        // Active version filter returns the versioned type (vActive is active).
        var activeTypes = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(activeTypes);
        Assert.Equal("Versioned Type", activeTypes[0].Name);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
