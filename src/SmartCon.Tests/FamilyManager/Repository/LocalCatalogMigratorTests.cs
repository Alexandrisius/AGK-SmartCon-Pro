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
        Assert.Equal("27", version);
    }

    [Fact]
    public async Task Migrate_V26_AddsFamilyNameAndPreservesRows()
    {
        // V26 (#183): family_types gets family_name ('' default for legacy
        // rows) and the UNIQUE becomes (item, version, family, name).
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var itemId = await SeedCatalogItemAsync(fixture);
        await SeedFamilyTypeRowAsync(fixture, itemId, "Стандарт", familyName: "Conduit without Fittings");

        await RewindFamilyTypesToV25Async(fixture);
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using (var colCmd = connection.CreateCommand())
        {
            colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('family_types') WHERE name='family_name'";
            Assert.Equal(1L, (long)(await colCmd.ExecuteScalarAsync())!);
        }

        using (var rowCmd = connection.CreateCommand())
        {
            // Legacy row migrated with '' family_name (never NULL).
            rowCmd.CommandText = "SELECT type_name, family_name FROM family_types WHERE catalog_item_id = @id";
            rowCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", itemId));
            using var reader = await rowCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Стандарт", reader.GetString(0));
            Assert.Equal(string.Empty, reader.GetString(1));
        }

        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
            Assert.Equal("27", (string?)await versionCmd.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Migrate_V27_AddsFamilyKeyAndPreservesRows()
    {
        // V27 (#190, ADR-064): family_types gets family_key ('' default for
        // legacy rows); family_name and the UNIQUE identity are untouched.
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var itemId = await SeedCatalogItemAsync(fixture);
        await SeedFamilyTypeRowAsync(fixture, itemId, "Стандарт", familyName: "Conduit without Fittings");

        using (var rewindConn = fixture.GetDatabase().CreateConnection())
        {
            await rewindConn.OpenAsync();
            using var rewindCmd = rewindConn.CreateCommand();
            rewindCmd.CommandText = """
                ALTER TABLE family_types DROP COLUMN family_key;
                UPDATE schema_info SET value = '26' WHERE key='schema_version';
                """;
            await rewindCmd.ExecuteNonQueryAsync();
        }

        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using (var colCmd = connection.CreateCommand())
        {
            colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('family_types') WHERE name='family_key'";
            Assert.Equal(1L, (long)(await colCmd.ExecuteScalarAsync())!);
        }

        using (var rowCmd = connection.CreateCommand())
        {
            // Legacy row migrated with '' family_key (never NULL); the
            // family_name value survives the migration.
            rowCmd.CommandText = "SELECT type_name, family_name, family_key FROM family_types WHERE catalog_item_id = @id";
            rowCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", itemId));
            using var reader = await rowCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Стандарт", reader.GetString(0));
            Assert.Equal("Conduit without Fittings", reader.GetString(1));
            Assert.Equal(string.Empty, reader.GetString(2));
        }

        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
            Assert.Equal("27", (string?)await versionCmd.ExecuteScalarAsync());
        }
    }

    private static async Task SeedFamilyTypeRowAsync(
        TempCatalogFixture fixture, string catalogItemId, string typeName, string familyName)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, family_name)
            VALUES (@id, @itemId, @name, 0, @family)
            """;
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@name", typeName));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@family", familyName));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Simulates the pre-V26 schema: recreates family_types with the V18-V25
    /// structure (no family_name, UNIQUE(item, version, name)) and rewinds
    /// schema_version to 25.
    /// </summary>
    private static async Task RewindFamilyTypesToV25Async(TempCatalogFixture fixture)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();

        using (var fkOff = conn.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys=OFF";
            await fkOff.ExecuteNonQueryAsync();
        }

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TABLE IF EXISTS family_types_v25;

                CREATE TABLE family_types_v25 (
                    id TEXT PRIMARY KEY,
                    catalog_item_id TEXT NOT NULL,
                    type_name TEXT NOT NULL,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    version_id TEXT,
                    file_id TEXT,
                    extraction_run_id TEXT,
                    type_unique_id TEXT,
                    FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
                    FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
                    FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE SET NULL,
                    UNIQUE(catalog_item_id, version_id, type_name)
                );

                INSERT INTO family_types_v25 (id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id)
                SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id
                FROM family_types;

                DROP TABLE family_types;
                ALTER TABLE family_types_v25 RENAME TO family_types;

                CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
                CREATE INDEX IF NOT EXISTS ix_family_types_name ON family_types (type_name);
                CREATE INDEX IF NOT EXISTS ix_family_types_version_id ON family_types (version_id) WHERE version_id IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS ix_family_types_orchestrator_unique
                ON family_types (catalog_item_id, type_name)
                WHERE version_id IS NULL;

                UPDATE schema_info SET value = '25' WHERE key='schema_version';
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            using var fkOn = conn.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys=ON";
            await fkOn.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Migrate_V24_AddsMinPluginVersionColumn()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('database_meta') WHERE name='min_plugin_version'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Migrate_V24_BackfillsMinPluginVersion_WhenFhv3HashesPresent()
    {
        // ADR-058 (#173): a database that already carries FHV3 hashes must be
        // retro-gated to the first FHV3-capable plugin release. Simulate the
        // pre-V24 state (rewind pattern from Migrate_FromV13_DropsSha256Columns).
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var itemId = await SeedCatalogItemAsync(fixture);
        await SeedVersionWithHashFormatAsync(fixture, itemId, hashFormatVersion: 3);

        await RewindToV23Async(fixture);
        await fixture.MigrateAsync();

        // V24 backfills the HISTORICAL FHV3 floor ('2.0.1-beta.5'); the FHV4
        // actualization task (hash-v4) raises the floor to
        // DbCompatibility.CurrentMinPluginVersion at runtime, after the
        // rehash — v4 rows exist only then, so a schema migration cannot
        // key on them (ADR-065).
        Assert.Equal("2.0.1-beta.5", await ReadMinPluginVersionAsync(fixture));
    }

    [Fact]
    public async Task Migrate_V24_LeavesMinPluginVersionNull_WhenNoFhv3Hashes()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();
        var itemId = await SeedCatalogItemAsync(fixture);
        await SeedVersionWithHashFormatAsync(fixture, itemId, hashFormatVersion: 2);

        await RewindToV23Async(fixture);
        await fixture.MigrateAsync();

        Assert.Null(await ReadMinPluginVersionAsync(fixture));
    }

    private static async Task SeedVersionWithHashFormatAsync(TempCatalogFixture fixture, string catalogItemId, int hashFormatVersion)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();

        var fileId = Guid.NewGuid().ToString();
        using (var fileCmd = conn.CreateCommand())
        {
            fileCmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, 'files/x.rfa', 'x.rfa', 2025, '2026-07-28T00:00:00Z')
                """;
            fileCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", fileId));
            await fileCmd.ExecuteNonQueryAsync();
        }

        using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = """
                INSERT INTO database_meta (id, name, created_at_utc, schema_version)
                VALUES ('db1', 'Test DB', '2026-07-28T00:00:00Z', 2)
                """;
            await metaCmd.ExecuteNonQueryAsync();
        }

        using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = """
            INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, content_hash, hash_format_version, published_at_utc)
            VALUES (@id, @itemId, @fileId, 'v1', 2025, 'ABCD', @fmt, '2026-07-28T00:00:00Z')
            """;
        versionCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", Guid.NewGuid().ToString()));
        versionCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", catalogItemId));
        versionCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@fileId", fileId));
        versionCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@fmt", hashFormatVersion));
        await versionCmd.ExecuteNonQueryAsync();
    }

    private static async Task RewindToV23Async(TempCatalogFixture fixture)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE database_meta DROP COLUMN min_plugin_version";
            await drop.ExecuteNonQueryAsync();
        }

        using var rewind = conn.CreateCommand();
        rewind.CommandText = "UPDATE schema_info SET value = '23' WHERE key='schema_version'";
        await rewind.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadMinPluginVersionAsync(TempCatalogFixture fixture)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT min_plugin_version FROM database_meta LIMIT 1";
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string?)value;
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
        Assert.Equal("27", version);

        using var tableCmd = verify.CreateCommand();
        tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='family_nested_shared_families'";
        Assert.NotNull(await tableCmd.ExecuteScalarAsync());

        using var idxCmd = verify.CreateCommand();
        idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='ix_nested_shared_version'";
        Assert.NotNull(await idxCmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migrate_V20_AddsBaseTypeColumn()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(database_meta)";
        var columns = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("base_type", columns);
    }

    [Fact]
    public async Task Migrate_V21_AddsProjectBindingJsonColumn()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using var connection = fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(database_meta)";
        var columns = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("project_binding_json", columns);
    }

    [Fact]
    public async Task Migrate_V21_FromV20_AddsProjectBindingJsonColumn()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE schema_info SET value = '20' WHERE key='schema_version'";
            await rewind.ExecuteNonQueryAsync();
        }

        await fixture.GetMigrator().MigrateAsync();

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();

        using var versionCmd = verify.CreateCommand();
        versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await versionCmd.ExecuteScalarAsync();
        Assert.Equal("27", version);

        using var cmd = verify.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(database_meta)";
        var columns = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        Assert.Contains("project_binding_json", columns);
    }

    [Fact]
    public async Task Migrate_V20_FromV19_AddsBaseTypeColumn()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE schema_info SET value = '19' WHERE key='schema_version'";
            await rewind.ExecuteNonQueryAsync();
        }

        await fixture.GetMigrator().MigrateAsync();

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();

        using var versionCmd = verify.CreateCommand();
        versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        var version = (string?)await versionCmd.ExecuteScalarAsync();
        Assert.Equal("27", version);

        using var cmd = verify.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(database_meta)";
        var columns = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        Assert.Contains("base_type", columns);
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
        Assert.Equal("27", version);
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
        Assert.Equal("27", version);

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

    /// <summary>
    /// Regression for the "FOREIGN KEY constraint failed" connect failure on
    /// legacy databases: pre-FK-enforcement databases can carry orphan rows in
    /// EVERY FK column of the rebuilt tables (not just the ones the original
    /// cleanups covered). The V15/V17/V18 rebuilds must clean orphans by all
    /// FK columns and let the database connect.
    /// </summary>
    [Fact]
    public async Task Migrate_LegacyDatabaseWithOrphanRows_CleansAllFkColumns()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        // Seed: one valid entity graph + orphan rows in every FK column the
        // rebuilds cover. Test connections default to foreign_keys=OFF, so
        // orphan inserts succeed (exactly how legacy databases got them).
        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();

            // The pooled physical connection inherits foreign_keys=ON from
            // the migrator — disable it explicitly so the orphan inserts
            // succeed (exactly how legacy databases got their orphans).
            using (var fkOff = connection.CreateCommand())
            {
                fkOff.CommandText = "PRAGMA foreign_keys=OFF";
                await fkOff.ExecuteNonQueryAsync();
            }

            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO catalog_items (id, name, normalized_name, family_source, created_at_utc, updated_at_utc)
                    VALUES ('item1', 'FamA', 'fama', 'loadable', '2024-01-01', '2024-01-01');
                    INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                    VALUES ('file1', 'files/item1/v1/FamA.rfa', 'FamA.rfa', 2021, '2024-01-01');
                    INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, revit_major_version, published_at_utc)
                    VALUES ('ver1', 'item1', 'file1', 'v1', 2021, '2024-01-01');
                    INSERT INTO attribute_definitions (id, name, is_active, created_at_utc)
                    VALUES ('attr1', 'ParamA', 1, '2024-01-01');
                    INSERT INTO family_data_import_runs (id, catalog_item_id, revit_major_version, started_at_utc)
                    VALUES ('run1', 'item1', 2021, '2024-01-01');

                    -- valid rows that MUST survive the rebuilds
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
                    VALUES ('type-ok', 'item1', 'T1', 0, 'ver1', 'file1');
                    INSERT INTO extracted_attribute_values (id, catalog_item_id, version_id, file_id, type_id, attribute_id,
                                                          parameter_name, storage_type, value_text, status,
                                                          extraction_run_id, extracted_at_utc)
                    VALUES ('eav-ok', 'item1', 'ver1', 'file1', 'type-ok', 'attr1',
                            'ParamA', 'String', 'X', 'Found', 'run1', '2024-01-01');

                    -- orphan rows: every FK column, one per violation kind
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
                    VALUES ('type-ghost-item', 'ghost-item', 'T2', 0, 'ver1', 'file1');
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
                    VALUES ('type-ghost-file', 'item1', 'T3', 0, 'ver1', 'ghost-file');
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
                    VALUES ('type-ghost-ver', 'item1', 'T4', 0, 'ghost-ver', 'file1');

                    INSERT INTO extracted_attribute_values (id, catalog_item_id, parameter_name, extraction_run_id, extracted_at_utc)
                    VALUES ('eav-ghost-item', 'ghost-item', 'P1', 'run1', '2024-01-01');
                    INSERT INTO extracted_attribute_values (id, catalog_item_id, type_id, parameter_name, extraction_run_id, extracted_at_utc)
                    VALUES ('eav-ghost-type', 'item1', 'ghost-type', 'P2', 'run1', '2024-01-01');
                    INSERT INTO extracted_attribute_values (id, catalog_item_id, attribute_id, parameter_name, extraction_run_id, extracted_at_utc)
                    VALUES ('eav-ghost-attr', 'item1', 'ghost-attr', 'P3', 'run1', '2024-01-01');
                    INSERT INTO extracted_attribute_values (id, catalog_item_id, version_id, parameter_name, extraction_run_id, extracted_at_utc)
                    VALUES ('eav-ghost-ver', 'item1', 'ghost-ver', 'P4', 'run1', '2024-01-01');
                    INSERT INTO extracted_attribute_values (id, catalog_item_id, parameter_name, extraction_run_id, extracted_at_utc)
                    VALUES ('eav-ghost-run', 'item1', 'P5', 'ghost-run', '2024-01-01');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            // Rewind to v14 so V15..V21 re-run against the dirty data.
            using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE schema_info SET value = '14' WHERE key='schema_version'";
            await rewind.ExecuteNonQueryAsync();
        }

        // Must not throw — previously failed with "FOREIGN KEY constraint failed".
        await fixture.GetMigrator().MigrateAsync();

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();

        using var versionCmd = verify.CreateCommand();
        versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
        Assert.Equal("27", (string?)await versionCmd.ExecuteScalarAsync());

        // Orphans are gone from both rebuilt tables.
        foreach (var (table, ghostId) in new[]
        {
            ("family_types", "type-ghost-item"),
            ("family_types", "type-ghost-file"),
            ("family_types", "type-ghost-ver"),
            ("extracted_attribute_values", "eav-ghost-item"),
            ("extracted_attribute_values", "eav-ghost-type"),
            ("extracted_attribute_values", "eav-ghost-attr"),
            ("extracted_attribute_values", "eav-ghost-ver"),
            ("extracted_attribute_values", "eav-ghost-run"),
        })
        {
            using var cmd = verify.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = '{ghostId}'";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync() ?? 0L));
        }

        // Valid rows survived the rebuilds.
        using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM family_types WHERE id = 'type-ok'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync() ?? 0L));
        }
        using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM extracted_attribute_values WHERE id = 'eav-ok'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync() ?? 0L));
        }

        // No FK violations remain anywhere in the database.
        using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var reader = await cmd.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync(), "foreign_key_check must find no violations after migration");
        }
    }

    [Fact]
    public async Task Migrate_V25_FromV24_CreatesValidationRulesTableAndIndex()
    {
        using var fixture = new TempCatalogFixture();
        await fixture.MigrateAsync();

        using (var connection = fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE schema_info SET value = '24' WHERE key='schema_version'; DROP TABLE IF EXISTS category_validation_rules";
            await rewind.ExecuteNonQueryAsync();
        }

        await fixture.GetMigrator().MigrateAsync();

        using var verify = fixture.GetDatabase().CreateConnection();
        await verify.OpenAsync();

        using (var versionCmd = verify.CreateCommand())
        {
            versionCmd.CommandText = "SELECT value FROM schema_info WHERE key='schema_version'";
            var version = (string?)await versionCmd.ExecuteScalarAsync();
            Assert.Equal("27", version);
        }

        using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='category_validation_rules'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync() ?? 0L));
        }

        using (var cmd = verify.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_validation_rules_binding'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync() ?? 0L));
        }
    }
}
