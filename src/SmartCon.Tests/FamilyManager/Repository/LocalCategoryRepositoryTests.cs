using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCategoryRepositoryTests
{
    private static async Task<TempCatalogFixture> CreateAndMigrate()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        return fixture;
    }

    [Fact]
    public async Task GetAllAsync_EmptyDb_ReturnsEmptyList()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var result = await repo.GetAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task AddAsync_RootCategory_ReturnsWithCorrectFullPath()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var category = await repo.AddAsync("Pipes", null, 0);

        Assert.Equal("Pipes", category.Name);
        Assert.Null(category.ParentId);
        Assert.Equal("Pipes", category.FullPath);
    }

    [Fact]
    public async Task AddAsync_RootCategory_AppearsInGetAll()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        await repo.AddAsync("Pipes", null, 0);
        var all = await repo.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("Pipes", all[0].Name);
        Assert.Equal("Pipes", all[0].FullPath);
    }

    [Fact]
    public async Task AddAsync_ChildCategory_HasCorrectParentId()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var parent = await repo.AddAsync("Pipes", null, 0);
        var child = await repo.AddAsync("Copper", parent.Id, 1);

        Assert.Equal(parent.Id, child.ParentId);
    }

    [Fact]
    public async Task AddAsync_ChildCategory_FullPathIncludesParent()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var parent = await repo.AddAsync("Pipes", null, 0);
        var child = await repo.AddAsync("Copper", parent.Id, 1);

        Assert.Equal("Pipes > Copper", child.FullPath);
    }

    [Fact]
    public async Task GetByIdAsync_Existing_ReturnsCategory()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var added = await repo.AddAsync("Pipes", null, 0);
        var result = await repo.GetByIdAsync(added.Id);

        Assert.NotNull(result);
        Assert.Equal(added.Id, result.Id);
        Assert.Equal("Pipes", result.Name);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var result = await repo.GetByIdAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndFullPath()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var added = await repo.AddAsync("Pipes", null, 0);
        var renamed = await repo.RenameAsync(added.Id, "Tubes");

        Assert.NotNull(renamed);
        Assert.Equal("Tubes", renamed.Name);
        Assert.Equal("Tubes", renamed.FullPath);
    }

    [Fact]
    public async Task RenameAsync_NonExistent_ReturnsNull()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var result = await repo.RenameAsync("nonexistent", "NewName");

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCategory()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var added = await repo.AddAsync("Pipes", null, 0);
        var deleted = await repo.DeleteAsync(added.Id);

        Assert.True(deleted);
        var all = await repo.GetAllAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_ReturnsFalse()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var deleted = await repo.DeleteAsync("nonexistent");

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteAsync_ParentWithChild_CascadesToChildren()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var parent = await repo.AddAsync("Pipes", null, 0);
        var child = await repo.AddAsync("Copper", parent.Id, 1);

        var deleted = await repo.DeleteAsync(parent.Id);

        Assert.True(deleted);
        Assert.Null(await repo.GetByIdAsync(parent.Id));
        Assert.Null(await repo.GetByIdAsync(child.Id));
    }

    [Fact]
    public async Task DeleteAsync_WithCatalogItems_ResetsCategoryId()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var category = await repo.AddAsync("Pipes", null, 0);

        using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, category_id, content_status, created_at_utc, updated_at_utc)
                VALUES ('item1', 'Pipe A', 'pipe a', @catId, 'Active', @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@catId", category.Id));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        await repo.DeleteAsync(category.Id);

        using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT category_id FROM catalog_items WHERE id = 'item1'";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal(DBNull.Value, result);
        }
    }

    [Fact]
    public async Task MoveAsync_ChangesParent()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var cat1 = await repo.AddAsync("Pipes", null, 0);
        var cat2 = await repo.AddAsync("Fittings", null, 1);
        var child = await repo.AddAsync("Copper", cat1.Id, 2);

        var moved = await repo.MoveAsync(child.Id, cat2.Id, 0);

        Assert.NotNull(moved);
        Assert.Equal(cat2.Id, moved.ParentId);
        Assert.Equal("Fittings > Copper", moved.FullPath);
    }

    [Fact]
    public async Task MoveAsync_ToRoot_ParentIdIsNull()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var parent = await repo.AddAsync("Pipes", null, 0);
        var child = await repo.AddAsync("Copper", parent.Id, 1);

        var moved = await repo.MoveAsync(child.Id, null, 5);

        Assert.NotNull(moved);
        Assert.Null(moved.ParentId);
        Assert.Equal("Copper", moved.FullPath);
        Assert.Equal(5, moved.SortOrder);
    }

    [Fact]
    public async Task MoveAsync_NonExistent_ReturnsNull()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var result = await repo.MoveAsync("nonexistent", null, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReorderAsync_UpdatesSortOrder()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var cat1 = await repo.AddAsync("A", null, 0);
        var cat2 = await repo.AddAsync("B", null, 1);
        var cat3 = await repo.AddAsync("C", null, 2);

        await repo.ReorderAsync([(cat3.Id, 0), (cat1.Id, 1), (cat2.Id, 2)]);

        var all = await repo.GetAllAsync();
        Assert.Equal(cat3.Id, all[0].Id);
        Assert.Equal(0, all[0].SortOrder);
        Assert.Equal(cat1.Id, all[1].Id);
        Assert.Equal(1, all[1].SortOrder);
        Assert.Equal(cat2.Id, all[2].Id);
        Assert.Equal(2, all[2].SortOrder);
    }

    [Fact]
    public async Task ReplaceAllAsync_ReplacesAllCategories()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        await repo.AddAsync("Old1", null, 0);
        await repo.AddAsync("Old2", null, 1);

        var newCategories = new List<CategoryNode>
        {
            new("n1", "New1", null, 0, "New1", DateTimeOffset.UtcNow),
            new("n2", "New2", "n1", 1, "New1 > New2", DateTimeOffset.UtcNow),
        };

        await repo.ReplaceAllAsync(newCategories);

        var all = await repo.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("New1", all[0].Name);
        Assert.Equal("New2", all[1].Name);
        Assert.Equal("n1", all[0].Id);
        Assert.Equal("n2", all[1].Id);
    }

    [Fact]
    public async Task BuildFullPath_DeepNesting_ReturnsCorrectPath()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var root = await repo.AddAsync("Root", null, 0);
        var child = await repo.AddAsync("Child", root.Id, 1);
        var grandchild = await repo.AddAsync("GrandChild", child.Id, 2);

        var result = await repo.GetByIdAsync(grandchild.Id);

        Assert.NotNull(result);
        Assert.Equal("Root > Child > GrandChild", result.FullPath);
    }

    [Fact]
    public async Task GetFamilyCountAsync_ReturnsCorrectCount()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var category = await repo.AddAsync("Pipes", null, 0);

        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < 3; i++)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, category_id, content_status, created_at_utc, updated_at_utc)
                VALUES (@id, @name, @norm, @catId, 'Active', @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", $"item{i}"));
            cmd.Parameters.Add(new SqliteParameter("@name", $"Item {i}"));
            cmd.Parameters.Add(new SqliteParameter("@norm", $"item {i}"));
            cmd.Parameters.Add(new SqliteParameter("@catId", category.Id));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        var count = await repo.GetFamilyCountAsync(category.Id);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task GetAllFamilyCountsAsync_ReturnsCorrectCounts()
    {
        using var fixture = await CreateAndMigrate();
        var repo = new LocalCategoryRepository(fixture.GetDatabase());

        var cat1 = await repo.AddAsync("Pipes", null, 0);
        var cat2 = await repo.AddAsync("Fittings", null, 1);

        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        for (var i = 0; i < 2; i++)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, category_id, content_status, created_at_utc, updated_at_utc)
                VALUES (@id, @name, @norm, @catId, 'Active', @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", $"item_cat1_{i}"));
            cmd.Parameters.Add(new SqliteParameter("@name", $"Pipes Item {i}"));
            cmd.Parameters.Add(new SqliteParameter("@norm", $"pipes item {i}"));
            cmd.Parameters.Add(new SqliteParameter("@catId", cat1.Id));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, category_id, content_status, created_at_utc, updated_at_utc)
                VALUES ('item_cat2_0', 'Fitting A', 'fitting a', @catId, 'Active', @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@catId", cat2.Id));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        var counts = await repo.GetAllFamilyCountsAsync();

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[cat1.Id]);
        Assert.Equal(1, counts[cat2.Id]);
    }
}
