using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyDependencyRepositoryTests
{
    private static async Task<TempCatalogFixture> CreateAndMigrate()
    {
        var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        return fixture;
    }

    [Fact]
    public async Task ReplaceForVersion_ThenGetForCurrentVersion_ReturnsOrderedLinks()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent1");
        var childA = await SeedItemAsync(fixture, "childA", "Отвод A");
        var childB = await SeedItemAsync(fixture, "childB", "Тройник B");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childB, FamilyDependencyKind.Routing, "Тройник B:Стандарт", 0),
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Equal(2, links.Count);
        Assert.Equal(childB, links[0].ChildCatalogItemId);
        Assert.Equal(FamilyDependencyKind.Routing, links[0].Kind);
        Assert.Equal("Тройник B:Стандарт", links[0].PartName);
        Assert.Equal(0, links[0].Ordinal);
        Assert.Equal(childA, links[1].ChildCatalogItemId);
        Assert.Equal(1, links[1].Ordinal);
    }

    [Fact]
    public async Task ReplaceForVersion_CalledTwice_SecondReplacesFirst()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent2");
        var childA = await SeedItemAsync(fixture, "childA2", "Отвод A");
        var childB = await SeedItemAsync(fixture, "childB2", "Тройник B");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childB, FamilyDependencyKind.Routing, "Тройник B:Стандарт", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        var link = Assert.Single(links);
        Assert.Equal(childB, link.ChildCatalogItemId);
    }

    [Fact]
    public async Task ReplaceForVersion_DedupsSameChildAndKind()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent3");
        var childA = await SeedItemAsync(fixture, "childA3", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Малый", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        var link = Assert.Single(links);
        Assert.Equal("Отвод A:Стандарт", link.PartName);
    }

    [Fact]
    public async Task GetForCurrentVersion_IgnoresLinksOfNonCurrentVersion()
    {
        using var fixture = await CreateAndMigrate();
        var parentId = "parent4";
        // Item's CURRENT label is v2; the seeded version row is v1 → links
        // recorded on v1 must not surface through the current-version join.
        await SeedItemAsync(fixture, parentId, "Родитель", currentVersionLabel: "v2");
        var oldVersionId = await SeedVersionRowAsync(fixture, parentId, "v1");
        var childA = await SeedItemAsync(fixture, "childA4", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, oldVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Empty(links);
    }

    [Fact]
    public async Task GetForCurrentVersion_NoLinks_ReturnsEmpty()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, _) = await SeedParentWithCurrentVersionAsync(fixture, "parent5");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Empty(links);
    }

    [Fact]
    public async Task ReplaceForCurrentVersion_WritesOntoCurrentVersion()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, _) = await SeedParentWithCurrentVersionAsync(fixture, "parent7");
        var childA = await SeedItemAsync(fixture, "childA7", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var written = await sut.ReplaceForCurrentVersionAsync(parentId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        Assert.Equal(1, written);
        var links = await sut.GetForCurrentVersionAsync(parentId);
        var link = Assert.Single(links);
        Assert.Equal(childA, link.ChildCatalogItemId);
    }

    [Fact]
    public async Task ReplaceForCurrentVersion_NoCurrentVersion_ReturnsZero()
    {
        using var fixture = await CreateAndMigrate();
        // Item WITHOUT a current version label — links must not be written.
        await SeedItemAsync(fixture, "parent8", "Родитель");
        var childA = await SeedItemAsync(fixture, "childA8", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        var written = await sut.ReplaceForCurrentVersionAsync("parent8", new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });

        Assert.Equal(0, written);
    }

    [Fact]
    public async Task ReplaceForVersion_EmptyList_ClearsExistingLinks()
    {
        using var fixture = await CreateAndMigrate();
        var (parentId, parentVersionId) = await SeedParentWithCurrentVersionAsync(fixture, "parent6");
        var childA = await SeedItemAsync(fixture, "childA6", "Отвод A");
        var sut = new LocalFamilyDependencyRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(parentId, parentVersionId, new[]
        {
            new FamilyDependencyInfo(childA, FamilyDependencyKind.Routing, "Отвод A:Стандарт", 0),
        });
        await sut.ReplaceForVersionAsync(parentId, parentVersionId, Array.Empty<FamilyDependencyInfo>());

        var links = await sut.GetForCurrentVersionAsync(parentId);

        Assert.Empty(links);
    }

    private static async Task<(string ItemId, string VersionId)> SeedParentWithCurrentVersionAsync(
        TempCatalogFixture fixture, string itemId)
    {
        await SeedItemAsync(fixture, itemId, "Родитель", currentVersionLabel: "v1");
        var versionId = await SeedVersionRowAsync(fixture, itemId, "v1");
        return (itemId, versionId);
    }

    private static async Task<string> SeedItemAsync(
        TempCatalogFixture fixture, string itemId, string name, string? currentVersionLabel = null)
    {
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, description, category_name, manufacturer, content_status, current_version_label, published_by, created_at_utc, updated_at_utc)
            VALUES (@id, @name, @norm, NULL, NULL, NULL, 'Active', @currentLabel, NULL, '2026-08-06T00:00:00Z', '2026-08-06T00:00:00Z')
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", itemId));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@norm", name.ToUpperInvariant()));
        cmd.Parameters.Add(new SqliteParameter("@currentLabel",
            currentVersionLabel is null ? DBNull.Value : currentVersionLabel));
        await cmd.ExecuteNonQueryAsync();
        return itemId;
    }

    private static async Task<string> SeedVersionRowAsync(
        TempCatalogFixture fixture, string itemId, string versionLabel)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using (var fileCmd = connection.CreateCommand())
        {
            fileCmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, 'files/x.rvt', 'x.rvt', 2025, '2026-08-06T00:00:00Z')
                """;
            fileCmd.Parameters.Add(new SqliteParameter("@id", fileId));
            await fileCmd.ExecuteNonQueryAsync();
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
            VALUES (@id, @itemId, @fileId, @label, 2025, '2026-08-06T00:00:00Z')
            """;
        versionCmd.Parameters.Add(new SqliteParameter("@id", versionId));
        versionCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        versionCmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        versionCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        await versionCmd.ExecuteNonQueryAsync();
        return versionId;
    }
}
