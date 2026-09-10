using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// End-to-end coverage for the #87 advanced attribute search: real SQLite
/// catalog, SearchAsync with FamilyCatalogQuery.AttributeFilters — the exact
/// path the FamilyManager tree uses. Attribute values are matched on the
/// item's ACTIVE version (current_version_label), with an attribute_id OR
/// parameter_name fallback, mirroring the properties dialog.
/// </summary>
public sealed class LocalCatalogProviderAdvancedSearchTests : IDisposable
{
    private const string AttrId = "attr-mfr";
    private const string AttrName = "Manufacturer";

    private readonly TempCatalogFixture _fixture;
    private readonly LocalCatalogProvider _provider;

    public LocalCatalogProviderAdvancedSearchTests()
    {
        _fixture = new TempCatalogFixture();
        _provider = _fixture.GetProvider();
        SeedAttributeDefinitionAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<List<string>> SearchNamesAsync(
        string? searchText = null,
        IReadOnlyList<AttributeFilterCondition>? attributeFilters = null,
        string? categoryFilter = null)
    {
        var query = new FamilyCatalogQuery(
            SearchText: searchText, CategoryFilter: categoryFilter, StatusFilter: null,
            Tags: null, Sort: FamilyCatalogSort.NameAsc, Offset: 0, Limit: int.MaxValue,
            AttributeFilters: attributeFilters);
        var results = await _provider.SearchAsync(query);
        return results.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private async Task SeedAttributeDefinitionAsync()
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attribute_definitions (id, name, is_active, created_at_utc)
            VALUES (@id, @name, 1, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", AttrId));
        cmd.Parameters.Add(new SqliteParameter("@name", AttrName));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds an item with a terminal import run and one Manufacturer value
    /// on its ACTIVE version. attributeId null → matched via parameter_name.</summary>
    private async Task SeedItemWithValueAsync(
        string name, string valueText, double? valueNumber = null, string? attributeId = AttrId)
    {
        var item = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, name);
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, item.ItemId, item.VersionId, item.FileId);
        await SeedValueAsync(item.ItemId, item.VersionId, item.FileId, runId, attributeId, AttrName, valueText, valueNumber);
    }

    /// <summary>Seeds an item whose value lives ONLY on an inactive version —
    /// the filter must not see it (active-version semantics).</summary>
    private async Task<string> SeedItemWithValueOnInactiveVersionAsync(string name, string valueText)
    {
        var item = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, name);
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, item.ItemId, item.VersionId, item.FileId);

        var inactiveVersionId = Guid.NewGuid().ToString();
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, 'v2', 2024, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", inactiveVersionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.ItemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", item.FileId));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        await SeedValueAsync(item.ItemId, inactiveVersionId, item.FileId, runId, AttrId, AttrName, valueText, null);
        return item.ItemId;
    }

    private async Task SeedItemWithoutValuesAsync(string name)
    {
        var item = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, name);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, item.ItemId, item.VersionId, item.FileId);
    }

    private async Task SeedValueAsync(
        string itemId, string? versionId, string fileId, string runId,
        string? attributeId, string parameterName, string valueText, double? valueNumber)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO extracted_attribute_values
                (id, catalog_item_id, version_id, file_id, attribute_id, parameter_name,
                 storage_type, value_text, value_number, status, extraction_run_id, extracted_at_utc)
            VALUES (@id, @itemId, @versionId, @fileId, @attributeId, @paramName,
                    'String', @valueText, @valueNumber, 'Found', @runId, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", (object?)versionId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new SqliteParameter("@attributeId", (object?)attributeId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@paramName", parameterName));
        cmd.Parameters.Add(new SqliteParameter("@valueText", valueText));
        cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)valueNumber ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@runId", runId));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private static AttributeFilterCondition Condition(ValidationRuleOperator op, string? value = null) =>
        new(AssignmentConditionSourceKind.Attribute, AttrId, AttrName, null, op,
            op is ValidationRuleOperator.HasValue or ValidationRuleOperator.IsEmpty ? null : value);

    private static AttributeFilterCondition SystemCondition(AssignmentSystemField field, ValidationRuleOperator op, string? value = null) =>
        new(AssignmentConditionSourceKind.System, null, null, field, op,
            AttributeFilterCondition.RequiresValue(op) ? value : null);

    [Fact]
    public async Task SearchAsync_Equals_MatchesActiveVersionAndNameFallback()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithValueAsync("ValveB", "Vendor Y");
        await SeedItemWithValueAsync("ValveE", "Vendor X", attributeId: null);
        await SeedItemWithValueOnInactiveVersionAsync("ValveD", "Vendor X");

        var names = await SearchNamesAsync(attributeFilters: [Condition(ValidationRuleOperator.Equals, "Vendor X")]);

        // ValveE matches via parameter_name (attribute_id IS NULL fallback);
        // ValveD's value lives on the inactive v2 and must NOT match.
        Assert.Equal(["ValveA", "ValveE"], names);
    }

    [Fact]
    public async Task SearchAsync_NotEquals_IsExactComplementOfEquals()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithValueAsync("ValveB", "Vendor Y");
        await SeedItemWithoutValuesAsync("ValveC");

        var names = await SearchNamesAsync(attributeFilters: [Condition(ValidationRuleOperator.NotEquals, "Vendor X")]);

        // No value at all also counts as "not equals Vendor X".
        Assert.Equal(["ValveB", "ValveC"], names);
    }

    [Fact]
    public async Task SearchAsync_Contains_MatchesSubstring()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X extra");
        await SeedItemWithValueAsync("ValveB", "Another Corp");
        await SeedItemWithoutValuesAsync("ValveC");

        var names = await SearchNamesAsync(attributeFilters: [Condition(ValidationRuleOperator.Contains, "vendor")]);

        Assert.Equal(["ValveA"], names);
    }

    [Fact]
    public async Task SearchAsync_HasValue_MatchesOnlyItemsWithValue()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithoutValuesAsync("ValveC");
        await SeedItemWithValueOnInactiveVersionAsync("ValveD", "Vendor X");

        var names = await SearchNamesAsync(attributeFilters: [Condition(ValidationRuleOperator.HasValue)]);

        Assert.Equal(["ValveA"], names);
    }

    [Fact]
    public async Task SearchAsync_IsEmpty_MatchesItemsWithoutValue()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithoutValuesAsync("ValveC");
        await SeedItemWithValueOnInactiveVersionAsync("ValveD", "Vendor X");

        var names = await SearchNamesAsync(attributeFilters: [Condition(ValidationRuleOperator.IsEmpty)]);

        Assert.Equal(["ValveC", "ValveD"], names);
    }

    [Fact]
    public async Task SearchAsync_EqualsNumber_MatchesValueNumberColumn()
    {
        // Display text ≠ query; only the numeric column carries 50.
        await SeedItemWithValueAsync("FlangeN", "50 mm", valueNumber: 50.0);
        await SeedItemWithValueAsync("ValveB", "Vendor Y");

        var names = await SearchNamesAsync(attributeFilters: [Condition(ValidationRuleOperator.Equals, "50")]);

        Assert.Equal(["FlangeN"], names);
    }

    [Fact]
    public async Task SearchAsync_TwoConditions_CombinedWithAnd()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithValueAsync("ValveB", "Vendor Y");

        var names = await SearchNamesAsync(attributeFilters:
        [
            Condition(ValidationRuleOperator.Equals, "Vendor X"),
            Condition(ValidationRuleOperator.HasValue),
        ]);

        Assert.Equal(["ValveA"], names);
    }

    [Fact]
    public async Task SearchAsync_CombinesWithTextSearchWithAnd()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithValueAsync("FlangeX", "Vendor X");

        var names = await SearchNamesAsync(
            searchText: "valve",
            attributeFilters: [Condition(ValidationRuleOperator.Equals, "Vendor X")]);

        Assert.Equal(["ValveA"], names);
    }

    [Fact]
    public async Task GetDistinctValueTextsAsync_ReturnsSortedDistinctFoundValues()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor B");
        await SeedItemWithValueAsync("ValveB", "Vendor A");
        await SeedItemWithValueAsync("ValveC2", "Vendor B");
        await SeedItemWithoutValuesAsync("ValveC");

        var values = await _fixture.GetValueRepository()
            .GetDistinctValueTextsAsync(AttrId, AttrName);

        Assert.Equal(["Vendor A", "Vendor B"], values);
    }

    [Fact]
    public async Task GetDistinctValueTextsAsync_NameFallback_MatchesRowsWithoutAttributeId()
    {
        await SeedItemWithValueAsync("ValveE", "Legacy Value", attributeId: null);

        var values = await _fixture.GetValueRepository()
            .GetDistinctValueTextsAsync(AttrId, AttrName);

        Assert.Equal(["Legacy Value"], values);
    }

    // ── system-field conditions ─────────────────────────────────────

    [Fact]
    public async Task SearchAsync_FamilyNameContains_MatchesNameSubstring()
    {
        await SeedItemWithValueAsync("ValveA", "Vendor X");
        await SeedItemWithValueAsync("FlangeF", "Vendor X");

        var names = await SearchNamesAsync(attributeFilters:
            [SystemCondition(AssignmentSystemField.FamilyName, ValidationRuleOperator.Contains, "valve")]);

        Assert.Equal(["ValveA"], names);
    }

    [Fact]
    public async Task SearchAsync_RevitCategoryEquals_MatchesOrdinal()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FittingA", revitCategoryId: -2008049);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FittingB", revitCategoryId: 12345);

        var names = await SearchNamesAsync(attributeFilters:
            [SystemCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049")]);

        Assert.Equal(["FittingA"], names);
    }

    [Fact]
    public async Task SearchAsync_RevitCategoryIsEmpty_MatchesItemsWithoutCategory()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FittingA", revitCategoryId: -2008049);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "NoCatN");

        var names = await SearchNamesAsync(attributeFilters:
            [SystemCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.IsEmpty)]);

        Assert.Equal(["NoCatN"], names);
    }

    [Fact]
    public async Task SearchAsync_PartTypeEquals_MatchesFact()
    {
        var tee = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TeeFitting");
        var elbow = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "ElbowFitting");
        await CatalogSeedHelper.SeedFactAsync(_fixture, tee.ItemId, "part_type", "3", "Тройник");

        var names = await SearchNamesAsync(attributeFilters:
            [SystemCondition(AssignmentSystemField.PartType, ValidationRuleOperator.Equals, "3")]);

        Assert.Equal(["TeeFitting"], names);
    }

    [Fact]
    public async Task SearchAsync_PartTypeNotEquals_IncludesItemsWithoutFact()
    {
        var tee = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TeeFitting");
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "ElbowFitting");
        await CatalogSeedHelper.SeedFactAsync(_fixture, tee.ItemId, "part_type", "3", "Тройник");

        var names = await SearchNamesAsync(attributeFilters:
            [SystemCondition(AssignmentSystemField.PartType, ValidationRuleOperator.NotEquals, "3")]);

        // Elbow has no part_type fact at all — counts as "not 3".
        Assert.Equal(["ElbowFitting"], names);
    }
}
