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
        Assert.Equal("14", version);
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
    public async Task Migrate_ExistingV14Database_UpgradesToV14()
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
        Assert.Equal("14", version);

        using var tableCmd = verify.CreateCommand();
        tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='family_nested_shared_families'";
        Assert.NotNull(await tableCmd.ExecuteScalarAsync());

        using var idxCmd = verify.CreateCommand();
        idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='ix_nested_shared_version'";
        Assert.NotNull(await idxCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migrate_OnFreshDatabase_DoesNotCreateSha256Columns()
    {
        // v2.0.0: a brand-new v14 database must NOT contain the legacy
        // sha256 / size_bytes columns. These were used for content-based
        // deduplication, which the v2.0.0 pipeline no longer does.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        foreach (var table in new[] { "family_files", "catalog_versions", "family_data_import_runs" })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            var columns = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.DoesNotContain("sha256", columns);
            if (table == "family_files")
            {
                Assert.DoesNotContain("size_bytes", columns);
            }
            if (table == "family_data_import_runs")
            {
                Assert.DoesNotContain("source_sha256", columns);
            }
        }
    }

    [Fact]
    public async Task Migrate_IsIdempotent_OnV14Database()
    {
        // v2.0.0: a second MigrateAsync on a v14 database must be a no-op.
        // The migrator's currentVersion guard already short-circuits, but
        // this test guards against a regression that would re-run the v14
        // DROP COLUMN statements and fail (column does not exist).
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("14", version);
    }

    [Fact]
    public async Task Migrate_FromV13_DropsSha256Columns()
    {
        // v2.0.0: simulate a v13 database that still has the legacy
        // sha256 / size_bytes columns, then run migration and verify
        // the columns are gone after the v14 migration runs. The v14
        // migration uses ALTER TABLE DROP COLUMN (SQLite 3.35+, we ship
        // 3.46+ via Microsoft.Data.Sqlite 8.x).
        //
        // We rebuild the v13 shape by:
        //   1) Running the full migration (which lands at v14).
        //   2) Rewinding schema_version to 13 and recreating the legacy
        //      columns + indexes with default values, so the v14
        //      migration has something to drop.
        //   3) Running the migration again and asserting v14 state.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();

            using (var rewind = connection.CreateCommand())
            {
                rewind.CommandText = "UPDATE schema_info SET value = '13' WHERE key='schema_version'";
                await rewind.ExecuteNonQueryAsync();
            }

            // Add the legacy columns back so ALTER TABLE DROP COLUMN has
            // something to drop. Default values avoid any NOT NULL
            // violations; we never read these rows after the migration.
            using (var addCols = connection.CreateCommand())
            {
                addCols.CommandText = """
                    ALTER TABLE family_files ADD COLUMN size_bytes INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE family_files ADD COLUMN sha256 TEXT NOT NULL DEFAULT '';
                    ALTER TABLE catalog_versions ADD COLUMN sha256 TEXT NOT NULL DEFAULT '';
                    ALTER TABLE family_data_import_runs ADD COLUMN source_sha256 TEXT NOT NULL DEFAULT '';
                    CREATE INDEX IF NOT EXISTS ix_family_files_sha256 ON family_files (sha256);
                    CREATE INDEX IF NOT EXISTS ix_catalog_versions_sha256 ON catalog_versions (sha256);
                    """;
                await addCols.ExecuteNonQueryAsync();
            }
        }

        await fixture.GetMigrator().MigrateAsync();

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();

        using var versionCmd = verify.CreateCommand();
        versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await versionCmd.ExecuteScalarAsync();
        Assert.Equal("14", version);

        foreach (var (table, column) in new[]
        {
            ("family_files", "size_bytes"),
            ("family_files", "sha256"),
            ("catalog_versions", "sha256"),
            ("family_data_import_runs", "source_sha256")
        })
        {
            using var cmd = verify.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            var columns = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
            Assert.DoesNotContain(column, columns);
        }

        foreach (var index in new[] { "ix_family_files_sha256", "ix_catalog_versions_sha256" })
        {
            using var cmd = verify.CreateCommand();
            cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='index' AND name='{index}'";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Null(result);
        }
    }
}
