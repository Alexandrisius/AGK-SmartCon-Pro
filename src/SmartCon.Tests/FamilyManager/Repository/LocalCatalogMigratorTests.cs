using Microsoft.Data.Sqlite;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCatalogMigratorTests
{
    [Fact]
    public async Task Migrate_CreatesAllTables()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Contains("schema_info", tables);
        Assert.Contains("catalog_items", tables);
        Assert.Contains("catalog_versions", tables);
        Assert.Contains("family_files", tables);
        Assert.Contains("family_assets", tables);
        Assert.Contains("catalog_tags", tables);
        Assert.Contains("project_usage", tables);
        Assert.Contains("database_meta", tables);
        Assert.Contains("categories", tables);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();

        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM catalog_items";
        var result = await cmd.ExecuteScalarAsync();
        var count = result is long l ? l : 0L;
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task Migrate_SetsSchemaVersion()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("3", version);
    }
}
