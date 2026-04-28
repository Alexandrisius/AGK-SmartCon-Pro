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
        _database.EnsureDatabase();

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        // Disable WAL mode — ensures data is written directly to .db file,
        // preventing data loss when Revit process is terminated
        using var journalCmd = connection.CreateCommand();
        journalCmd.CommandText = "PRAGMA journal_mode=DELETE;";
        await journalCmd.ExecuteNonQueryAsync(ct);

        using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = FamilyCatalogSql.CreateTables;
        await tableCmd.ExecuteNonQueryAsync(ct);

        using var indexCmd = connection.CreateCommand();
        indexCmd.CommandText = FamilyCatalogSql.CreateIndexes;
        await indexCmd.ExecuteNonQueryAsync(ct);

        using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = """
            INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '1')
            """;
        await versionCmd.ExecuteNonQueryAsync(ct);
    }
}
