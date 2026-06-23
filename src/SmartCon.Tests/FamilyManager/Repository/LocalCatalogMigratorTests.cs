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

        using var connection = fixture.GetDatabase().CreateConnection();
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
        Assert.Contains("attribute_presets", tables);
        Assert.Contains("attribute_preset_parameters", tables);
        Assert.Contains("family_types", tables);
        Assert.Contains("attribute_definitions", tables);
        Assert.Contains("category_attribute_bindings", tables);
        Assert.Contains("family_data_import_runs", tables);
        Assert.Contains("extracted_attribute_values", tables);
        Assert.Contains("db_users", tables);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
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

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("13", version);
    }

    [Fact]
    public async Task Migrate_CreatesNestedSharedFamiliesTable()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='family_nested_shared_families'";
        var exists = await cmd.ExecuteScalarAsync();
        Assert.NotNull(exists);
    }

    [Fact]
    public async Task Migrate_ExistingV12Database_UpgradesToV13AndCreatesTable()
    {
        // Simulate an existing V12 database by running the full migration,
        // then manually rewinding the schema_version to 12 and dropping the
        // new table. The next migration must add the table and bump the
        // version without throwing.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE schema_info SET value = '12' WHERE key='schema_version'";
            await rewind.ExecuteNonQueryAsync();

            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE IF EXISTS family_nested_shared_families";
            await drop.ExecuteNonQueryAsync();

            using var dropIdx = connection.CreateCommand();
            dropIdx.CommandText = "DROP INDEX IF EXISTS ix_nested_shared_version";
            await dropIdx.ExecuteNonQueryAsync();
        }

        await fixture.GetMigrator().MigrateAsync();

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();
        using var versionCmd = verify.CreateCommand();
        versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await versionCmd.ExecuteScalarAsync();
        Assert.Equal("13", version);

        using var tableCmd = verify.CreateCommand();
        tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='family_nested_shared_families'";
        Assert.NotNull(await tableCmd.ExecuteScalarAsync());

        using var idxCmd = verify.CreateCommand();
        idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='ix_nested_shared_version'";
        Assert.NotNull(await idxCmd.ExecuteScalarAsync());
    }
}
