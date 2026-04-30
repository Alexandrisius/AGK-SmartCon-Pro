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
        string? category = null, string? status = "Active", string[]? tags = null)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, manufacturer, content_status, current_version_label, published_by, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @normalizedName, NULL, @category, NULL, @status, NULL, NULL, @createdAt, @updatedAt)
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
        Assert.Equal("Mechanical", item.CategoryPath);
        Assert.Equal(ContentStatus.Active, item.ContentStatus);
    }

    [Fact]
    public async Task SearchAsync_FilterByCategoryId()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "c1", "Pipe A", "pipe a");
        await SeedItemAsync(fixture, "c2", "Valve B", "valve b");

        using var conn = new SqliteConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        using var catCmd = conn.CreateCommand();
        catCmd.CommandText = "INSERT INTO categories (id, name, sort_order, created_at_utc) VALUES ('cat1', 'Pipes', 0, @t)";
        catCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await catCmd.ExecuteNonQueryAsync();

        using var updCmd = conn.CreateCommand();
        updCmd.CommandText = "UPDATE catalog_items SET category_id = 'cat1' WHERE id = 'c1'";
        await updCmd.ExecuteNonQueryAsync();

        var query = new FamilyCatalogQuery(null, "cat1", null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);

        Assert.Single(results);
        Assert.Equal("Pipe A", results[0].Name);
    }

    [Fact]
    public async Task SearchAsync_FilterByStatus()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "s1", "Active Item", "active item", null, "Active");
        await SeedItemAsync(fixture, "s2", "Deprecated Item", "deprecated item", null, "Deprecated");

        var query = new FamilyCatalogQuery(null, null, ContentStatus.Active, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);

        Assert.Single(results);
        Assert.Equal("Active Item", results[0].Name);
    }

    [Fact]
    public async Task UpdateItemAsync_UpdatesNameAndCategoryId()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "upd1", "Old Name", "old name", "Old Category");

        var updated = await fixture.GetProvider().UpdateItemAsync("upd1", "New Name", "New Desc", "cat-123", null, null);

        Assert.Equal("New Name", updated.Name);
        Assert.Equal("New Desc", updated.Description);
        Assert.Equal("cat-123", updated.CategoryId);
    }

    [Fact]
    public async Task UpdateItemAsync_ChangesStatus()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "st1", "Status Test", "status test");

        var updated = await fixture.GetProvider().UpdateItemAsync("st1", null, null, null, null, ContentStatus.Deprecated);

        Assert.Equal(ContentStatus.Deprecated, updated.ContentStatus);
    }

    [Fact]
    public async Task DeleteItemAsync_RemovesItem()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "del1", "Delete Me", "delete me");

        var deleted = await fixture.GetProvider().DeleteItemAsync("del1");
        Assert.True(deleted);

        var item = await fixture.GetProvider().GetItemAsync("del1");
        Assert.Null(item);
    }
}
