using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// <see cref="LocalFamilyRoutingRuleRepository"/> (V34, ADR-072):
/// DELETE+INSERT per version, read roundtrip with criteria JSON,
/// legacy-fallback discriminator (<c>HasRulesForVersion</c>).
/// </summary>
public sealed class LocalFamilyRoutingRuleRepositoryTests
{
    private static async Task<TempCatalogFixture> CreateAndMigrate()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        return fixture;
    }

    [Fact]
    public async Task Replace_ThenRead_ReturnsRulesAndSettings()
    {
        using var fixture = await CreateAndMigrate();
        var (itemId, versionId) = await SeedItemWithVersionAsync(fixture, "pipes-1");
        var sut = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());

        var rules = new[]
        {
            new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types", "Elbows", 0, "ElbowFam:DN50", "first",
                [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.0, 0.5)]),
            new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types", "Elbows", 1, null, "welded", []),
            new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types",
                RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM"), 0, "TeeFam:Std", "", []),
        };
        var settings = new[] { new FamilyRoutingTypeSettings("Pipe A", "Pipe.Types", 1) };

        await sut.ReplaceForVersionAsync(itemId, versionId, rules, settings);
        var (readRules, readSettings) = await sut.ReadForVersionAsync(itemId, versionId);

        Assert.Equal(3, readRules.Count);
        var first = readRules.Single(r => r.RuleOrder == 0 && r.GroupKey == "Elbows");
        Assert.Equal("ElbowFam:DN50", first.PartName);
        Assert.Equal("first", first.Description);
        var criterion = Assert.Single(first.Criteria);
        Assert.Equal("PrimarySizeCriterion", criterion.CriterionType);
        Assert.Equal(0.5, criterion.MaximumSize);

        var noPart = readRules.Single(r => r.RuleOrder == 1 && r.GroupKey == "Elbows");
        Assert.Null(noPart.PartName);
        Assert.Empty(noPart.Criteria);

        var setting = Assert.Single(readSettings);
        Assert.Equal(1, setting.PreferredJunctionType);
    }

    [Fact]
    public async Task Replace_CalledTwice_SecondReplacesFirst()
    {
        using var fixture = await CreateAndMigrate();
        var (itemId, versionId) = await SeedItemWithVersionAsync(fixture, "pipes-2");
        var sut = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(itemId, versionId,
            [new FamilyRoutingRuleInfo("Pipe A", "K", "Elbows", 0, "Old:Type", "", [])],
            [new FamilyRoutingTypeSettings("Pipe A", "K", 1)]);
        await sut.ReplaceForVersionAsync(itemId, versionId,
            [new FamilyRoutingRuleInfo("Pipe A", "K", "Junctions", 0, "New:Type", "", [])],
            [new FamilyRoutingTypeSettings("Pipe A", "K", 2)]);

        var (rules, settings) = await sut.ReadForVersionAsync(itemId, versionId);
        var rule = Assert.Single(rules);
        Assert.Equal("Junctions", rule.GroupKey);
        Assert.Equal("New:Type", rule.PartName);
        Assert.Equal(2, Assert.Single(settings).PreferredJunctionType);
    }

    [Fact]
    public async Task HasRules_EmptyVersion_ReturnsFalse_AfterReplace_True()
    {
        using var fixture = await CreateAndMigrate();
        var (itemId, versionId) = await SeedItemWithVersionAsync(fixture, "pipes-3");
        var sut = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());

        Assert.False(await sut.HasRulesForVersionAsync(itemId, versionId));

        // A legitimately rule-less routed type (flex with all «Нет») keeps
        // its settings row — presence is tracked by rows, not rule count.
        await sut.ReplaceForVersionAsync(itemId, versionId, [],
            [new FamilyRoutingTypeSettings("Flex A", "Flex.Round", 1)]);

        Assert.True(await sut.HasRulesForVersionAsync(itemId, versionId));
    }

    [Fact]
    public async Task Read_CorruptCriteriaJson_ToleratedAsEmpty()
    {
        using var fixture = await CreateAndMigrate();
        var (itemId, versionId) = await SeedItemWithVersionAsync(fixture, "pipes-4");
        var sut = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(itemId, versionId,
            [new FamilyRoutingRuleInfo("Pipe A", "K", "Elbows", 0, "E:S", "", [])],
            [new FamilyRoutingTypeSettings("Pipe A", "K", 0)]);

        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE family_routing_rules SET criteria_json = '{broken' WHERE catalog_version_id = @v";
            cmd.Parameters.Add(new SqliteParameter("@v", versionId));
            await cmd.ExecuteNonQueryAsync();
        }

        var (rules, _) = await sut.ReadForVersionAsync(itemId, versionId);
        var rule = Assert.Single(rules);
        Assert.Empty(rule.Criteria);
    }

    [Fact]
    public async Task Migration_CreatesRoutingTables()
    {
        using var fixture = await CreateAndMigrate();
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN ('family_routing_rules', 'family_routing_type_settings')
            """;
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(2, count);
    }

    private static async Task<(string ItemId, string VersionId)> SeedItemWithVersionAsync(
        TempCatalogFixture fixture, string itemId)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using (var itemCmd = connection.CreateCommand())
        {
            itemCmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, description, category_name, manufacturer, content_status, current_version_label, published_by, family_source, created_at_utc, updated_at_utc)
                VALUES (@id, 'Pipes', 'PIPES', NULL, NULL, NULL, 'Active', 'v1', NULL, 'system', '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z')
                """;
            itemCmd.Parameters.Add(new SqliteParameter("@id", itemId));
            await itemCmd.ExecuteNonQueryAsync();
        }
        using (var fileCmd = connection.CreateCommand())
        {
            fileCmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, 'files/x.rvt', 'x.rvt', 2025, '2026-08-29T00:00:00Z')
                """;
            fileCmd.Parameters.Add(new SqliteParameter("@id", fileId));
            await fileCmd.ExecuteNonQueryAsync();
        }
        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, 'v1', 2025, '2026-08-29T00:00:00Z')
                """;
            versionCmd.Parameters.Add(new SqliteParameter("@id", versionId));
            versionCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            versionCmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            await versionCmd.ExecuteNonQueryAsync();
        }
        return (itemId, versionId);
    }
}
