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
                INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '2')
                """;
            await versionCmd.ExecuteNonQueryAsync(ct);
        }
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
                INSERT OR IGNORE INTO schema_info (key, value) VALUES ('schema_version', '2')
                """;
            versionCmd.ExecuteNonQuery();
        }
    }
}
