using System.IO;
using Microsoft.Data.Sqlite;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalCatalogDatabase
{
    static LocalCatalogDatabase()
    {
        // Initialize SQLitePCL provider for .NET Framework compatibility
        SQLitePCL.Batteries.Init();
    }
    private readonly string _dbPath;
    private readonly string _connectionString;

    public LocalCatalogDatabase()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "SmartCon", "FamilyManager");
        _dbPath = Path.Combine(dir, "catalog.db");
        _connectionString = $"Data Source={_dbPath};Pooling=false";
    }

    public string ConnectionString => _connectionString;

    public SqliteConnection CreateConnection() => new(_connectionString);

    public void EnsureDatabase()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(dir);
    }

    public void Checkpoint()
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort checkpoint
        }
    }
}
