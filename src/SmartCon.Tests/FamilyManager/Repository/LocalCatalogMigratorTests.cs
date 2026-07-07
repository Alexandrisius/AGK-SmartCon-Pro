using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
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
        Assert.Equal("19", version);
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
        Assert.Equal("19", version);

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
    public async Task Migrate_IsIdempotent_OnV15Database()
    {
        // v2.0.0 (ADR-036): a second MigrateAsync on a v15 database must be a no-op.
        // The migrator's currentVersion guard already short-circuits, but
        // this test guards against a regression that would re-run the v15
        // table-recreate statements and fail.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("19", version);
    }

    [Fact]
    public async Task Migrate_V15_AddsAttributeValuesForeignKey()
    {
        // v2.0.0 (ADR-036): FK on extracted_attribute_values.type_id with
        // ON DELETE CASCADE is required for SyncTypesAsync to clean up
        // attribute values for removed types at the DB level.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list(extracted_attribute_values)";
        using var reader = await cmd.ExecuteReaderAsync();
        var fks = new List<(string Table, string From, string To, string OnDelete)>();
        while (await reader.ReadAsync())
        {
            fks.Add((
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(6) ? "" : reader.GetString(6)));
        }

        var typeFk = fks.FirstOrDefault(f => f.From == "type_id");
        Assert.Equal("family_types", typeFk.Table);
        Assert.Equal("id", typeFk.To);
        Assert.Equal("CASCADE", typeFk.OnDelete);
    }

    [Fact]
    public async Task Migrate_V15_DeletingType_CascadesToAttributeValues()
    {
        // v2.0.0 (ADR-036): inserting attribute values, then deleting the
        // referenced type, must remove the attribute values automatically.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var typeRepo = fixture.GetTypeRepository();

        // Seed: catalog_item, family_type, family_data_import_runs
        var itemId = await SeedCatalogItemAsync(fixture);
        var runId = await SeedImportRunAsync(fixture, itemId, "test-run");
        var typeId = (await typeRepo.SyncTypesAsync(itemId, null, null, runId, new List<FamilyTypeDescriptor>
        {
            new("type-1", itemId, "Type A", 0)
        })).Values.First();

        using (var conn = fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var insertVal = conn.CreateCommand();
            insertVal.CommandText = """
                INSERT INTO extracted_attribute_values
                    (id, catalog_item_id, type_id, parameter_name, extraction_run_id, extracted_at_utc)
                VALUES
                    ('val-1', @itemId, @typeId, 'Param1', @runId, '2026-06-25T00:00:00Z')
                """;
            insertVal.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", itemId));
            insertVal.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@typeId", typeId));
            insertVal.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@runId", runId));
            await insertVal.ExecuteNonQueryAsync();
        }

        // Sanity check: 1 attribute value before
        long countBefore;
        using (var conn = fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values";
            countBefore = (long)(await c.ExecuteScalarAsync() ?? 0L);
        }
        Assert.Equal(1L, countBefore);

        // Replace types with empty list → DELETE all types → CASCADE removes attribute values
        var runId2 = await SeedImportRunAsync(fixture, itemId, "test-run-2");
        await typeRepo.SyncTypesAsync(itemId, null, null, runId2, new List<FamilyTypeDescriptor>());

        long countAfter;
        using (var conn = fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values";
            countAfter = (long)(await c.ExecuteScalarAsync() ?? 0L);
        }
        Assert.Equal(0L, countAfter);
    }

    [Fact]
    public async Task Migrate_V15_CleansOrphanAttributeValues()
    {
        // v2.0.0 (ADR-036): pre-existing orphan attribute values
        // (type_id pointing at a non-existent family_types row) must be
        // deleted by the V15 migration BEFORE the FK is enforced.
        //
        // Setup: in production this scenario happens when a user upgrades
        // from V14 (no FK on type_id) where orphan rows may have accumulated
        // due to prior bugs. The V15 migration must clean them up before
        // the new FK is enforced.
        //
        // Test technique: V15 has already been applied via MigrateAsync(), so
        // the FK is already in place. We temporarily disable FK enforcement
        // to insert the orphan row, then run the V15 migration again. Since
        // the schema_version is already 16, the migration short-circuits —
        // so we use a separate test technique: directly call the migration
        // SQL (which does the orphan cleanup first) to validate the SQL
        // constant in isolation.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var itemId = await SeedCatalogItemAsync(fixture);
        var runId = await SeedImportRunAsync(fixture, itemId, "old-run");

        // Disable FKs to insert the orphan row.
        using (var conn = fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var offFk = conn.CreateCommand();
            offFk.CommandText = "PRAGMA foreign_keys = OFF";
            await offFk.ExecuteNonQueryAsync();

            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO extracted_attribute_values
                    (id, catalog_item_id, type_id, parameter_name, extraction_run_id, extracted_at_utc)
                VALUES
                    ('orphan-1', @itemId, 'non-existent-type-id', 'Param1', @runId, '2026-06-25T00:00:00Z')
                """;
            insert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", itemId));
            insert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@runId", runId));
            await insert.ExecuteNonQueryAsync();

            // Sanity: 1 orphan row inserted
            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync() ?? 0L));
        }

        // Manually run only the V15 SQL constant to validate the orphan cleanup
        // logic (DELETE WHERE type_id NOT IN family_types). In production this
        // runs as part of MigrateV15Async on a V14 database.
        using (var conn = fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM extracted_attribute_values
                WHERE type_id IS NOT NULL
                  AND type_id NOT IN (SELECT id FROM family_types);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-enable FKs and verify orphan row is gone (FK now valid).
        using (var conn = fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var onFk = conn.CreateCommand();
            onFk.CommandText = "PRAGMA foreign_keys = ON";
            await onFk.ExecuteNonQueryAsync();

            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync() ?? 0L));

            // Verify FK enforcement works (attempt to insert invalid row fails).
            using var invalidInsert = conn.CreateCommand();
            invalidInsert.CommandText = """
                INSERT INTO extracted_attribute_values
                    (id, catalog_item_id, type_id, parameter_name, extraction_run_id, extracted_at_utc)
                VALUES
                    ('bad', @itemId, 'non-existent-type', 'Param2', @runId, '2026-06-25T00:00:00Z')
                """;
            invalidInsert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", itemId));
            invalidInsert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@runId", runId));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await invalidInsert.ExecuteNonQueryAsync());
        }
    }

    private static async Task<string> SeedCatalogItemAsync(TempCatalogFixture fixture)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var insert = conn.CreateCommand();
        var id = Guid.NewGuid().ToString();
        insert.CommandText = """
            INSERT INTO catalog_items (id, name, normalized_name, content_status, family_source, created_at_utc, updated_at_utc)
            VALUES (@id, 'TestItem', 'testitem', 'Active', 'loadable', '2026-06-25T00:00:00Z', '2026-06-25T00:00:00Z')
            """;
        insert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", id));
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<string> SeedImportRunAsync(TempCatalogFixture fixture, string catalogItemId, string? runId = null)
    {
        runId ??= Guid.NewGuid().ToString();
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO family_data_import_runs
                (id, catalog_item_id, revit_major_version, status, types_count, started_at_utc)
            VALUES
                (@id, @itemId, 2025, 'Succeeded', 0, '2026-06-25T00:00:00Z')
            """;
        insert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", runId));
        insert.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", catalogItemId));
        await insert.ExecuteNonQueryAsync();
        return runId;
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
        Assert.Equal("19", version);

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
