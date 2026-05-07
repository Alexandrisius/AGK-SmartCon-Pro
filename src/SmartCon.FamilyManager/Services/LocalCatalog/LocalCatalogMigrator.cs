using System.IO;
using Microsoft.Data.Sqlite;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalCatalogMigrator
{
    private readonly LocalCatalogDatabase _database;

    public LocalCatalogMigrator(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_database.GetDatabaseRoot());

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using (var journalCmd = connection.CreateCommand())
        {
            journalCmd.CommandText = "PRAGMA journal_mode=DELETE;";
            await journalCmd.ExecuteNonQueryAsync(ct);
        }

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

        await MigrateV2Async(connection, ct);
        await MigrateV3Async(connection, ct);
        await MigrateV4Async(connection, ct);
        await MigrateV5Async(connection, ct);
        await MigrateV6Async(connection, ct);

        await EnsureCriticalColumnsAsync(connection, ct);
    }

    public void Migrate()
    {
        Directory.CreateDirectory(_database.GetDatabaseRoot());

        using var connection = _database.CreateConnection();
        connection.Open();

        using (var journalCmd = connection.CreateCommand())
        {
            journalCmd.CommandText = "PRAGMA journal_mode=DELETE;";
            journalCmd.ExecuteNonQuery();
        }

        using (var foreignKeysCmd = connection.CreateCommand())
        {
            foreignKeysCmd.CommandText = "PRAGMA foreign_keys=ON;";
            foreignKeysCmd.ExecuteNonQuery();
        }

        using (var tableCmd = connection.CreateCommand())
        {
            tableCmd.CommandText = FamilyCatalogSql.CreateTables;
            tableCmd.ExecuteNonQuery();
        }

        using (var indexCmd = connection.CreateCommand())
        {
            indexCmd.CommandText = FamilyCatalogSql.CreateIndexes;
            indexCmd.ExecuteNonQuery();
        }

        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = """
                INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '1')
                """;
            versionCmd.ExecuteNonQuery();
        }

        MigrateV2(connection);
        MigrateV3(connection);
        MigrateV4(connection);
        MigrateV5(connection);
        MigrateV6(connection);

        EnsureCriticalColumns(connection);
    }

    private static async Task MigrateV2Async(SqliteConnection connection, CancellationToken ct)
    {
        var currentVersion = await GetSchemaVersionAsync(connection, ct);
        if (currentVersion >= 2) return;

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '2' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }

    private static void MigrateV2(SqliteConnection connection)
    {
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion >= 2) return;

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '2' WHERE key = 'schema_version'";
        versionCmd.ExecuteNonQuery();
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

    private static void MigrateV3(SqliteConnection connection)
    {
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion >= 3) return;

        if (!ColumnExists(connection, "catalog_items", "category_id"))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = FamilyCatalogSql.MigrateV3AddCategoryIdColumn;
            alterCmd.ExecuteNonQuery();
        }

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '3' WHERE key = 'schema_version'";
        versionCmd.ExecuteNonQuery();
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

    private static void MigrateV4(SqliteConnection connection)
    {
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion >= 4) return;

        if (!TableExists(connection, "family_types"))
        {
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText = FamilyCatalogSql.CreateFamilyTypes;
            createCmd.ExecuteNonQuery();
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateFamilyTypesIndexes;
        idxCmd.ExecuteNonQuery();

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '4' WHERE key = 'schema_version'";
        versionCmd.ExecuteNonQuery();
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

    private static void MigrateV5(SqliteConnection connection)
    {
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion >= 5) return;

        if (!TableExists(connection, "attribute_presets"))
        {
            using var createPresetsCmd = connection.CreateCommand();
            createPresetsCmd.CommandText = FamilyCatalogSql.CreateAttributePresets;
            createPresetsCmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "attribute_preset_parameters"))
        {
            using var createParamsCmd = connection.CreateCommand();
            createParamsCmd.CommandText = FamilyCatalogSql.CreateAttributePresetParameters;
            createParamsCmd.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "family_assets", "is_primary"))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = FamilyCatalogSql.MigrateV5AddIsPrimaryColumn;
            alterCmd.ExecuteNonQuery();
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateAttributePresetsIndexes;
        idxCmd.ExecuteNonQuery();

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '5' WHERE key = 'schema_version'";
        versionCmd.ExecuteNonQuery();
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

    private static void MigrateV6(SqliteConnection connection)
    {
        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion >= 6) return;

        if (!TableExists(connection, "attribute_definitions"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateAttributeDefinitions;
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "category_attribute_bindings"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateCategoryAttributeBindings;
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "family_data_import_runs"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyDataImportRuns;
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "extracted_attribute_values"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateExtractedAttributeValues;
            cmd.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "family_types", "version_id"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV6FamilyTypesAddColumns;
            cmd.ExecuteNonQuery();
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = FamilyCatalogSql.CreateV6Indexes;
        idxCmd.ExecuteNonQuery();

        MigrateV6LegacyPresets(connection);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "UPDATE schema_info SET value = '6' WHERE key = 'schema_version'";
        versionCmd.ExecuteNonQuery();
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

    private static void MigrateV6LegacyPresets(SqliteConnection connection)
    {
        var presetExists = TableExists(connection, "attribute_presets");
        if (!presetExists) return;

        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM attribute_presets";
            var count = (long)(countCmd.ExecuteScalar() ?? 0L);
            if (count == 0) return;
        }

        using var tx = connection.BeginTransaction();
        try
        {
            using (var readerCmd = connection.CreateCommand())
            {
                readerCmd.CommandText = "SELECT p.id, p.category_id, pp.parameter_name, pp.sort_order FROM attribute_presets p LEFT JOIN attribute_preset_parameters pp ON p.id = pp.preset_id ORDER BY p.id, pp.sort_order";
                using var reader = readerCmd.ExecuteReader();

                var paramMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var bindings = new List<(string CategoryId, string AttributeId, int SortOrder)>();

                while (reader.Read())
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
                        insertAttr.ExecuteNonQuery();

                        using var getIdCmd = connection.CreateCommand();
                        getIdCmd.CommandText = "SELECT id FROM attribute_definitions WHERE name = @name COLLATE NOCASE";
                        getIdCmd.Parameters.Add(new SqliteParameter("@name", paramName));
                        var existingId = getIdCmd.ExecuteScalar();
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
                    insertBinding.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
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

        if (!await ColumnExistsAsync(connection, "family_types", "version_id", ct))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV6FamilyTypesAddColumns;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static void EnsureCriticalColumns(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "family_assets", "is_primary"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV5AddIsPrimaryColumn;
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "attribute_definitions"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateAttributeDefinitions;
            cmd.ExecuteNonQuery();
        }
        else if (!ColumnExists(connection, "attribute_definitions", "group_name"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE attribute_definitions ADD COLUMN group_name TEXT";
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "category_attribute_bindings"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateCategoryAttributeBindings;
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "family_data_import_runs"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateFamilyDataImportRuns;
            cmd.ExecuteNonQuery();
        }

        if (!TableExists(connection, "extracted_attribute_values"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.CreateExtractedAttributeValues;
            cmd.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "family_types", "version_id"))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = FamilyCatalogSql.MigrateV6FamilyTypesAddColumns;
            cmd.ExecuteNonQuery();
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return int.TryParse(result?.ToString(), out var v) ? v : 1;
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version'";
        var result = cmd.ExecuteScalar();
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

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column";
        cmd.Parameters.Add(new SqliteParameter("@table", tableName));
        cmd.Parameters.Add(new SqliteParameter("@column", columnName));
        var result = cmd.ExecuteScalar();
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

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.Add(new SqliteParameter("@name", tableName));
        var result = cmd.ExecuteScalar();
        return result is long l && l > 0;
    }
}
