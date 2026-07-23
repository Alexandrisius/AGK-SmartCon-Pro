using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed class LocalCatalogMigrator : ILocalCatalogMigrator
{
    private readonly LocalCatalogDatabase _database;

    public LocalCatalogMigrator(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_database.GetDatabaseRoot());

        using var connection = _database.CreateWritableConnection();
        await connection.OpenAsync(ct);

        using (var foreignKeysCmd = connection.CreateCommand())
        {
            foreignKeysCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await foreignKeysCmd.ExecuteNonQueryAsync(ct);
        }

        using (var tableCmd = connection.CreateCommand())
        {
            tableCmd.CommandText = FamilyCatalogSql.CreateTables;
            await tableCmd.ExecuteNonQueryAsync(ct);
        }

        using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.CommandText = FamilyCatalogSql.CreateIndexes;
            await indexCmd.ExecuteNonQueryAsync(ct);
        }

        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = """
                INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '1')
                """;
            await versionCmd.ExecuteNonQueryAsync(ct);
        }

        var initialVersion = await GetSchemaVersionAsync(connection, ct);
        if (initialVersion < 23)
        {
            SmartConLogger.Info($"Schema migration starting: current=v{initialVersion}, target=v23");
        }

        await RunMigrationAsync(connection, 2, MigrateV2Async, ct);
        await RunMigrationAsync(connection, 3, MigrateV3Async, ct);
        await RunMigrationAsync(connection, 4, MigrateV4Async, ct);
        await RunMigrationAsync(connection, 5, MigrateV5Async, ct);
        await RunMigrationAsync(connection, 6, MigrateV6Async, ct);
        await RunMigrationAsync(connection, 7, MigrateV7Async, ct);
        await RunMigrationAsync(connection, 9, MigrateV9Async, ct);
        await RunMigrationAsync(connection, 10, MigrateV10Async, ct);
        await RunMigrationAsync(connection, 11, MigrateV11Async, ct);
        await RunMigrationAsync(connection, 12, MigrateV12Async, ct);
        await RunMigrationAsync(connection, 13, MigrateV13Async, ct);
        await RunMigrationAsync(connection, 14, MigrateV14Async, ct);
        // V15/V17/V18 recreate tables: the family_types rebuilds DROP the
        // parent of extracted_attribute_values — they must run under the
        // FK-off rebuild recipe or the CASCADE wipes attribute values.
        await RunRebuildMigrationAsync(connection, 15, MigrateV15Async, ct);
        await RunMigrationAsync(connection, 16, MigrateV16Async, ct);
        await RunRebuildMigrationAsync(connection, 17, MigrateV17Async, ct);
        await RunRebuildMigrationAsync(connection, 18, MigrateV18Async, ct);
        await RunMigrationAsync(connection, 19, MigrateV19Async, ct);
        await RunMigrationAsync(connection, 20, MigrateV20Async, ct);
        await RunMigrationAsync(connection, 21, MigrateV21Async, ct);
        await RunMigrationAsync(connection, 22, MigrateV22Async, ct);
        await RunMigrationAsync(connection, 23, MigrateV23Async, ct);

        // V8 may need to recreate extracted_attribute_values; disable FK enforcement during the swap.
        try
        {
            using var fkOff = connection.CreateCommand();
            fkOff.CommandText = "PRAGMA foreign_keys=OFF;";
            await fkOff.ExecuteNonQueryAsync(ct);

            await MigrateV8Async(connection, ct);
        }
        finally
        {
            using var fkOn = connection.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys=ON;";
            await fkOn.ExecuteNonQueryAsync(ct);
        }

        await EnsureCriticalColumnsAsync(connection, ct);
    }

    private static async Task RunMigrationAsync(
        SqliteConnection connection,
        int version,
        Func<SqliteConnection, CancellationToken, Task> migration,
        CancellationToken ct)
    {
        try
        {
            await migration(connection, ct);
        }
        catch (Exception ex)
        {
            // Fail-fast is correct (a half-migrated schema is worse), but the
            // log must name the failing migration — otherwise a truncated
            // user log leaves zero chance to find the culprit.
            SmartConLogger.Error(
                $"Schema migration v{version} FAILED: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: пришлите smartcon.log разработчику; база не повреждена — миграция откачена транзакцией]");
            throw;
        }
    }

    private static async Task LogForeignKeyViolationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            var violations = new List<string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_key_check";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    violations.Add(
                        $"{reader.GetString(0)}.rowid={reader.GetValue(1)} → {reader.GetString(2)}({reader.GetValue(3)})");
                    if (violations.Count >= 10) break;
                }
            }

            if (violations.Count > 0)
            {
                SmartConLogger.Warn(
                    $"foreign_key_check found {violations.Count}+ orphan rows after schema migrations: " +
                    $"{string.Join("; ", violations)} [Action: пришлите smartcon.log разработчику; " +
                    $"записи-«сироты» будут удалены при следующей rebuild-миграции]");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"foreign_key_check failed: {ex.Message} [Action: диагностика пропущена, на работу не влияет]");
        }
    }

    private static async Task MigrateV2Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 2) return;

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '2' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV3Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 3) return;

        if (!await ColumnExistsAsync(connection, "catalog_items", "category_id", ct))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = FamilyCatalogSql.MigrateV3AddCategoryIdColumn;
            await alterCmd.ExecuteNonQueryAsync(ct);
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '3' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV4Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 4) return;

        if (!await TableExistsAsync(connection, "family_types", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyTypes;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateFamilyTypesIndexes;
        await idxCmd.ExecuteNonQueryAsync(ct);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '4' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV5Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 5) return;

        if (!await TableExistsAsync(connection, "attribute_presets", ct))
        {
            using var createPresetsCmd = connection.CreateCommand();
            createPresetsCmd.CommandText = FamilyCatalogSql.CreateAttributePresets;
            await createPresetsCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "attribute_preset_parameters", ct))
        {
            using var createParamsCmd = connection.CreateCommand();
            createParamsCmd.CommandText = FamilyCatalogSql.CreateAttributePresetParameters;
            await createParamsCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_assets", "is_primary", ct))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = FamilyCatalogSql.MigrateV5AddIsPrimaryColumn;
            await alterCmd.ExecuteNonQueryAsync(ct);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateAttributePresetsIndexes;
        await idxCmd.ExecuteNonQueryAsync(ct);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '5' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV6Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 6) return;

        if (!await TableExistsAsync(connection, "attribute_definitions", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateAttributeDefinitions;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "category_attribute_bindings", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateCategoryAttributeBindings;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_data_import_runs", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyDataImportRuns;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "extracted_attribute_values", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateExtractedAttributeValues;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "version_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV6FamilyTypesAddColumns;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateV6Indexes;
        await idxCmd.ExecuteNonQueryAsync(ct);

        await MigrateV6LegacyPresetsAsync(connection, ct);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '6' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV6LegacyPresetsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var presetExists = await TableExistsAsync(connection, "attribute_presets", ct);
        if (!presetExists) return;

        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM attribute_presets";
            var count = (long)(await countCmd.ExecuteScalarAsync(ct) ?? 0L);
            if (count == 0) return;
        }

        using var tx = connection.BeginTransaction();
        try
        {
            using (var readerCmd = connection.CreateCommand())
            {
                readerCmd.CommandText = "SELECT p.id, p.category_id, pp.parameter_name, pp.sort_order FROM attribute_presets p LEFT JOIN attribute_preset_parameters pp ON p.id = pp.preset_id ORDER BY p.id, pp.sort_order";
                using var reader = await readerCmd.ExecuteReaderAsync(ct);

                var paramMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var bindings = new List<(string CategoryId, string AttributeId, int SortOrder)>();

                while (await reader.ReadAsync(ct))
                {
                    var categoryId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var paramName = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var sortOrder = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);

                    if (paramName is null) continue;

                    if (!paramMap.TryGetValue(paramName, out var attrId))
                    {
                        attrId = Guid.NewGuid().ToString();
                        paramMap[paramName] = attrId;

                        using var insertAttr = connection.CreateCommand();
                        insertAttr.CommandText = "INSERT OR IGNORE INTO attribute_definitions (id, name, is_active, created_at_utc) VALUES (@id, @name, 1, @createdAt)";
                        insertAttr.Parameters.Add(new SqliteParameter("@id", attrId));
                        insertAttr.Parameters.Add(new SqliteParameter("@name", paramName));
                        insertAttr.Parameters.Add(new SqliteParameter("@createdAt", DateTimeOffset.UtcNow.ToString("o")));
                        await insertAttr.ExecuteNonQueryAsync(ct);

                        using var getIdCmd = connection.CreateCommand();
                        getIdCmd.CommandText = "SELECT id FROM attribute_definitions WHERE name = @name COLLATE NOCASE";
                        getIdCmd.Parameters.Add(new SqliteParameter("@name", paramName));
                        var existingId = await getIdCmd.ExecuteScalarAsync(ct);
                        if (existingId is not null)
                            paramMap[paramName] = existingId.ToString()!;
                    }

                    if (categoryId is not null)
                        bindings.Add((categoryId, paramMap[paramName], sortOrder));
                }

                foreach (var (catId, attrId, sortOrder) in bindings)
                {
                    using var insertBinding = connection.CreateCommand();
                    insertBinding.CommandText = "INSERT OR IGNORE INTO category_attribute_bindings (id, category_id, attribute_id, sort_order, is_enabled) VALUES (@id, @catId, @attrId, @sort, 1)";
                    insertBinding.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
                    insertBinding.Parameters.Add(new SqliteParameter("@catId", catId));
                    insertBinding.Parameters.Add(new SqliteParameter("@attrId", attrId));
                    insertBinding.Parameters.Add(new SqliteParameter("@sort", sortOrder));
                    await insertBinding.ExecuteNonQueryAsync(ct);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
        }
    }

    private static async Task MigrateV7Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 7) return;

        if (!await TableExistsAsync(connection, "db_users", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateDbUsers;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateDbUsersIndexes;
        await idxCmd.ExecuteNonQueryAsync(ct);

        if (!await ColumnExistsAsync(connection, "database_meta", "owner_identity", ct))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = FamilyCatalogSql.MigrateV7AddOwnerIdentity;
            await alterCmd.ExecuteNonQueryAsync(ct);
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '7' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV9Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 9) return;

        if (!await ColumnExistsAsync(connection, "project_usage", "loaded_version_label", ct))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = FamilyCatalogSql.MigrateV9AddLoadedVersionLabel;
            await alterCmd.ExecuteNonQueryAsync(ct);
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '9' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV10Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 10) return;

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.MigrateV10AddFamilyTypesNameIndex;
        await idxCmd.ExecuteNonQueryAsync(ct);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '10' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV11Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 11) return;

        if (!await ColumnExistsAsync(connection, "catalog_items", "family_source", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable'";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN revit_category TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "type_unique_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE family_types ADD COLUMN type_unique_id TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateV11Indexes;
        await idxCmd.ExecuteNonQueryAsync(ct);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '11' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV12Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 12) return;

        // 1. Drop the project_usage lookup index (Phase 23 era).
        using (var dropIdxCmd = connection.CreateCommand())
        {
            dropIdxCmd.CommandText = FamilyCatalogSql.MigrateV12DropProjectUsageIndex;
            await dropIdxCmd.ExecuteNonQueryAsync(ct);
        }

        // 2. Drop the project_usage table (SSOT is now ExtensibleStorage on .rfa, ADR-030).
        using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.CommandText = FamilyCatalogSql.MigrateV12DropProjectUsageTable;
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Bump schema version.
        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '12' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateV13Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 13) return;

        if (!await TableExistsAsync(connection, "family_nested_shared_families", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyNestedSharedFamilies;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using (var idxCmd = connection.CreateCommand())
        {
            idxCmd.CommandText = FamilyCatalogSql.CreateNestedSharedFamiliesIndexes;
            await idxCmd.ExecuteNonQueryAsync(ct);
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '13' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// v2.0.0 migration v14: drop sha256/size_bytes columns. SQLite 3.35+
    /// supports ALTER TABLE DROP COLUMN. We drop each column inside a single
    /// BEGIN IMMEDIATE transaction so partial state is rolled back on
    /// failure. Indexes on dropped columns are auto-removed by SQLite.
    /// </summary>
    private static async Task MigrateV14Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 14) return;

        using var tx = connection.BeginTransaction();
        try
        {
            // Idempotent index drops first. Even if the columns are already
            // gone (idempotent retry), the DROP INDEX IF EXISTS won't fail.
            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.CommandText = FamilyCatalogSql.MigrateV14DropSha256Indexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            // Drop columns. SQLite allows DROP COLUMN only if the column
            // exists and is not referenced by FK / PK. None of our columns
            // are PK or FK targets, so this is safe.
            if (await ColumnExistsAsync(connection, "family_files", "size_bytes", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE family_files DROP COLUMN size_bytes";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (await ColumnExistsAsync(connection, "family_files", "sha256", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE family_files DROP COLUMN sha256";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (await ColumnExistsAsync(connection, "catalog_versions", "sha256", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_versions DROP COLUMN sha256";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (await ColumnExistsAsync(connection, "family_data_import_runs", "source_sha256", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE family_data_import_runs DROP COLUMN source_sha256";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "UPDATE schema_info SET value = '14' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v14: dropped sha256/size_bytes columns");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task MigrateV8Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 8) return;

        if (await IsColumnNotNullAsync(connection, "extracted_attribute_values", "attribute_id", ct))
        {
            using var recreateCmd = connection.CreateCommand();
            recreateCmd.CommandText = FamilyCatalogSql.MigrateV8RecreateExtractedAttributeValues;
            await recreateCmd.ExecuteNonQueryAsync(ct);
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '8' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Runs a table-rebuild migration under the official SQLite recipe:
    /// PRAGMA foreign_keys=OFF BEFORE the transaction (the pragma is a no-op
    /// inside one) so DROP TABLE does not fire ON DELETE CASCADE on child
    /// tables (family_types rebuild would otherwise wipe
    /// extracted_attribute_values.type_id rows), then foreign_key_check for
    /// diagnostics, then FK enforcement back on.
    /// </summary>
    private static async Task RunRebuildMigrationAsync(
        SqliteConnection connection,
        int version,
        Func<SqliteConnection, CancellationToken, Task> migration,
        CancellationToken ct)
    {
        // Skip cheaply when the migration is already applied — the FK-off
        // dance and especially foreign_key_check (full-database scan) must
        // NOT run on every database connect/switch.
        if (await GetSchemaVersionAsync(connection, ct) >= version)
        {
            await migration(connection, ct);
            return;
        }

        using (var fkOff = connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys=OFF";
            await fkOff.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await RunMigrationAsync(connection, version, migration, ct);
        }
        finally
        {
            try
            {
                await LogForeignKeyViolationsAsync(connection, ct);
            }
            finally
            {
                using var fkOn = connection.CreateCommand();
                fkOn.CommandText = "PRAGMA foreign_keys=ON";
                await fkOn.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>
    /// v2.0.0 (ADR-036) migration v15: add FOREIGN KEY (type_id) → family_types(id)
    /// ON DELETE CASCADE on extracted_attribute_values. Recreates the table to
    /// add the constraint (SQLite limitation). Pre-existing orphan rows are
    /// cleaned up inside the SQL constant.
    /// </summary>
    private static async Task MigrateV15Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 15) return;

        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV15AddAttributeValuesForeignKey;
            await cmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "UPDATE schema_info SET value = '15' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v15: added FK on extracted_attribute_values.type_id (ON DELETE CASCADE)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// v2.1.0 migration v16: add content_hash and hash_format_version
    /// columns to catalog_items and catalog_versions for content-fingerprint
    /// deduplication. Additive only — no breaking changes. Idempotent:
    /// each ALTER TABLE is guarded by a column-existence check.
    /// </summary>
    private static async Task MigrateV16Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 16) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_items", "content_hash", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN content_hash TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await ColumnExistsAsync(connection, "catalog_items", "hash_format_version", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN hash_format_version INTEGER";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await ColumnExistsAsync(connection, "catalog_versions", "content_hash", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN content_hash TEXT";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await ColumnExistsAsync(connection, "catalog_versions", "hash_format_version", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN hash_format_version INTEGER";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = FamilyCatalogSql.CreateV16Indexes;
            await idxCmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "UPDATE schema_info SET value = '16' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v16: added content_hash columns and indexes");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// v2.0.0 (ADR-041) migration v17: add FOREIGN KEY (version_id) REFERENCES
    /// catalog_versions(id) ON DELETE CASCADE on family_types and
    /// extracted_attribute_values. Also adds the composite index
    /// (catalog_item_id, version_label) on catalog_versions for fast
    /// GetVersionByLabelAsync / SetActiveVersionAsync lookups.
    ///
    /// Recreate-and-copy pattern (same as V15) because SQLite does not support
    /// ALTER TABLE ADD CONSTRAINT. Each table is recreated inside a single
    /// transaction with orphan-row cleanup before the copy.
    ///
    /// Order matters: family_types is recreated first because
    /// extracted_attribute_values has FK (type_id) → family_types(id).
    /// </summary>
    private static async Task MigrateV17Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 17) return;

        using var tx = connection.BeginTransaction();
        try
        {
            // Step 1: family_types — recreate with FK (version_id) ON DELETE CASCADE
            using (var ftCmd = connection.CreateCommand())
            {
                ftCmd.Transaction = tx;
                ftCmd.CommandText = FamilyCatalogSql.MigrateV17RecreateFamilyTypesWithVersionFk;
                await ftCmd.ExecuteNonQueryAsync(ct);
            }

            // Step 2: extracted_attribute_values — recreate with FK (version_id) ON DELETE CASCADE
            using (var eavCmd = connection.CreateCommand())
            {
                eavCmd.Transaction = tx;
                eavCmd.CommandText = FamilyCatalogSql.MigrateV17RecreateExtractedAttributeValuesWithVersionFk;
                await eavCmd.ExecuteNonQueryAsync(ct);
            }

            // Step 3: composite index on catalog_versions for ByLabel lookups
            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateV17Indexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '17' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v17: added FK on family_types.version_id and extracted_attribute_values.version_id (ON DELETE CASCADE)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// v2.1.0 (ADR-041 rev #2) migration v18: change the UNIQUE constraint
    /// on family_types from (catalog_item_id, type_name) to
    /// (catalog_item_id, version_id, type_name) so a type name can coexist
    /// across multiple versions of the same catalog item. This is the
    /// prerequisite for per-version type storage and version-scoped DELETE
    /// in SyncTypesAsync: without it, INSERT for a new version with the
    /// same type name as an existing version hits ON CONFLICT and silently
    /// reassigns family_types.version_id to the new version, destroying the
    /// previous version's type rows.
    ///
    /// Recreate-and-copy pattern (same as V15/V17). Idempotent: if the
    /// table already has the new UNIQUE, the recreate is a no-op data copy.
    /// </summary>
    private static async Task MigrateV18Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 18) return;

        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = FamilyCatalogSql.MigrateV18RecreateFamilyTypesPerVersionUnique;
            await cmd.ExecuteNonQueryAsync(ct);

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '18' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v18: changed family_types UNIQUE to (catalog_item_id, version_id, type_name) for per-version type storage");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V19: adds <c>published_by TEXT</c> column to <c>catalog_versions</c>
    /// to track which Revit user published each version. Simple ALTER TABLE
    /// ADD COLUMN — no recreate needed. ADR-041 rev #5.
    /// </summary>
    private static async Task MigrateV19Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 19) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_versions", "published_by", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV19AddPublishedByColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '19' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v19: added published_by column to catalog_versions for per-version author tracking");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V20 (ADR-045 / #119): adds <c>base_type INTEGER NOT NULL DEFAULT 0</c>
    /// column to <c>database_meta</c>. <c>0</c> = General (default for
    /// existing databases that predate #119), <c>1</c> = Project. The base
    /// type itself lives in <c>registry.json</c> as the source of truth
    /// (decision A1); this column is a convenience cache for RBAC and other
    /// consumers that read the catalog without touching the registry. Simple
    /// ALTER TABLE ADD COLUMN — no recreate needed. Existing rows get 0
    /// (General) on account of the DEFAULT clause.
    /// </summary>
    private static async Task MigrateV20Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 20) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "database_meta", "base_type", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV20AddBaseTypeColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '20' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v20: added base_type column to database_meta (General=0 default) — project-base binding cache for #119");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V21 (#119): adds <c>project_binding_json</c> column to <c>database_meta</c>.
    /// Project base binding is persisted inside the catalog database so that
    /// disconnecting and later reconnecting a project database restores its
    /// <see cref="BaseType.Project"/> kind and binding rules.
    /// </summary>
    private static async Task MigrateV21Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 21) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "database_meta", "project_binding_json", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV21AddProjectBindingColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '21' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v21: added project_binding_json column to database_meta — binding survives disconnect/reconnect");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V22 (ADR-055): family-facts subsystem — adds
    /// <c>catalog_items.revit_category_id INTEGER</c> (BuiltInCategory
    /// ordinal for rule-registry matching) and the <c>family_facts</c>
    /// table (one row per catalog item + fact). Additive only; the rows
    /// are backfilled by the <c>family-facts-v1</c> actualization task.
    /// </summary>
    private static async Task MigrateV22Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 22) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category_id", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV22AddRevitCategoryIdColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (!await TableExistsAsync(connection, "family_facts", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.CreateFamilyFacts;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var idxCmd = connection.CreateCommand())
            {
                idxCmd.Transaction = tx;
                idxCmd.CommandText = FamilyCatalogSql.CreateFamilyFactsIndexes;
                await idxCmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '22' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v22: added revit_category_id column and family_facts table (family-facts subsystem, ADR-055)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// V23 (#157): adds <c>catalog_versions.glb_state INTEGER</c> — terminal
    /// "no extractable geometry" marker for the glb-v1 task (-1). Without
    /// it, families that legitimately have no 3D (2D/annotation symbols)
    /// stayed pending forever (eternal amber dot).
    /// </summary>
    private static async Task MigrateV23Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 23) return;

        using var tx = connection.BeginTransaction();
        try
        {
            if (!await ColumnExistsAsync(connection, "catalog_versions", "glb_state", ct))
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = FamilyCatalogSql.MigrateV23AddGlbStateColumn;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using var versionCmd = connection.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "UPDATE schema_info SET value = '23' WHERE key = 'schema_version'";
            await versionCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            SmartConLogger.Info("Migration v23: added glb_state column to catalog_versions (terminal no-geometry marker for glb-v1, #157)");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task EnsureCriticalColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!await ColumnExistsAsync(connection, "family_assets", "is_primary", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV5AddIsPrimaryColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "attribute_definitions", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateAttributeDefinitions;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else if (!await ColumnExistsAsync(connection, "attribute_definitions", "group_name", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE attribute_definitions ADD COLUMN group_name TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "category_attribute_bindings", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateCategoryAttributeBindings;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_data_import_runs", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyDataImportRuns;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "extracted_attribute_values", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateExtractedAttributeValues;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else if (await IsColumnNotNullAsync(connection, "extracted_attribute_values", "attribute_id", ct))
        {
            using var recreateCmd = connection.CreateCommand();
            recreateCmd.CommandText = FamilyCatalogSql.MigrateV8RecreateExtractedAttributeValues;
            await recreateCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "version_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV6FamilyTypesAddColumns;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "db_users", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateDbUsers;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "database_meta", "owner_identity", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV7AddOwnerIdentity;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "database_meta", "base_type", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV20AddBaseTypeColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "family_source", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable'";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN revit_category TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV22AddRevitCategoryIdColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_facts", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyFacts;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var factsIdxCmd = connection.CreateCommand())
        {
            factsIdxCmd.CommandText = FamilyCatalogSql.CreateFamilyFactsIndexes;
            await factsIdxCmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "family_types", "type_unique_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE family_types ADD COLUMN type_unique_id TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await TableExistsAsync(connection, "family_nested_shared_families", ct))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyNestedSharedFamilies;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        // Indexes are idempotent (CREATE INDEX IF NOT EXISTS). Always attempt
        // them so a partial migration (table created, indexes failed) is
        // healed on next launch.
        using (var idxCmd = connection.CreateCommand())
        {
            idxCmd.CommandText = FamilyCatalogSql.CreateNestedSharedFamiliesIndexes;
            await idxCmd.ExecuteNonQueryAsync(ct);
        }

        // v16 content_hash columns — ensure they exist even if migration
        // sequence was interrupted.
        if (!await ColumnExistsAsync(connection, "catalog_items", "content_hash", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN content_hash TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_items", "hash_format_version", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN hash_format_version INTEGER";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_versions", "content_hash", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN content_hash TEXT";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_versions", "hash_format_version", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE catalog_versions ADD COLUMN hash_format_version INTEGER";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (!await ColumnExistsAsync(connection, "catalog_versions", "glb_state", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV23AddGlbStateColumn;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var v16IdxCmd = connection.CreateCommand())
        {
            v16IdxCmd.CommandText = FamilyCatalogSql.CreateV16Indexes;
            await v16IdxCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return int.TryParse(result?.ToString(), out var v) ? v : 1;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column";
        cmd.Parameters.Add(new SqliteParameter("@table", tableName));
        cmd.Parameters.Add(new SqliteParameter("@column", columnName));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.Add(new SqliteParameter("@name", tableName));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }

    private static async Task<bool> IsColumnNotNullAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tableName, ct))
            return false;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT \"notnull\" FROM pragma_table_info('{tableName}') WHERE name = '{columnName}'";
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is long l) return l == 1;
        if (result is int i) return i == 1;
        return false;
    }
}
