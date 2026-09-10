using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed partial class LocalCatalogMigrator : ILocalCatalogMigrator
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
        if (initialVersion < 37)
        {
            SmartConLogger.Info($"Schema migration starting: current=v{initialVersion}, target=v37");
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
        await RunMigrationAsync(connection, 24, MigrateV24Async, ct);
        await RunMigrationAsync(connection, 25, MigrateV25Async, ct);
        // V26 recreates family_types (parent of extracted_attribute_values)
        // — must run under the FK-off rebuild recipe like V15/V17/V18.
        await RunRebuildMigrationAsync(connection, 26, MigrateV26Async, ct);
        // V27 is a plain ADD COLUMN — no rebuild needed.
        await RunMigrationAsync(connection, 27, MigrateV27Async, ct);
        await RunMigrationAsync(connection, 28, MigrateV28Async, ct);
        await RunMigrationAsync(connection, 29, MigrateV29Async, ct);
        await RunMigrationAsync(connection, 30, MigrateV30Async, ct);
        await RunMigrationAsync(connection, 31, MigrateV31Async, ct);
        await RunMigrationAsync(connection, 32, MigrateV32Async, ct);
        await RunMigrationAsync(connection, 33, MigrateV33Async, ct);
        await RunMigrationAsync(connection, 34, MigrateV34Async, ct);
        await RunMigrationAsync(connection, 35, MigrateV35Async, ct);
        await RunMigrationAsync(connection, 36, MigrateV36Async, ct);
        await RunMigrationAsync(connection, 37, MigrateV37Async, ct);
        await RunMigrationAsync(connection, 38, MigrateV38Async, ct);

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
