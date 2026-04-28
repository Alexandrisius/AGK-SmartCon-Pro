using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCatalogProviderTests
{
    private static async Task<TempCatalogFixture> CreateAndMigrate()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        return fixture;
    }

    private static async Task SeedItemAsync(TempCatalogFixture fixture, string id, string name, string normalizedName,
        string? category = null, string? status = "Draft", string[]? tags = null)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, provider_id, name, normalized_name, description, category_name, manufacturer, status, current_version_id, created_at_utc, updated_at_utc)
            VALUES (@id, 'local', @name, @normalizedName, NULL, @category, NULL, @status, NULL, @createdAt, @updatedAt)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@normalizedName", normalizedName));
        cmd.Parameters.Add(new SqliteParameter("@category", category ?? (object)DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", status));
        cmd.Parameters.Add(new SqliteParameter("@createdAt", DateTimeOffset.UtcNow.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAt", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();

        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                var normalizedTag = SmartCon.Core.Services.FamilyManager.FamilySearchNormalizer.Normalize(tag);
                using var tagCmd = connection.CreateCommand();
                tagCmd.CommandText = """
                    INSERT OR IGNORE INTO catalog_tags (catalog_item_id, tag, normalized_tag)
                    VALUES (@id, @tag, @normalizedTag)
                    """;
                tagCmd.Parameters.Add(new SqliteParameter("@id", id));
                tagCmd.Parameters.Add(new SqliteParameter("@tag", tag));
                tagCmd.Parameters.Add(new SqliteParameter("@normalizedTag", normalizedTag));
                await tagCmd.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task SearchAsync_EmptyDb_ReturnsEmptyList()
    {
        using var fixture = await CreateAndMigrate();
        var query = new FamilyCatalogQuery(null, null, null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetItemAsync_NonExistent_ReturnsNull()
    {
        using var fixture = await CreateAndMigrate();
        var item = await fixture.GetProvider().GetItemAsync("nonexistent");
        Assert.Null(item);
    }

    [Fact]
    public async Task GetItemCountAsync_EmptyDb_ReturnsZero()
    {
        using var fixture = await CreateAndMigrate();
        var count = await fixture.GetProvider().GetItemCountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AfterSeed_SearchFindsItem()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "item1", "Pipe Fitting", "pipe fitting", "Pipes");

        var query = new FamilyCatalogQuery("pipe", null, null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);

        Assert.Single(results);
        Assert.Equal("Pipe Fitting", results[0].Name);
    }

    [Fact]
    public async Task AfterSeed_GetItemReturnsItem()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "item2", "Valve", "valve", "Mechanical");

        var item = await fixture.GetProvider().GetItemAsync("item2");
        Assert.NotNull(item);
        Assert.Equal("Valve", item.Name);
        Assert.Equal("Mechanical", item.CategoryName);
    }

    [Fact]
    public async Task SearchAsync_FilterByCategory()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "c1", "Pipe A", "pipe a", "Pipes");
        await SeedItemAsync(fixture, "c2", "Valve B", "valve b", "Mechanical");

        var query = new FamilyCatalogQuery(null, "Pipes", null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);

        Assert.Single(results);
        Assert.Equal("Pipe A", results[0].Name);
    }

    [Fact]
    public async Task SearchAsync_FilterByStatus()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "s1", "Active Item", "active item", null, "Verified");
        await SeedItemAsync(fixture, "s2", "Draft Item", "draft item", null, "Draft");

        var query = new FamilyCatalogQuery(null, null, FamilyContentStatus.Verified, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);

        Assert.Single(results);
        Assert.Equal("Active Item", results[0].Name);
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersions()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "v1", "Versioned Item", "versioned item");

        using var conn = new SqliteConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        using var fc = conn.CreateCommand();
        fc.CommandText = """
            INSERT INTO family_files (id, original_path, cached_path, file_name, size_bytes, sha256, last_write_time_utc, storage_mode)
            VALUES ('file1', NULL, NULL, 'test.rfa', 100, 'abc123', NULL, 'Cached')
            """;
        await fc.ExecuteNonQueryAsync();

        using var vc = conn.CreateCommand();
        vc.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, sha256, revit_major_version, types_count, parameters_count, imported_at_utc)
            VALUES ('ver1', 'v1', 'file1', 'v1', 'abc123', NULL, NULL, NULL, @importedAt)
            """;
        vc.Parameters.Add(new SqliteParameter("@importedAt", DateTimeOffset.UtcNow.ToString("o")));
        await vc.ExecuteNonQueryAsync();

        var versions = await fixture.GetProvider().GetVersionsAsync("v1");
        Assert.Single(versions);
        Assert.Equal("v1", versions[0].VersionLabel);
        Assert.Equal("abc123", versions[0].Sha256);
    }

    [Fact]
    public async Task UpdateItemAsync_UpdatesNameAndCategory()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "upd1", "Old Name", "old name", "Old Category");

        var updated = await fixture.GetProvider().UpdateItemAsync("upd1", "New Name", "New Desc", "New Category", null, null);

        Assert.Equal("New Name", updated.Name);
        Assert.Equal("New Desc", updated.Description);
        Assert.Equal("New Category", updated.CategoryName);
    }
}
