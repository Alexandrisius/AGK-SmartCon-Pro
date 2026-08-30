using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Routing;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// <see cref="CatalogRoutingEditorService"/> (ADR-072, World B): the editor
/// save edits the item-level routing links IN PLACE (no version is created,
/// no hash is touched) and regenerates the current version's dependency
/// links atomically; legacy items (no item rows) fall back to the current
/// version's V34 rows; removed parts locked by archived versions are
/// reported.
/// </summary>
public sealed class CatalogRoutingEditorServiceTests
{
    private const int PipeCategoryId = RoutingGroupCatalog.PipeCurvesCategoryId;

    private static SystemFamilySnapshot BuildSnapshot(RoutingPreferencesSnapshot typeARouting) => new(
        CategoryName: "Трубы",
        CategoryId: PipeCategoryId,
        Types:
        [
            new SystemTypeSnapshot(
                "Type A",
                [new SystemParameterValue("Diameter", "Double", true, "50", 50.0, null)],
                Routing: typeARouting,
                FamilyName: "Pipe Types",
                FamilyKey: "Single"),
            new SystemTypeSnapshot(
                "Type B",
                [new SystemParameterValue("Diameter", "Double", true, "32", 32.0, null)],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(0, "Seg B", "", []),
                    new RoutingRuleSnapshot(1, "ElbowOld:DN32", "", []),
                ]),
                FamilyName: "Pipe Types",
                FamilyKey: "Single"),
        ]);

    private static RoutingPreferencesSnapshot TypeARouting(string elbowPart) => new(0,
    [
        new RoutingRuleSnapshot(0, "Seg A", "", []),
        new RoutingRuleSnapshot(1, elbowPart, "отвод",
            [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.0, 0.0)]),
    ]);

    private static async Task<(string ItemId, string VersionId)> SeedSystemItemAsync(
        TempCatalogFixture fixture, string itemId, string versionLabel, RoutingPreferencesSnapshot typeARouting,
        bool seedItemTables = true)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        await ExecAsync(connection, """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, current_version_label,
                family_source, revit_category, revit_category_id,
                created_at_utc, updated_at_utc)
            VALUES (@id, 'Pipes', @norm, 'Active', @label, 'system', 'Pipes', @cat,
                '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z')
            """,
            ("@id", itemId), ("@norm", FamilyNameNormalizer.Normalize("Pipes")),
            ("@label", versionLabel), ("@cat", PipeCategoryId));
        await ExecAsync(connection, """
            INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
            VALUES (@id, 'files/x.rvt', 'x.rvt', 2025, '2026-08-29T00:00:00Z')
            """, ("@id", fileId));
        await ExecAsync(connection, """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version,
                es_marker_version, routing_backfilled, published_at_utc)
            VALUES (@id, @itemId, @fileId, @label, 2025, 1, 1, '2026-08-29T00:00:00Z')
            """,
            ("@id", versionId), ("@itemId", itemId), ("@fileId", fileId), ("@label", versionLabel));

        foreach (var (typeName, sort) in new[] { ("Type A", 0), ("Type B", 1) })
        {
            await ExecAsync(connection, """
                INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, family_name, family_key)
                VALUES (@id, @itemId, @name, @sort, @vid, 'Pipe Types', 'Single')
                """,
                ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId),
                ("@name", typeName), ("@sort", sort), ("@vid", versionId));
        }

        // V34 rows mirror the snapshot (legacy fallback source).
        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        foreach (var type in BuildSnapshot(typeARouting).Types)
        {
            RoutingRuleRecordMapper.ToRecords(type, rules, settings);
        }
        var repo = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());
        await repo.ReplaceForVersionAsync(itemId, versionId, rules, settings);
        if (seedItemTables)
        {
            await repo.ReplaceForItemAsync(itemId, rules, settings);
        }
        return (itemId, versionId);
    }

    private static async Task<string> SeedLoadableFittingAsync(
        TempCatalogFixture fixture, string name, int revitCategoryId, int? partType, params string[] typeNames)
    {
        var itemId = Guid.NewGuid().ToString();
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        await ExecAsync(connection, """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, current_version_label,
                family_source, revit_category_id, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, 'Active', 'v1', 'loadable', @cat,
                '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z')
            """,
            ("@id", itemId), ("@name", name),
            ("@norm", FamilyNameNormalizer.Normalize(name)), ("@cat", revitCategoryId));
        await ExecAsync(connection, """
            INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
            VALUES (@id, 'files/f.rfa', 'f.rfa', 2025, '2026-08-29T00:00:00Z')
            """, ("@id", fileId));
        await ExecAsync(connection, """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
            VALUES (@id, @itemId, @fileId, 'v1', 2025, '2026-08-29T00:00:00Z')
            """, ("@id", versionId), ("@itemId", itemId), ("@fileId", fileId));
        if (partType is int ordinal)
        {
            await ExecAsync(connection, """
                INSERT INTO family_facts (catalog_item_id, fact_key, value_key, value_display)
                VALUES (@id, 'part_type', @key, 'Elbow')
                """,
                ("@id", itemId),
                ("@key", ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var sort = 0;
        foreach (var typeName in typeNames)
        {
            await ExecAsync(connection, """
                INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id)
                VALUES (@id, @itemId, @name, @sort, @vid)
                """,
                ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId),
                ("@name", typeName), ("@sort", sort++), ("@vid", versionId));
        }
        return itemId;
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.Add(new SqliteParameter(name, value));
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static CatalogRoutingEditorService CreateSut(TempCatalogFixture fixture)
        => new(fixture.GetDatabase(),
            new LocalFamilyRoutingRuleRepository(fixture.GetDatabase()),
            fixture.GetProvider(),
            new LocalSegmentSizeRepository(fixture.GetDatabase()));

    [Fact]
    public async Task Save_EditsInPlace_NoVersionCreated_LinksRegenerated()
    {
        using var fixture = new TempCatalogFixture();
        var (itemId, versionId) = await SeedSystemItemAsync(fixture, "pipes-1", "v1", TypeARouting("ElbowOld:DN50"));
        var newFittingId = await SeedLoadableFittingAsync(
            fixture, "ElbowNew", RoutingGroupCatalog.PipeFittingCategoryId, 5, "DN50");
        await SeedLoadableFittingAsync(fixture, "ElbowOld", RoutingGroupCatalog.PipeFittingCategoryId, 5, "DN50", "DN32");

        // Pre-existing links of v1: one routing (to the old elbow) + one shared_nested.
        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            var oldFittingId = await ScalarAsync(connection,
                "SELECT id FROM catalog_items WHERE name = 'ElbowOld'");
            await ExecAsync(connection, """
                INSERT INTO family_dependencies
                    (parent_catalog_item_id, parent_version_id, child_catalog_item_id, dependency_kind, part_name, ordinal, child_version_label)
                VALUES (@p, @v, @c, 'routing', 'ElbowOld:DN50', 0, 'v1'),
                       (@p, @v, @c2, 'shared_nested', NULL, 1, 'v1')
                """,
                ("@p", itemId), ("@v", versionId),
                ("@c", oldFittingId!), ("@c2", newFittingId));
        }

        var sut = CreateSut(fixture);
        var editedRules = new List<FamilyRoutingRuleInfo>();
        var editedSettings = new List<FamilyRoutingTypeSettings>();
        RoutingRuleRecordMapper.ToRecords(
            BuildSnapshot(TypeARouting("ElbowNew:DN50")).Types[0], editedRules, editedSettings);
        var result = await sut.SaveAsync(itemId, new RoutingEditorSave(
        [
            new RoutingEditorTypeSave("Type A", "Single", 0, editedRules),
        ]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.ArchivedLockedParts);

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();

        // World B: NO version is created; the item pointer is untouched.
        var versionCount = await ScalarAsync(verify,
            "SELECT COUNT(*) FROM catalog_versions WHERE catalog_item_id = @id", ("@id", itemId));
        Assert.Equal("1", versionCount);
        var pointer = await ScalarAsync(verify,
            "SELECT current_version_label FROM catalog_items WHERE id = @id", ("@id", itemId));
        Assert.Equal("v1", pointer);

        // Item-level links: edited Type A + untouched Type B.
        var rules = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());
        var (itemRules, itemSettings) = await rules.ReadForItemAsync(itemId);
        Assert.Contains(itemRules, r => r.TypeName == "Type A" && r.PartName == "ElbowNew:DN50");
        Assert.Contains(itemRules, r => r.TypeName == "Type B" && r.PartName == "ElbowOld:DN32");
        Assert.Equal(2, itemSettings.Count);

        // Links of the current version: shared_nested carried over; routing
        // link now points at the new elbow only.
        var links = await ScalarAsync(verify,
            "SELECT group_concat(dependency_kind || ':' || COALESCE(part_name, ''), ';') FROM family_dependencies WHERE parent_version_id = @v",
            ("@v", versionId));
        Assert.Contains("shared_nested:", links);
        Assert.Contains("routing:ElbowNew:DN50", links);
        Assert.DoesNotContain("ElbowOld:DN50", links);
    }

    [Fact]
    public async Task Save_RemovedPart_UsedByArchivedVersion_ReportedAsLocked()
    {
        using var fixture = new TempCatalogFixture();
        // Archived v1 uses ElbowOld; current v2 also uses it; the edit removes it.
        var (itemId, v1Id) = await SeedSystemItemAsync(fixture, "pipes-3", "v1", TypeARouting("ElbowOld:DN50"));
        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            // Promote a second current version v2 (same content) so v1 becomes archived.
            await ExecAsync(connection, """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version,
                    es_marker_version, routing_backfilled, published_at_utc)
                SELECT 'v2-id', catalog_item_id, file_id, 'v2', revit_major_version,
                    es_marker_version, routing_backfilled, '2026-08-30T00:00:00Z'
                FROM catalog_versions WHERE catalog_item_id = @id AND version_label = 'v1'
                """, ("@id", itemId));
            await ExecAsync(connection,
                "UPDATE catalog_items SET current_version_label = 'v2' WHERE id = @id", ("@id", itemId));
            await ExecAsync(connection, """
                INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, family_name, family_key)
                SELECT lower(hex(randomblob(16))), catalog_item_id, type_name, sort_order, 'v2-id', family_name, family_key
                FROM family_types WHERE catalog_item_id = @id AND version_id != 'v2-id'
                """, ("@id", itemId));
            var repo = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());
            var (rules, settings) = await repo.ReadForVersionAsync(itemId, v1Id);
            // The current version's Type B must NOT use ElbowOld — otherwise
            // the family is not "removed" by the Type A edit at all.
            rules = rules
                .Select(r => r.PartName == "ElbowOld:DN32" ? r with { PartName = "OtherElbow:DN32" } : r)
                .ToList();
            await repo.ReplaceForVersionAsync(itemId, "v2-id", rules, settings);
            await repo.ReplaceForItemAsync(itemId, rules, settings);
        }

        var sut = CreateSut(fixture);
        // Edit Type A: replace the elbow with no-part («Нет»).
        var editedRules = new List<FamilyRoutingRuleInfo>
        {
            new("Type A", "Single", "Segments", 0, "Seg A", "", []),
            new("Type A", "Single", "Elbows", 0, null, "", []),
        };
        var result = await sut.SaveAsync(itemId, new RoutingEditorSave(
        [
            new RoutingEditorTypeSave("Type A", "Single", 0, editedRules),
        ]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["ElbowOld"], result.ArchivedLockedParts);
    }

    [Fact]
    public async Task GetPartCandidates_FiltersByCategoryAndPartType_WithTypes()
    {
        using var fixture = new TempCatalogFixture();
        await SeedLoadableFittingAsync(fixture, "ElbowA", RoutingGroupCatalog.PipeFittingCategoryId, 5, "DN50", "DN65");
        await SeedLoadableFittingAsync(fixture, "TeeB", RoutingGroupCatalog.PipeFittingCategoryId, 6, "DN50");
        await SeedLoadableFittingAsync(fixture, "DuctElbowC", RoutingGroupCatalog.DuctFittingCategoryId, 5, "100");
        await SeedLoadableFittingAsync(fixture, "LegacyNoFactD", RoutingGroupCatalog.PipeFittingCategoryId, null, "DN80");

        var sut = CreateSut(fixture);
        var candidates = await sut.GetPartCandidatesAsync(
            RoutingGroupCatalog.PipeFittingCategoryId, [5]);

        var candidate = Assert.Single(candidates);
        Assert.Equal("ElbowA", candidate.FamilyName);
        Assert.Equal(["DN50", "DN65"], candidate.TypeNames);
        Assert.NotNull(candidate.PartTypeLabel);
    }

    [Fact]
    public async Task GetPartCandidates_FamilyWithoutTypes_OffersVirtualFamilyNameType()
    {
        using var fixture = new TempCatalogFixture();
        // A loadable family whose developer never named a type: Revit loads
        // it with a default type named after the family — the picker must
        // offer that virtual type so routing can reference the family.
        await SeedLoadableFittingAsync(fixture, "NoTypesElbow", RoutingGroupCatalog.PipeFittingCategoryId, 5);

        var sut = CreateSut(fixture);
        var candidates = await sut.GetPartCandidatesAsync(
            RoutingGroupCatalog.PipeFittingCategoryId, [5]);

        var candidate = Assert.Single(candidates);
        Assert.Equal("NoTypesElbow", candidate.FamilyName);
        Assert.Equal(["NoTypesElbow"], candidate.TypeNames);
    }

    [Fact]
    public async Task Load_ReturnsTypesRulesAndPresenceFlags()
    {
        using var fixture = new TempCatalogFixture();
        var (itemId, _) = await SeedSystemItemAsync(fixture, "pipes-4", "v1", TypeARouting("MissingFam:DN50"));
        var sut = CreateSut(fixture);

        var data = await sut.LoadAsync(itemId);

        Assert.NotNull(data);
        Assert.Equal(PipeCategoryId, data!.HostCategoryId);
        Assert.Equal(2, data.Types.Count);
        Assert.All(data.Types, t => Assert.True(t.WithFittings));
        Assert.NotEmpty(data.Rules);
        Assert.Equal(["ElbowOld", "MissingFam"], data.MissingPartFamilies.OrderBy(f => f));
    }

    [Fact]
    public async Task Load_ItemTablesEmpty_FallsBackToCurrentVersionV34()
    {
        using var fixture = new TempCatalogFixture();
        var (itemId, _) = await SeedSystemItemAsync(fixture, "pipes-5", "v1",
            TypeARouting("ElbowOld:DN50"), seedItemTables: false);
        var sut = CreateSut(fixture);

        var data = await sut.LoadAsync(itemId);

        Assert.NotNull(data);
        Assert.NotEmpty(data!.Rules);
        Assert.Contains(data.Rules, r => r.TypeName == "Type A" && r.PartName == "ElbowOld:DN50");
    }

    [Fact]
    public async Task Load_NonSystemItem_ReturnsNull()
    {
        using var fixture = new TempCatalogFixture();
        await SeedLoadableFittingAsync(fixture, "ElbowA", RoutingGroupCatalog.PipeFittingCategoryId, 5, "DN50");
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        var loadableId = await ScalarAsync(connection, "SELECT id FROM catalog_items WHERE name = 'ElbowA'");

        var sut = CreateSut(fixture);
        Assert.Null(await sut.LoadAsync(loadableId!));
    }

    [Fact]
    public async Task Load_ReturnsSegmentNominalsAndBounds_ForDropdownsAndSegmentRow()
    {
        using var fixture = new TempCatalogFixture();
        var (itemId, versionId) = await SeedSystemItemAsync(fixture, "pipes-7", "v1", TypeARouting("E:X"));
        await new LocalSegmentSizeRepository(fixture.GetDatabase()).ReplaceForVersionAsync(versionId,
        [
            new SegmentSizeRecord("Seg A", 50.0 / 304.8, 0, 0, true, true, 0),
            new SegmentSizeRecord("Seg A", 100.0 / 304.8, 0, 0, true, true, 1),
            new SegmentSizeRecord("Seg B", 50.0 / 304.8, 0, 0, true, true, 0),
        ]);

        var sut = CreateSut(fixture);
        var data = await sut.LoadAsync(itemId);

        Assert.NotNull(data);
        Assert.Equal([50.0 / 304.8, 100.0 / 304.8], data!.SizeNominalsFeet);
        Assert.Equal(2, data.SegmentBounds.Count);
        var segA = data.SegmentBounds.Single(b => b.SegmentName == "Seg A");
        Assert.Equal(50.0 / 304.8, segA.MinNominalFeet);
        Assert.Equal(100.0 / 304.8, segA.MaxNominalFeet);
    }

    private static async Task<string?> ScalarAsync(
        SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.Add(new SqliteParameter(name, value));
        }
        return Convert.ToString(await cmd.ExecuteScalarAsync());
    }
}
