using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalAttributeValueRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAttributeValueRepository _repo;

    public LocalAttributeValueRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();
        _repo = new LocalAttributeValueRepository(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task SeedCatalogItemAsync(string id)
    {
        using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, 'Active', @t, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", id));
        cmd.Parameters.Add(new SqliteParameter("@norm", id.ToLowerInvariant()));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedAttributeDefinitionAsync(string id)
    {
        using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attribute_definitions (id, name, is_active, created_at_utc)
            VALUES (@id, @name, 1, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", id));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedImportRunAsync(string runId, string catalogItemId)
    {
        using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_data_import_runs (id, catalog_item_id, revit_major_version, status, types_count, started_at_utc)
            VALUES (@id, @itemId, 2025, 'Succeeded', 0, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", runId));
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedPrerequisitesAsync(string catalogItemId, HashSet<string> attributeIds, HashSet<string> runIds)
    {
        await SeedCatalogItemAsync(catalogItemId);
        foreach (var attrId in attributeIds)
            await SeedAttributeDefinitionAsync(attrId);
        foreach (var runId in runIds)
            await SeedImportRunAsync(runId, catalogItemId);
    }

    private static ExtractedAttributeValue CreateTestValue(
        string id, string itemId, string? versionId, string? typeId,
        string attributeId, string paramName, AttributeValueStatus status,
        string runId, string? fileId = null)
    {
        return new ExtractedAttributeValue(
            id, itemId, versionId, fileId, typeId, attributeId,
            null, paramName, AttributeScope.Type, "String", "test_value",
            null, null, null, status, null, runId, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SaveValuesAsync_SavesAndRetrievesByItem()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "type1", "attr1", "Param1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", null, "type1", "attr2", "Param2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", null, "type1", "attr3", "Param3", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var result = await _repo.GetValuesForItemAsync("item1", null);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task SaveValuesAsync_WithVersionId_FiltersByVersion()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", "v1", "type1", "attr1", "Param1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", "v1", "type1", "attr2", "Param2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", "v2", "type1", "attr3", "Param3", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var v1Result = await _repo.GetValuesForItemAsync("item1", "v1");
        Assert.Equal(2, v1Result.Count);
        Assert.All(v1Result, v => Assert.Equal("v1", v.VersionId));
    }

    [Fact]
    public async Task SaveValuesAsync_NullVersionId_FiltersCorrectly()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "type1", "attr1", "Param1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", "v1", "type1", "attr2", "Param2", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var nullVersionResult = await _repo.GetValuesForItemAsync("item1", null);
        Assert.Single(nullVersionResult);
        Assert.Null(nullVersionResult[0].VersionId);
    }

    [Fact]
    public async Task GetValuesForTypeAsync_ReturnsOnlyMatchingType()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "typeA", "attr1", "Param1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", null, "typeB", "attr2", "Param2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", null, "typeA", "attr3", "Param3", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var result = await _repo.GetValuesForTypeAsync("typeA");
        Assert.Equal(2, result.Count);
        Assert.All(result, v => Assert.Equal("typeA", v.TypeId));
    }

    [Fact]
    public async Task GetValuesForRunAsync_ReturnsOnlyMatchingRun()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3"], ["runA", "runB"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "type1", "attr1", "Param1", AttributeValueStatus.Found, "runA"),
            CreateTestValue("v2", "item1", null, "type1", "attr2", "Param2", AttributeValueStatus.Found, "runB"),
            CreateTestValue("v3", "item1", null, "type1", "attr3", "Param3", AttributeValueStatus.Found, "runA"),
        };

        await _repo.SaveValuesAsync(values);

        var result = await _repo.GetValuesForRunAsync("runA");
        Assert.Equal(2, result.Count);
        Assert.All(result, v => Assert.Equal("runA", v.ExtractionRunId));
    }

    [Fact]
    public async Task ReplaceSnapshotAsync_ReplacesOldValues()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3", "attr4", "attr5"], ["run1", "run2"]);

        var oldValues = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", "v1", "type1", "attr1", "P1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", "v1", "type1", "attr2", "P2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", "v1", "type1", "attr3", "P3", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(oldValues);

        var newValues = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v4", "item1", "v1", "type1", "attr4", "P4", AttributeValueStatus.Found, "run2"),
            CreateTestValue("v5", "item1", "v1", "type1", "attr5", "P5", AttributeValueStatus.Found, "run2"),
        };

        await _repo.ReplaceSnapshotAsync("item1", "v1", "run2", newValues);

        var result = await _repo.GetValuesForItemAsync("item1", "v1");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ReplaceSnapshotAsync_DoesNotAffectOtherVersions()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3", "attr4", "attr5"], ["run1", "run2"]);

        var v1Values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", "v1", "type1", "attr1", "P1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", "v1", "type1", "attr2", "P2", AttributeValueStatus.Found, "run1"),
        };
        var v2Values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v3", "item1", "v2", "type1", "attr3", "P3", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v4", "item1", "v2", "type1", "attr4", "P4", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(v1Values);
        await _repo.SaveValuesAsync(v2Values);

        var replacement = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v5", "item1", "v1", "type1", "attr5", "P5", AttributeValueStatus.Found, "run2"),
        };
        await _repo.ReplaceSnapshotAsync("item1", "v1", "run2", replacement);

        var v2Result = await _repo.GetValuesForItemAsync("item1", "v2");
        Assert.Equal(2, v2Result.Count);
    }

    [Fact]
    public async Task DeleteValuesForRunAsync_RemovesValues_ReturnsCount()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3", "attr4", "attr5"], ["run1", "run2"]);

        var run1Values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "type1", "attr1", "P1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", null, "type1", "attr2", "P2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", null, "type1", "attr3", "P3", AttributeValueStatus.Found, "run1"),
        };
        var run2Values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v4", "item1", null, "type1", "attr4", "P4", AttributeValueStatus.Found, "run2"),
            CreateTestValue("v5", "item1", null, "type1", "attr5", "P5", AttributeValueStatus.Found, "run2"),
        };

        await _repo.SaveValuesAsync(run1Values);
        await _repo.SaveValuesAsync(run2Values);

        var deletedCount = await _repo.DeleteValuesForRunAsync("run1");
        Assert.Equal(3, deletedCount);

        var remaining = await _repo.GetValuesForRunAsync("run2");
        Assert.Equal(2, remaining.Count);

        var deleted = await _repo.GetValuesForRunAsync("run1");
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task GetFoundCountAsync_CountsFoundStatus()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "type1", "attr1", "P1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", null, "type1", "attr2", "P2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", null, "type1", "attr3", "P3", AttributeValueStatus.MissingParameter, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var count = await _repo.GetFoundCountAsync("item1", null);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetMissingCountAsync_CountsNonFoundStatus()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2", "attr3"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", null, "type1", "attr1", "P1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", null, "type1", "attr2", "P2", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v3", "item1", null, "type1", "attr3", "P3", AttributeValueStatus.MissingParameter, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var count = await _repo.GetMissingCountAsync("item1", null);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetFoundCountAsync_WithVersionId_FiltersByVersion()
    {
        await SeedPrerequisitesAsync("item1", ["attr1", "attr2"], ["run1"]);

        var values = new List<ExtractedAttributeValue>
        {
            CreateTestValue("v1", "item1", "v1", "type1", "attr1", "P1", AttributeValueStatus.Found, "run1"),
            CreateTestValue("v2", "item1", "v2", "type1", "attr2", "P2", AttributeValueStatus.Found, "run1"),
        };

        await _repo.SaveValuesAsync(values);

        var count = await _repo.GetFoundCountAsync("item1", "v1");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SaveValuesAsync_UpsertsOnConflict()
    {
        await SeedPrerequisitesAsync("item1", ["attr1"], ["run1"]);

        var original = new List<ExtractedAttributeValue>
        {
            new ExtractedAttributeValue(
                "x", "item1", null, null, "type1", "attr1",
                null, "Param1", AttributeScope.Type, "String", "old_value",
                null, null, null, AttributeValueStatus.Found, null, "run1", DateTimeOffset.UtcNow),
        };

        await _repo.SaveValuesAsync(original);

        var updated = new List<ExtractedAttributeValue>
        {
            new ExtractedAttributeValue(
                "x", "item1", null, null, "type1", "attr1",
                null, "Param1", AttributeScope.Type, "String", "new_value",
                null, null, null, AttributeValueStatus.Found, null, "run1", DateTimeOffset.UtcNow),
        };

        await _repo.SaveValuesAsync(updated);

        var result = await _repo.GetValuesForItemAsync("item1", null);
        Assert.Single(result);
        Assert.Equal("new_value", result[0].ValueText);
    }

    [Fact]
    public async Task GetValuesForItemAsync_EmptyResult_ReturnsEmptyList()
    {
        var result = await _repo.GetValuesForItemAsync("nonexistent", null);
        Assert.Empty(result);
    }
}
