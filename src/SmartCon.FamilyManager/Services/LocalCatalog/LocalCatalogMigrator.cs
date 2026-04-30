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
                INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '4')
                """;
            await versionCmd.ExecuteNonQueryAsync(ct);
        }

        await MigrateV2Async(connection, ct);
        await MigrateV3Async(connection, ct);
        await MigrateV4Async(connection, ct);
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
                INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '4')
                """;
            versionCmd.ExecuteNonQuery();
        }

        MigrateV2(connection);
        MigrateV3(connection);
        MigrateV4(connection);
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
