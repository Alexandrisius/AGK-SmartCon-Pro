using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
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
        using var connection = fixture.GetDatabase().CreateConnection();
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
        var query = new FamilyCatalogQuery(null, null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
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

        var query = new FamilyCatalogQuery("pipe", null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
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
    public async Task SearchAsync_WithTags_PreservesRevitCategoryId()
    {
        // #187: the tags enrichment in SearchAsync rebuilt FamilyCatalogItem
        // WITHOUT RevitCategoryId — every search returned null, silently
        // breaking presence badges and the batch stale check for system items.
        using var fixture = await CreateAndMigrate();
        await SeedSystemItemWithRevitCategoryAsync(fixture, "sys1", "Стены", tags: new[] { "mep" });

        var query = new FamilyCatalogQuery(null, null, null, null, FamilyCatalogSort.NameAsc, 0, 50);
        var results = await fixture.GetProvider().SearchAsync(query);

        var item = Assert.Single(results);
        Assert.Equal(-2000011, item.RevitCategoryId);
        Assert.Equal("system", item.FamilySource);
        Assert.Contains("mep", item.Tags);
    }

    private static async Task SeedSystemItemWithRevitCategoryAsync(
        TempCatalogFixture fixture, string id, string name, string[] tags)
    {
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, family_source, revit_category, revit_category_id, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, 'Active', 'system', 'Стены', -2000011, @now, @now)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@norm", name.ToLowerInvariant()));
        cmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();

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

    [Fact]
    public async Task GetItemAsync_WithTags_PreservesRevitCategoryId()
    {
        // Manual test 2026-08-04 (round 4): GetItemAsync rebuilt the record
        // field-by-field like SearchAsync did before #187 — and dropped
        // RevitCategoryId (+ Active/MinRevitMajorVersion). Every consumer
        // resolving the category from GetItemAsync (sync orchestrator,
        // placement, stale detector) silently fell back to an unscoped
        // name match — the wire sync bound a settings object and wrote
        // params + the ES marker to the wrong element.
        using var fixture = await CreateAndMigrate();
        await SeedSystemItemWithRevitCategoryAsync(fixture, "sys1", "Стены", tags: new[] { "mep" });

        var item = await fixture.GetProvider().GetItemAsync("sys1");

        Assert.NotNull(item);
        Assert.Equal(-2000011, item.RevitCategoryId);
        Assert.Equal("system", item.FamilySource);
        Assert.Contains("mep", item.Tags);
    }

    [Fact]
    public async Task FindByRevitCategoryIdAsync_Found_ReturnsItem()
    {
        using var fixture = await CreateAndMigrate();
        await SeedSystemItemWithRevitCategoryAsync(fixture, "sys1", "Стены", tags: []);

        var item = await fixture.GetProvider().FindByRevitCategoryIdAsync(-2000011, "system");

        Assert.NotNull(item);
        Assert.Equal("sys1", item.Id);
        Assert.Equal(-2000011, item.RevitCategoryId);
    }

    [Fact]
    public async Task FindByRevitCategoryIdAsync_NotFound_ReturnsNull()
    {
        using var fixture = await CreateAndMigrate();

        var item = await fixture.GetProvider().FindByRevitCategoryIdAsync(-2000011, "system");

        Assert.Null(item);
    }

    [Fact]
    public async Task FindByRevitCategoryIdAsync_CrossSourceSeparation_ReturnsNull()
    {
        // #192: a system-category id must never resolve to a loadable row
        // and vice versa (same separation as the hash search).
        using var fixture = await CreateAndMigrate();
        await SeedSystemItemWithRevitCategoryAsync(fixture, "sys1", "Стены", tags: []);

        var item = await fixture.GetProvider().FindByRevitCategoryIdAsync(-2000011, "loadable");

        Assert.Null(item);
    }

    [Fact]
    public async Task FindByNormalizedNameAsync_SourceFilter_ReturnsItemOfThatSource()
    {
        // #201: the same normalized name exists in both sources — the
        // source filter must pick the row of the requested source.
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "load1", "Трубы", "трубы");
        await SeedSystemItemWithRevitCategoryAsync(fixture, "sys1", "Трубы", tags: []);

        var item = await fixture.GetProvider().FindByNormalizedNameAsync("трубы", "system");

        Assert.NotNull(item);
        Assert.Equal("sys1", item.Id);
        Assert.Equal("system", item.FamilySource);
    }

    [Fact]
    public async Task FindByNormalizedNameAsync_CrossSource_ReturnsNull()
    {
        // #201: a system query must never match a loadable row with the
        // same name (and vice versa).
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "load1", "Трубы", "трубы");

        var item = await fixture.GetProvider().FindByNormalizedNameAsync("трубы", "system");

        Assert.Null(item);
    }

    [Fact]
    public async Task FindByNormalizedNameAsync_NullSource_MatchesAnySource()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "load1", "Трубы", "трубы");

        var item = await fixture.GetProvider().FindByNormalizedNameAsync("трубы");

        Assert.NotNull(item);
        Assert.Equal("load1", item.Id);
    }

    [Fact]
    public async Task SearchAsync_FilterByCategoryId()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "c1", "Pipe A", "pipe a");
        await SeedItemAsync(fixture, "c2", "Valve B", "valve b");

        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var catCmd = conn.CreateCommand();
        catCmd.CommandText = "INSERT INTO categories (id, name, sort_order, created_at_utc) VALUES ('cat1', 'Pipes', 0, @t)";
        catCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await catCmd.ExecuteNonQueryAsync();

        using var updCmd = conn.CreateCommand();
        updCmd.CommandText = "UPDATE catalog_items SET category_id = 'cat1' WHERE id = 'c1'";
        await updCmd.ExecuteNonQueryAsync();

        var query = new FamilyCatalogQuery(null, "cat1", null, null, FamilyCatalogSort.NameAsc, 0, 50);
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

        var query = new FamilyCatalogQuery(null, null, ContentStatus.Active, null, FamilyCatalogSort.NameAsc, 0, 50);
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

    [Fact]
    public async Task DeleteItemAsync_CascadesItemRoutingLinks()
    {
        // ADR-072 World B (V37 item_routing_*) — the item-level routing
        // links must die with their item via FK CASCADE.
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "delr", "Routing Owner", "routing owner");
        var repo = new LocalFamilyRoutingRuleRepository(fixture.GetDatabase());
        await repo.ReplaceForItemAsync("delr",
            [new FamilyRoutingRuleInfo("Type A", "Single", "Elbows", 0, "A:B", "", [])],
            [new FamilyRoutingTypeSettings("Type A", "Single", 0)]);

        var deleted = await fixture.GetProvider().DeleteItemAsync("delr");
        Assert.True(deleted);
        Assert.False(await repo.HasAnyForItemAsync("delr"));
    }

    [Fact]
    public async Task DeleteItemAsync_RemovesFilesBeforeDb()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "del2", "Delete With Files", "delete with files");

        var familyDir = Path.Combine(fixture.GetDatabaseRoot(), "files", "del2");
        Directory.CreateDirectory(familyDir);
        var filePath = Path.Combine(familyDir, "preview.png");
        await File.WriteAllTextAsync(filePath, "fake image content");

        var deleted = await fixture.GetProvider().DeleteItemAsync("del2");
        Assert.True(deleted);

        var item = await fixture.GetProvider().GetItemAsync("del2");
        Assert.Null(item);
        Assert.False(Directory.Exists(familyDir), "Family directory should be deleted");
    }

    [Fact]
    public async Task DeleteItemAsync_LockedFiles_ThrowsBeforeDbDelete()
    {
        using var fixture = await CreateAndMigrate();
        await SeedItemAsync(fixture, "del3", "Locked Files", "locked files");

        var familyDir = Path.Combine(fixture.GetDatabaseRoot(), "files", "del3");
        Directory.CreateDirectory(familyDir);
        var filePath = Path.Combine(familyDir, "preview.png");
        await File.WriteAllTextAsync(filePath, "fake image content");

        // Lock the file by opening it for read
        using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.GetProvider().DeleteItemAsync("del3"));

        // Verify DB record still exists
        var item = await fixture.GetProvider().GetItemAsync("del3");
        Assert.NotNull(item);
        Assert.Equal("Locked Files", item.Name);
    }
}
